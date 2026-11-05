using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OracleSessionHistory.Core.Data;
using OracleSessionHistory.Core.Entities;
using OracleSessionHistory.Core.Processes;

namespace OracleSessionHistory.Core.Services;

/// <summary>1 回分のプロセス情報取得の結果</summary>
public sealed record ProcessInfoOutcome(int Resolved, int Failed, int Skipped);

/// <summary>
/// 接続中のクライアントセッション（登録した実行ファイルのもの）と、DB Link 接続元 DB 上の利用元セッションについて、
/// 接続元サーバーのプロセス情報を取得してアプリ名を確定する（Batch から呼ばれる）。
/// セッション収集とは独立して動き、失敗してもセッションの接続・切断の判定には一切影響しない。
/// </summary>
public sealed class ProcessInfoService(
    IDbContextFactory<AppDbContext> dbFactory,
    ProcessInspectorResolver inspectors,
    HostBackoff backoff,
    ILogger<ProcessInfoService> logger)
{
    /// <summary>1 回に処理するセッション数の上限</summary>
    internal int BatchSize { get; set; } = 500;
    /// <summary>同時に問い合わせるホスト数</summary>
    private const int MaxParallelHosts = 4;
    private const int MaxErrorLength = 500;

    private sealed record HostResult(IReadOnlyList<ClientProcessInfo>? Processes, string? Error);

    /// <summary>
    /// DB Link 利用元は接続中かどうかを持たないため、最後に観測してからこの時間内のものだけ調べる
    /// （それより古いものは接続元セッションが終了しており、PID が別のプロセスに再利用されている可能性がある）
    /// </summary>
    internal static readonly TimeSpan LinkUsageWindow = TimeSpan.FromMinutes(10);

    public async Task<ProcessInfoOutcome> RunOnceAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var settings = await db.ProcessInspectionSettings.AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == ProcessInspectionSettings.SingletonId, ct);
        if (settings is not { Enabled: true }) return new ProcessInfoOutcome(0, 0, 0);
        var options = ToOptions(settings);

        var sessions = await db.Sessions
            .Where(s => s.IsConnected && s.ProcessKind == ProcessKinds.Client
                        && (s.ProcInfoState == ProcInfoStates.Pending || s.ProcInfoState == ProcInfoStates.Failed))
            // 再試行待ちの端末の失敗行が多くても、新しい行が後回しにならないよう未取得を先に処理する
            .OrderBy(s => s.ProcInfoState == ProcInfoStates.Pending ? 0 : 1)
            .ThenBy(s => s.Id)
            .Take(BatchSize)
            .Select(s => new { Row = s, s.Target!.Provider })
            .ToListAsync(ct);

        var now = DateTime.Now;
        var since = now - LinkUsageWindow;
        // 観測されなくなった DB Link 利用元の未取得行は、取得しないまま残さず理由を付けて終える
        await db.DbLinkUsages
            .Where(u => u.ProcInfoState == ProcInfoStates.Pending && u.LastSeenAt < since)
            .ExecuteUpdateAsync(x => x
                .SetProperty(u => u.ProcInfoState, ProcInfoStates.NotApplicable)
                .SetProperty(u => u.ProcInfoError, "接続元セッションの終了後のため取得していません")
                .SetProperty(u => u.ProcInfoAt, now), ct);
        var usages = await db.DbLinkUsages
            .Where(u => u.LastSeenAt >= since
                        && (u.ProcInfoState == ProcInfoStates.Pending || u.ProcInfoState == ProcInfoStates.Failed))
            .OrderBy(u => u.ProcInfoState == ProcInfoStates.Pending ? 0 : 1)
            .ThenBy(u => u.Id)
            .Take(BatchSize)
            .Select(u => new { Row = u, u.LinkSource!.Provider })
            .ToListAsync(ct);

        var rows = sessions.Select(r => (Row: (IProcessInfoRecord)r.Row, r.Provider, Direct: true))
            .Concat(usages.Select(r => (Row: (IProcessInfoRecord)r.Row, r.Provider, Direct: false)))
            .ToList();
        if (rows.Count == 0) return new ProcessInfoOutcome(0, 0, 0);

        var skipped = 0;
        var work = new List<(IProcessInfoRecord Row, string Provider, string Host, int Pid)>();
        foreach (var r in rows)
        {
            var host = ClientProcessRules.HostOf(r.Row.Machine);
            var pid = ClientProcessRules.ProcessIdOf(r.Row.ClientProcess);
            // 監視対象への直接の接続は登録した実行ファイルだけ調べる。DB Link 利用元は接続元 DB の登録で対象を絞っているため常に調べる
            string? reason = r.Direct && !ClientProcessRules.IsTargetProgram(r.Row.Program, settings.TargetPrograms)
                    ? "プロセス情報を取得する実行ファイルに登録されていません"
                : host is null ? "端末名（MACHINE）がありません"
                : pid is null ? $"PROCESS（{r.Row.ClientProcess}）からプロセス ID を取得できません"
                : ClientProcessRules.IsDriverDefaultProcess(r.Row.ClientProcess, r.Row.Program, r.Row.ClientDriver)
                    ? "JDBC Thin ドライバの既定値（PROCESS=1234）のため、プロセスを特定できません"
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
                return (Group: g, Result: await InspectHostAsync(g.Key.Inspector, g.Key.Host, g.Select(w => w.Pid).Distinct().ToList(), options, ct));
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

        await SaveSkippingDeletedAsync(db, ct);
        return new ProcessInfoOutcome(resolved, failed, skipped);
    }

    /// <summary>
    /// 保存する。取得中に監視対象の削除・履歴の削除で行が消えていた場合は、その行を除いて保存し直す
    /// （1 行のためにその回の結果をすべて失わないようにする）。
    /// </summary>
    private async Task SaveSkippingDeletedAsync(AppDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // 例外に含まれるのは最初に失敗した行（バッチ）だけなので、消えた行をまとめて調べて除く
            logger.LogDebug(ex, "プロセス情報の保存中に削除されたセッションがあったため、除いて保存し直します");
            var sessions = db.ChangeTracker.Entries<TrackedSession>().Where(e => e.State == EntityState.Modified).ToList();
            var sessionIds = sessions.Select(e => e.Entity.Id).ToList();
            var existingSessions = (await db.Sessions.AsNoTracking().Where(s => sessionIds.Contains(s.Id)).Select(s => s.Id).ToListAsync(ct)).ToHashSet();
            foreach (var e in sessions.Where(e => !existingSessions.Contains(e.Entity.Id))) e.State = EntityState.Detached;

            var usages = db.ChangeTracker.Entries<DbLinkUsage>().Where(e => e.State == EntityState.Modified).ToList();
            var usageIds = usages.Select(e => e.Entity.Id).ToList();
            var existingUsages = (await db.DbLinkUsages.AsNoTracking().Where(u => usageIds.Contains(u.Id)).Select(u => u.Id).ToListAsync(ct)).ToHashSet();
            foreach (var e in usages.Where(e => !existingUsages.Contains(e.Entity.Id))) e.State = EntityState.Detached;
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// 設定（登録した実行ファイル・対象外端末）を変更したとき、対象外にしていた接続中のクライアントセッションを未取得に戻して調べ直す。
    /// </summary>
    public static Task<int> RequeueSkippedAsync(AppDbContext db, CancellationToken ct) =>
        db.Sessions
            .Where(s => s.IsConnected && s.ProcessKind == ProcessKinds.Client && s.ProcInfoState == ProcInfoStates.NotApplicable)
            .ExecuteUpdateAsync(x => x
                .SetProperty(s => s.ProcInfoState, ProcInfoStates.Pending)
                .SetProperty(s => s.ProcInfoError, (string?)null), ct);

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
        Task<IReadOnlyList<ClientProcessInfo>>? task = null;
        try
        {
            task = inspector.InspectAsync(host, pids, options, cts.Token);
            // CIM の同期呼び出しはキャンセルを無視して待ち続けることがあるため、待つ側でも打ち切る
            return new HostResult(await task.WaitAsync(cts.Token), null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            if (task is not null) ObserveAbandoned(task, host);
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

    /// <summary>打ち切った問い合わせが後で失敗した場合も、例外を握りつぶさずログに残す</summary>
    private void ObserveAbandoned(Task task, string host) =>
        _ = task.ContinueWith(t => logger.LogDebug(t.Exception, "{Host} への打ち切った問い合わせが失敗しました", host),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    private static void SetState(IProcessInfoRecord row, string state, string? error, DateTime now)
    {
        row.ProcInfoState = state;
        row.ProcInfoError = error is { Length: > MaxErrorLength } ? error[..MaxErrorLength] : error;
        row.ProcInfoAt = now;
    }

    private static void Apply(IProcessInfoRecord row, ClientProcessInfo p, DateTime now)
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
