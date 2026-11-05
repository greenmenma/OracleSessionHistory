using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OracleSessionHistory.Core.Data;
using OracleSessionHistory.Core.Entities;
using OracleSessionHistory.Core.Processes;
using OracleSessionHistory.Core.Tracking;

namespace OracleSessionHistory.Core.Services;

/// <summary>1 回分のプロセス情報取得の結果</summary>
public sealed record ProcessInfoOutcome(int Resolved, int Failed, int Skipped);

/// <summary>
/// 接続中のクライアントセッションと、DB Link 接続元 DB 上の利用元セッションについて、
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
        await CloseUnreachableAsync(db, settings is { Enabled: true }, ct);
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
        // 観測されなくなった DB Link 利用元の未取得・失敗行は、そのまま残さず理由を付けて終える
        await db.DbLinkUsages
            .Where(u => (u.ProcInfoState == ProcInfoStates.Pending || u.ProcInfoState == ProcInfoStates.Failed) && u.LastSeenAt < since)
            .ExecuteUpdateAsync(x => x
                .SetProperty(u => u.ProcInfoState, ProcInfoStates.NotApplicable)
                .SetProperty(u => u.ProcInfoError, "接続元セッションの終了後のため取得していません")
                .SetProperty(u => u.ProcInfoAt, now), ct);
        var usages = await db.DbLinkUsages
            .Where(u => u.LastSeenAt >= since
                        && (u.ProcInfoState == ProcInfoStates.Pending || u.ProcInfoState == ProcInfoStates.Failed))
            .OrderBy(u => u.ProcInfoState == ProcInfoStates.Pending ? 0 : 1)
            .ThenBy(u => u.Id)
            // 1 回の処理件数はセッションと合わせて BatchSize まで
            .Take(Math.Max(0, BatchSize - sessions.Count))
            .Select(u => new { Row = u, u.LinkSource!.Provider })
            .ToListAsync(ct);

        var rows = sessions.Select(r => (Row: (IProcessInfoRecord)r.Row, r.Provider, Direct: true))
            .Concat(usages.Select(r => (Row: (IProcessInfoRecord)r.Row, r.Provider, Direct: false)))
            .ToList();
        if (rows.Count == 0) return new ProcessInfoOutcome(0, 0, 0);

        var skipped = new List<(IProcessInfoRecord Row, string State, string? Error, DateTime? At)>();
        var work = new List<(IProcessInfoRecord Row, string Provider, string Host, int Pid)>();
        foreach (var r in rows)
        {
            var host = ClientProcessRules.HostOf(r.Row.Machine);
            var pid = ClientProcessRules.ProcessIdOf(r.Row.ClientProcess);
            // DB Link 利用元がジョブ・さらに別の DB Link 等の場合、端末は DB サーバー自身のため問い合わせない
            string? reason = !r.Direct && ProgramClassifier.Classify(r.Row.Program, "USER", r.Row.Module) != ProcessKinds.Client
                    ? "接続元が Oracle のプロセス（ジョブ・DB Link 等）のため取得しません"
                : host is null ? "端末名（MACHINE）がありません"
                : pid is null ? $"PROCESS（{r.Row.ClientProcess}）からプロセス ID を取得できません"
                : ClientProcessRules.IsDriverDefaultProcess(r.Row.ClientProcess, r.Row.Program, r.Row.ClientDriver)
                    ? "JDBC Thin ドライバの既定値（PROCESS=1234）のため、プロセスを特定できません"
                : ClientProcessRules.IsExcluded(host, settings.ExcludeMachines) ? "対象外端末に指定されています"
                : null;
            if (reason is not null)
            {
                skipped.Add((r.Row, r.Row.ProcInfoState, r.Row.ProcInfoError, r.Row.ProcInfoAt));
                SetState(r.Row, ProcInfoStates.NotApplicable, reason, now);
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

        // 取得中に画面で設定（対象外端末）が保存された場合、古い設定での「対象外」は保存しない。
        // 保存時の再取得の登録（RequeueSkippedAsync）より後にこの回の結果を書くと、調べ直されなくなるため
        var updatedAt = await db.ProcessInspectionSettings.AsNoTracking()
            .Where(s => s.Id == ProcessInspectionSettings.SingletonId).Select(s => (DateTime?)s.UpdatedAt).SingleOrDefaultAsync(ct);
        if (updatedAt != settings.UpdatedAt)
        {
            foreach (var (row, state, error, at) in skipped)
            {
                row.ProcInfoState = state;
                row.ProcInfoError = error;
                row.ProcInfoAt = at;
            }
        }

        await SaveSkippingDeletedAsync(db, ct);

        // DB Link 利用元のアプリが確定したら、監視対象側のセッション（ORACLE.EXE）にも接続元のアプリとして反映する
        var linked = work.Where(w => w.Row is DbLinkUsage { MatchedSessionId: not null, ProcInfoState: ProcInfoStates.Resolved })
            .Select(w => ((DbLinkUsage)w.Row).MatchedSessionId!.Value).ToList();
        if (linked.Count > 0)
        {
            try
            {
                await LinkOriginSync.RefreshAsync(db, linked, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "DB Link 接続元のアプリをセッションに反映できませんでした（次回の利用元の取得で反映します）");
            }
        }
        return new ProcessInfoOutcome(resolved, failed, skipped.Count);
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
    /// 設定（対象外端末）を変更したとき、対象外にしていた接続中のクライアントセッションと
    /// 観測中の DB Link 利用元を未取得に戻して調べ直す。
    /// </summary>
    public static async Task<int> RequeueSkippedAsync(AppDbContext db, CancellationToken ct)
    {
        var n = await db.Sessions
            .Where(s => s.IsConnected && s.ProcessKind == ProcessKinds.Client && s.ProcInfoState == ProcInfoStates.NotApplicable)
            .ExecuteUpdateAsync(x => x
                .SetProperty(s => s.ProcInfoState, ProcInfoStates.Pending)
                .SetProperty(s => s.ProcInfoError, (string?)null), ct);
        var since = DateTime.Now - LinkUsageWindow;
        n += await db.DbLinkUsages
            .Where(u => u.LastSeenAt >= since && u.ProcInfoState == ProcInfoStates.NotApplicable)
            .ExecuteUpdateAsync(x => x
                .SetProperty(u => u.ProcInfoState, ProcInfoStates.Pending)
                .SetProperty(u => u.ProcInfoError, (string?)null), ct);
        return n;
    }

    /// <summary>
    /// 取得しないまま「取得待ち」と表示され続ける行に理由を付けて終える。
    /// ・取得前に切断したセッション（切断後は PID が再利用されるため調べない）
    /// ・取得が無効の間の行（有効にして保存すると <see cref="RequeueSkippedAsync"/> で調べ直す）
    /// </summary>
    private static async Task CloseUnreachableAsync(AppDbContext db, bool enabled, CancellationToken ct)
    {
        var now = DateTime.Now;
        await db.Sessions
            .Where(s => !s.IsConnected && s.ProcInfoState == ProcInfoStates.Pending)
            .ExecuteUpdateAsync(x => x
                .SetProperty(s => s.ProcInfoState, ProcInfoStates.NotApplicable)
                .SetProperty(s => s.ProcInfoError, "切断までに取得できませんでした")
                .SetProperty(s => s.ProcInfoAt, now), ct);
        if (enabled) return;

        // 読んだ後に画面で有効にして保存（調べ直しの登録）された場合に取り消さないよう、更新の時点でも無効であることを条件にする
        const string disabled = "プロセス情報の取得が無効です";
        var enabledNow = db.ProcessInspectionSettings.Where(p => p.Id == ProcessInspectionSettings.SingletonId && p.Enabled);
        await db.Sessions
            .Where(s => s.IsConnected && s.ProcInfoState == ProcInfoStates.Pending && !enabledNow.Any())
            .ExecuteUpdateAsync(x => x
                .SetProperty(s => s.ProcInfoState, ProcInfoStates.NotApplicable)
                .SetProperty(s => s.ProcInfoError, disabled)
                .SetProperty(s => s.ProcInfoAt, now), ct);
        await db.DbLinkUsages
            .Where(u => u.ProcInfoState == ProcInfoStates.Pending && !enabledNow.Any())
            .ExecuteUpdateAsync(x => x
                .SetProperty(u => u.ProcInfoState, ProcInfoStates.NotApplicable)
                .SetProperty(u => u.ProcInfoError, disabled)
                .SetProperty(u => u.ProcInfoAt, now), ct);
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
        // アプリ名はパスワードを隠す前の値で決める（隠した値ではスクリプトのパス等が取れなくなることがある）
        (row.AppName, row.AppDetail) = AppIdentifier.Identify(p, row.Program, row.Module, row.OsUser);
        if (row.AppDetail is not null) row.AppDetail = ClientProcessRules.MaskCredentials(row.AppDetail, ClientProcessRules.IsOracleTool(p.Name));

        // コマンドラインに含まれるパスワード（sqlplus scott/tiger@db 等）は保存しない
        var tool = ClientProcessRules.IsOracleTool(p.Name);
        var masked = p with
        {
            CommandLine = ClientProcessRules.MaskCredentials(p.CommandLine),
            Ancestors = p.Ancestors?.Select(a => a with { CommandLine = ClientProcessRules.MaskCredentials(a.CommandLine, tool) }).ToList(),
        };
        row.ProcName = masked.Name;
        row.ProcPath = masked.ExecutablePath;
        row.ProcCommandLine = masked.CommandLine;
        row.ProcOwner = masked.Owner;
        row.ProcStartedAt = masked.StartedAt;
        row.ProcParentName = masked.ParentName ?? masked.Ancestors?.FirstOrDefault()?.Name;
        row.ProcAncestry = masked.Ancestors is { Count: > 0 } ancestors
            ? string.Join("\n", ancestors.Select(a => string.IsNullOrWhiteSpace(a.CommandLine) ? a.Name : $"{a.Name}: {a.CommandLine}"))
            : null;
        row.IisAppPool = masked.IisAppPool is { Length: > 256 } pool ? pool[..256] : masked.IisAppPool;
        row.IisSite = masked.IisSite;
    }
}
