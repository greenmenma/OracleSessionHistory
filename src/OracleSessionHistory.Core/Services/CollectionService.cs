using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OracleSessionHistory.Core.Collection;
using OracleSessionHistory.Core.Data;
using OracleSessionHistory.Core.DbLinks;
using OracleSessionHistory.Core.Entities;
using OracleSessionHistory.Core.Processes;
using OracleSessionHistory.Core.Tracking;

namespace OracleSessionHistory.Core.Services;

/// <summary>1 回分のポーリング結果</summary>
public sealed record PollOutcome(bool Success, int Observed, int Added, int Disconnected, string? Error);

/// <summary>監査証跡のログオン件数の取得結果。Needed=false は、履歴が 2 ヶ月分たまっていて取得不要だった</summary>
public sealed record AuditPollOutcome(bool Success, int Rows, string? Error);

/// <summary>
/// 収集処理（Batch から呼ばれる）。取得 → 追跡 → 保存 を 1 回分行う。
/// </summary>
public sealed class CollectionService(
    IDbContextFactory<AppDbContext> dbFactory,
    SessionSourceFactory sources,
    DbLinkInspectorFactory inspectors,
    ILogger<CollectionService> logger)
{
    /// <summary>切断済みの行も、この時間以内なら復帰判定の対象にする</summary>
    private static readonly TimeSpan ReconnectWindow = TimeSpan.FromHours(1);

    public async Task<PollOutcome> PollSessionsAsync(int targetId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var target = await db.Targets.SingleOrDefaultAsync(t => t.Id == targetId, ct)
                     ?? throw new InvalidOperationException($"監視対象が見つかりません: {targetId}");

        SessionSnapshot snapshot;
        try
        {
            snapshot = await sources.Get(target.Provider).CaptureAsync(target, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // 取得失敗：セッションには一切触れない（誤って切断扱いにしない）
            target.LastPollAt = DateTime.Now;
            target.LastError = ex.Message;
            target.ConsecutiveFailures++;
            await db.SaveChangesAsync(ct);
            logger.LogWarning(ex, "[{Target}] セッション取得に失敗しました（{Count} 回連続）", target.Name, target.ConsecutiveFailures);
            return new PollOutcome(false, 0, 0, 0, ex.Message);
        }

        var since = snapshot.CapturedAt - ReconnectWindow;
        var known = await db.Sessions
            .Where(s => s.TargetId == targetId && (s.IsConnected || s.DisconnectDetectedAt >= since))
            .ToListAsync(ct);

        var result = SessionTracker.Apply(targetId, known, snapshot, target.LastSuccessAt,
            TimeSpan.FromSeconds(Math.Max(1, target.IntervalSeconds)));

        db.Sessions.AddRange(result.Added);
        var appNameChanges = DeferAppNameChanges(db, result.Updated);
        target.LastPollAt = snapshot.CapturedAt;
        target.LastSuccessAt = snapshot.CapturedAt;
        target.ConsecutiveFailures = 0;
        target.LastSessionCount = snapshot.Sessions.Count;
        target.LastError = snapshot.Warnings.Count > 0 ? "（警告）" + string.Join(" / ", snapshot.Warnings) : null;
        await db.SaveChangesAsync(ct);
        await ApplyAppNameChangesAsync(db, target.Name, appNameChanges, ct);

        if (result.Added.Count > 0 || result.Disconnected.Count > 0)
            logger.LogInformation("[{Target}] 取得 {Observed} 件 / 新規 {Added} 件 / 切断 {Disconnected} 件",
                target.Name, snapshot.Sessions.Count, result.Added.Count, result.Disconnected.Count);

        return new PollOutcome(true, snapshot.Sessions.Count, result.Added.Count, result.Disconnected.Count, null);
    }

    /// <summary>
    /// 既存行の仮のアプリ名（PROGRAM・MODULE 由来）の変更は通常の保存から外す。
    /// 行を読み込んだ後にプロセス情報の取得処理（別ループ）がアプリ名を確定していた場合に、古い値で上書きしないため。
    /// </summary>
    private static List<(long Id, string AppName, string? AppDetail)> DeferAppNameChanges(AppDbContext db, IEnumerable<TrackedSession> updated)
    {
        var changes = new List<(long, string, string?)>();
        foreach (var row in updated)
        {
            var entry = db.Entry(row);
            var name = entry.Property(x => x.AppName);
            var detail = entry.Property(x => x.AppDetail);
            if (!name.IsModified && !detail.IsModified) continue;
            changes.Add((row.Id, row.AppName, row.AppDetail));
            name.IsModified = false;
            detail.IsModified = false;
        }
        return changes;
    }

    /// <summary>
    /// 仮のアプリ名の変更を、プロセス情報で確定していない行にだけ反映する（同じ値の行はまとめて更新）。
    /// 表示用の補助情報のため、失敗してもセッションの取得自体は成功として扱う。
    /// </summary>
    private async Task ApplyAppNameChangesAsync(AppDbContext db, string targetName,
        List<(long Id, string AppName, string? AppDetail)> changes, CancellationToken ct)
    {
        try
        {
            foreach (var g in changes.GroupBy(c => (c.AppName, c.AppDetail)))
            {
                var ids = g.Select(c => c.Id).ToList();
                var (name, detail) = g.Key;
                await db.Sessions
                    .Where(s => ids.Contains(s.Id) && s.ProcInfoState != ProcInfoStates.Resolved)
                    .ExecuteUpdateAsync(u => u.SetProperty(s => s.AppName, name).SetProperty(s => s.AppDetail, detail), ct);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "[{Target}] アプリ名の更新に失敗しました（次回の取得で再度反映します）", targetName);
        }
    }

    /// <summary>DB Link 接続元 DB を調べ、DB Link を使っているセッションを記録する</summary>
    public async Task PollLinkSourcesAsync(int targetId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var linkSources = await db.LinkSources.Where(l => l.TargetId == targetId && l.Enabled).ToListAsync(ct);
        if (linkSources.Count == 0) return;

        // 監視対象側の DB Link 疑いセッション：process（接続元の spid）→ セッション ID
        var suspects = await db.Sessions
            .Where(s => s.TargetId == targetId && s.IsConnected && s.ProcessKind == ProcessKinds.DbLinkSuspect)
            .Select(s => new { s.Id, s.ClientProcess })
            .ToListAsync(ct);
        var pidMap = new Dictionary<string, long>();
        foreach (var s in suspects)
            foreach (var pid in ProgramClassifier.ProcessIdCandidates(s.ClientProcess))
                pidMap.TryAdd(pid, s.Id);

        foreach (var source in linkSources)
        {
            var now = DateTime.Now;
            IReadOnlyCollection<long> reassigned = [];
            try
            {
                var found = await inspectors.Get(source.Provider).FindUsagesAsync(source, pidMap.Keys, ct);
                reassigned = await UpsertUsagesAsync(db, source, found.Usages, pidMap, now, ct);
                source.LastSuccessAt = now;
                source.LastError = found.Warnings.Count > 0 ? "（警告）" + string.Join(" / ", found.Warnings) : null;
                if (found.Warnings.Count > 0)
                    logger.LogWarning("[{Source}] DB Link 接続元の一部の検索ができませんでした: {Warnings}", source.Name, string.Join(" / ", found.Warnings));
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                source.LastError = ex.Message;
                logger.LogWarning(ex, "[{Source}] DB Link 接続元の取得に失敗しました", source.Name);
            }
            source.LastPollAt = now;
            await db.SaveChangesAsync(ct);
            await RefreshLinkOriginsAsync(db, source, reassigned, ct);
        }
    }

    /// <summary>
    /// この接続元 DB の利用元が紐付いた、接続中の DB Link の可能性があるセッションに、接続元の端末・アプリを反映する。
    /// 失敗してもセッション収集・利用元の記録には影響させない。
    /// </summary>
    /// <param name="reassigned">利用元の付け替えで、紐付けが外れた前のセッション</param>
    private async Task RefreshLinkOriginsAsync(AppDbContext db, LinkSource source, IReadOnlyCollection<long> reassigned, CancellationToken ct)
    {
        try
        {
            var ids = await db.DbLinkUsages
                .Where(u => u.LinkSourceId == source.Id && u.MatchedSessionId != null && u.MatchedSession!.IsConnected)
                .Select(u => u.MatchedSessionId!.Value).Distinct().ToListAsync(ct);
            await LinkOriginSync.RefreshAsync(db, ids.Concat(reassigned).ToList(), ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "[{Source}] DB Link 接続元のアプリをセッションに反映できませんでした（次回の取得で再度反映します）", source.Name);
        }
    }

    /// <returns>付け替えで紐付けが外れた前のセッション</returns>
    private static async Task<IReadOnlyCollection<long>> UpsertUsagesAsync(AppDbContext db, LinkSource source, IReadOnlyList<ObservedLinkUsage> usages,
        Dictionary<string, long> pidMap, DateTime now, CancellationToken ct)
    {
        var reassigned = new HashSet<long>();
        if (usages.Count == 0) return reassigned;

        // 同じ接続元セッションがプロセス一致していれば、SQL 一致の行にも対応セッションを設定する
        var matchedBySession = usages
            .Where(u => u.ServerProcess is not null && pidMap.ContainsKey(u.ServerProcess))
            .GroupBy(u => (u.InstId, u.Sid, u.Serial, u.LogonTime))
            .ToDictionary(g => g.Key, g => pidMap[g.First().ServerProcess!]);

        var since = now.AddDays(-1);
        var existing = await db.DbLinkUsages
            .Where(u => u.LinkSourceId == source.Id && u.LastSeenAt >= since)
            .ToListAsync(ct);
        var byKey = existing.ToDictionary(u => (u.DbLinkName, u.InstId, u.Sid, u.Serial, u.LogonTime));

        foreach (var u in usages.DistinctBy(u => (u.DbLinkName, u.InstId, u.Sid, u.Serial, u.LogonTime)))
        {
            var key = (u.DbLinkName, u.InstId, u.Sid, u.Serial, u.LogonTime);
            if (!byKey.TryGetValue(key, out var row))
            {
                row = new DbLinkUsage
                {
                    LinkSourceId = source.Id,
                    TargetId = source.TargetId,
                    DbLinkName = u.DbLinkName,
                    InstId = u.InstId,
                    Sid = u.Sid,
                    Serial = u.Serial,
                    LogonTime = u.LogonTime,
                    FirstSeenAt = now,
                    // 接続元端末のプロセス情報は ProcessInfoService が取得する（それまでは PROGRAM 等から仮のアプリ名を付ける）。
                    // 取得対象のプログラム（w3wp.exe・sqlplus.exe）以外は取得しない
                    ProcInfoState = ClientProcessRules.IsInspectedProgram(u.Program) ? ProcInfoStates.Pending : ProcInfoStates.NotApplicable,
                };
                (row.AppName, row.AppDetail) = AppIdentifier.Identify(null, u.Program, u.Module, u.OsUser);
                db.DbLinkUsages.Add(row);
                byKey[key] = row;
            }
            row.MatchType = u.MatchType;
            row.Username = u.Username;
            row.SchemaName = u.SchemaName;
            row.OsUser = u.OsUser;
            row.Machine = u.Machine?.Trim();
            row.Terminal = u.Terminal;
            row.Program = u.Program;
            row.Module = u.Module;
            row.Action = u.Action;
            row.ServerProcess = u.ServerProcess;
            row.ClientProcess = u.ClientProcess;
            row.SqlId = u.SqlId ?? row.SqlId;
            row.SqlText = u.SqlText ?? row.SqlText;
            row.LastSeenAt = now;
            row.HitCount++;
            if (matchedBySession.TryGetValue((u.InstId, u.Sid, u.Serial, u.LogonTime), out var sessionId))
            {
                if (row.MatchedSessionId is { } old && old != sessionId) reassigned.Add(old);
                row.MatchedSessionId = sessionId;
            }
        }
        return reassigned;
    }

    /// <summary>
    /// 監査証跡（DBA_AUDIT_SESSION）から日・端末ごとのログオン件数を取得して保存する（接続アプリ表の端末の行の件数）。
    /// 期間は接続アプリ表の最も古い列（2 ヶ月前の同日）から本日まで。前回取得した期間がそろっていれば、
    /// 前回取得した日（途中までの件数）以降だけを取り直し、期間より古い件数は消す。
    /// 失敗してもセッションの収集には影響しない（理由を監視対象に残し、保存済みの件数は消さない）。
    /// </summary>
    public async Task<AuditPollOutcome> PollAuditLogonsAsync(int targetId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var target = await db.Targets.AsNoTracking().SingleOrDefaultAsync(t => t.Id == targetId, ct);
        if (target is null) return new AuditPollOutcome(true, 0, null);

        var from = AuditWindowStart(DateTime.Today);
        var to = DateTime.Today.AddDays(1);
        // 前回の期間が今回の開始日を含んでいれば、前回取得した日から取り直す（それより前の日は確定している）
        var fetchFrom = target is { AuditFrom: { } pf, AuditTo: { } pt } && pf <= from && pt > from
            ? Max(from, pt.AddDays(-1))
            : from;

        IReadOnlyList<AuditLogonDay> days;
        try
        {
            days = await sources.Get(target.Provider).CaptureAuditLogonsAsync(target, fetchFrom, to, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var error = Truncate(ex.Message, 1000);
            // 同じ理由で失敗し続ける場合（権限なし等）は、最初の 1 回だけ警告する
            if (error != target.AuditError)
                logger.LogWarning(ex, "[{Target}] 監査証跡（DBA_AUDIT_SESSION）のログオン件数を取得できませんでした（接続アプリ表の端末の件数に使います）", target.Name);
            await db.Targets.Where(t => t.Id == targetId).ExecuteUpdateAsync(x => x
                .SetProperty(t => t.AuditError, error)
                .SetProperty(t => t.AuditFetchedAt, DateTime.Now), ct);
            return new AuditPollOutcome(false, 0, error);
        }

        // 端末名の大文字小文字の違いは同じ端末として合計する（SQL Server の照合順序では同じ値になるため）
        var rows = days
            .GroupBy(d => (Day: d.Day.Date, Key: Truncate(d.Machine?.Trim() ?? "", 256).ToUpperInvariant()))
            .Select(g => new AuditLogonCount
            {
                TargetId = targetId, Day = g.Key.Day, Machine = Truncate(g.First().Machine?.Trim() ?? "", 256), Count = g.Sum(d => d.Count),
            })
            .ToList();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.AuditLogonCounts.Where(a => a.TargetId == targetId && (a.Day < from || a.Day >= fetchFrom)).ExecuteDeleteAsync(ct);
        db.AuditLogonCounts.AddRange(rows);
        await db.SaveChangesAsync(ct);
        // UpdatedAt は変えない（Batch の再起動の判断に使うため）
        await db.Targets.Where(t => t.Id == targetId).ExecuteUpdateAsync(x => x
            .SetProperty(t => t.AuditFrom, from)
            .SetProperty(t => t.AuditTo, to)
            .SetProperty(t => t.AuditFetchedAt, DateTime.Now)
            .SetProperty(t => t.AuditError, (string?)null), ct);
        await tx.CommitAsync(ct);
        if (target.AuditError is not null)
            logger.LogInformation("[{Target}] 監査証跡のログオン件数を取得できるようになりました", target.Name);
        return new AuditPollOutcome(true, rows.Count, null);
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    /// <summary>接続アプリ表の最も古い列（1ヶ月〜2ヶ月）の開始日</summary>
    public static DateTime AuditWindowStart(DateTime today) => today.AddMonths(-2);

    private static string Truncate(string s, int max) => s.Length > max ? s[..max] : s;

    /// <summary>保持期間を過ぎた履歴を削除する</summary>
    public async Task<int> CleanupAsync(int retentionDays, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await AppDbMaintenance.DeleteExpiredAsync(db, DateTime.Now.AddDays(-retentionDays), ct);
    }
}
