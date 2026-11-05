using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OracleSessionHistory.Core.Data;
using OracleSessionHistory.Core.Entities;
using OracleSessionHistory.Core.Processes;

namespace OracleSessionHistory.Core.Services;

/// <summary>1 回分のプロセス情報取得の結果</summary>
public sealed record ProcessInfoOutcome(int Resolved, int Failed, int Skipped);

/// <summary>
/// 接続中のクライアントセッションについて、接続元サーバーのプロセス情報を取得してアプリ名を確定する（Batch から呼ばれる）。
/// セッション収集とは独立して動き、失敗してもセッションの接続・切断の判定には一切影響しない。
/// </summary>
public sealed class ProcessInfoService(
    IDbContextFactory<AppDbContext> dbFactory,
    ProcessInspectorResolver inspectors,
    HostBackoff backoff,
    ILogger<ProcessInfoService> logger)
{
    /// <summary>1 回に処理するセッション数の上限</summary>
    private const int BatchSize = 500;
    /// <summary>同時に問い合わせるホスト数</summary>
    private const int MaxParallelHosts = 4;
    private const int MaxErrorLength = 500;

    private sealed record HostResult(IReadOnlyList<ClientProcessInfo>? Processes, string? Error);

    public async Task<ProcessInfoOutcome> RunOnceAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var settings = await db.ProcessInspectionSettings.AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == ProcessInspectionSettings.SingletonId, ct);
        if (settings is not { Enabled: true }) return new ProcessInfoOutcome(0, 0, 0);
        var options = ToOptions(settings);

        var rows = await db.Sessions
            .Where(s => s.IsConnected && s.ProcessKind == ProcessKinds.Client
                        && (s.ProcInfoState == ProcInfoStates.Pending || s.ProcInfoState == ProcInfoStates.Failed))
            .OrderBy(s => s.Id)
            .Take(BatchSize)
            .Select(s => new { Row = s, s.Target!.Provider })
            .ToListAsync(ct);
        if (rows.Count == 0) return new ProcessInfoOutcome(0, 0, 0);

        var now = DateTime.Now;
        var skipped = 0;
        var work = new List<(TrackedSession Row, string Provider, string Host, int Pid)>();
        foreach (var r in rows)
        {
            var host = ClientProcessRules.HostOf(r.Row.Machine);
            var pid = ClientProcessRules.ProcessIdOf(r.Row.ClientProcess);
            string? reason = host is null ? "端末名（MACHINE）がありません"
                : pid is null ? $"PROCESS（{r.Row.ClientProcess}）からプロセス ID を取得できません"
                : ClientProcessRules.IsExcluded(host, settings.ExcludeMachines) ? "対象外端末に指定されています"
                : null;
            if (reason is not null)
            {
                SetState(r.Row, ProcInfoStates.NotApplicable, reason, now);
                skipped++;
                continue;
            }
            work.Add((r.Row, r.Provider, host!, pid!.Value));
        }

        // ホスト単位で問い合わせる（再試行待ちのホストは問い合わせない）
        var groups = work.GroupBy(w => (Inspector: inspectors.For(w.Provider), Host: w.Host.ToUpperInvariant())).ToList();
        var waiting = groups.Where(g => backoff.IsWaiting(g.Key.Host, now)).ToList();
        foreach (var g in waiting)
        {
            foreach (var w in g.Where(w => w.Row.ProcInfoState == ProcInfoStates.Pending))
                SetState(w.Row, ProcInfoStates.Failed, $"接続できないため再試行待ちです（{backoff.RetryAt(g.Key.Host):HH:mm} 以降）", now);
        }

        using var gate = new SemaphoreSlim(MaxParallelHosts);
        var targets = groups.Except(waiting).ToList();
        var results = await Task.WhenAll(targets.Select(async g =>
        {
            await gate.WaitAsync(ct);
            try
            {
                return (Group: g, Result: await InspectHostAsync(g.Key.Inspector, g.Key.Host, g.Select(w => w.Pid).ToList(), options, ct));
            }
            finally
            {
                gate.Release();
            }
        }));

        int resolved = 0, failed = 0;
        now = DateTime.Now;
        foreach (var (g, result) in results)
        {
            if (result.Error is not null)
            {
                if (backoff.RecordFailure(g.Key.Host, now))
                    logger.LogWarning("{Host} のプロセス情報を取得できません（{Retry:HH:mm} に再試行します）: {Error}",
                        g.Key.Host, backoff.RetryAt(g.Key.Host), result.Error);
                foreach (var w in g) SetState(w.Row, ProcInfoStates.Failed, result.Error, now);
                failed += g.Count();
                continue;
            }

            if (backoff.RecordSuccess(g.Key.Host))
                logger.LogInformation("{Host} のプロセス情報を取得できるようになりました", g.Key.Host);
            var byPid = result.Processes!.GroupBy(p => p.ProcessId).ToDictionary(x => x.Key, x => x.First());
            foreach (var w in g)
            {
                if (!byPid.TryGetValue(w.Pid, out var p))
                {
                    SetState(w.Row, ProcInfoStates.NotFound, "プロセスが見つかりません（終了済み）", now);
                }
                else if (ClientProcessRules.IsMismatch(p, w.Row.Program, w.Row.LogonTime))
                {
                    SetState(w.Row, ProcInfoStates.Mismatch,
                        $"別のプロセスの可能性があるため紐付けません（{p.Name}、開始 {p.StartedAt:yyyy/MM/dd HH:mm:ss}）", now);
                }
                else
                {
                    Apply(w.Row, p, now);
                    resolved++;
                }
            }
        }

        await db.SaveChangesAsync(ct);
        return new ProcessInfoOutcome(resolved, failed, skipped);
    }

    /// <summary>設定画面の接続テスト</summary>
    public Task<string> TestAsync(string host, ProcessInspectionSettings settings, CancellationToken ct) =>
        inspectors.Real.TestAsync(ClientProcessRules.HostOf(host) ?? host, ToOptions(settings), ct);

    internal static ProcessInspectorOptions ToOptions(ProcessInspectionSettings s) =>
        new(s.UserName, s.Password, s.DnsSuffix, TimeSpan.FromSeconds(Math.Clamp(s.TimeoutSeconds, 1, 120)));

    private async Task<HostResult> InspectHostAsync(IProcessInspector inspector, string host, IReadOnlyCollection<int> pids,
        ProcessInspectorOptions options, CancellationToken ct)
    {
        // 取得元がタイムアウトを守らない場合に備え、ホスト単位でも打ち切る（WinRM → DCOM の 2 回分＋余裕）
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(options.Timeout * 3);
        try
        {
            return new HostResult(await inspector.InspectAsync(host, pids, options, cts.Token), null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new HostResult(null, "タイムアウトしました");
        }
        catch (ProcessInspectionException ex)
        {
            return new HostResult(null, ex.Message);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // 想定外の例外もそのホストの失敗として扱い、他のホストの処理を続ける
            logger.LogWarning(ex, "{Host} のプロセス情報の取得で想定外のエラーが発生しました", host);
            return new HostResult(null, ex.Message);
        }
    }

    private static void SetState(TrackedSession row, string state, string? error, DateTime now)
    {
        row.ProcInfoState = state;
        row.ProcInfoError = error is { Length: > MaxErrorLength } ? error[..MaxErrorLength] : error;
        row.ProcInfoAt = now;
    }

    private static void Apply(TrackedSession row, ClientProcessInfo p, DateTime now)
    {
        SetState(row, ProcInfoStates.Resolved, null, now);
        row.ProcName = p.Name;
        row.ProcPath = p.ExecutablePath;
        row.ProcCommandLine = p.CommandLine;
        row.ProcOwner = p.Owner;
        row.ProcStartedAt = p.StartedAt;
        row.ProcParentName = p.ParentName;
        row.IisAppPool = p.IisAppPool is { Length: > 256 } pool ? pool[..256] : p.IisAppPool;
        row.IisSite = p.IisSite;
        (row.AppName, row.AppDetail) = AppIdentifier.Identify(p, row.Program, row.Module, row.OsUser);
    }
}
