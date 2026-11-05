using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OracleSessionHistory.Core.Collection;
using OracleSessionHistory.Core.Data;
using OracleSessionHistory.Core.DbLinks;
using OracleSessionHistory.Core.Entities;
using OracleSessionHistory.Core.Tracking;

namespace OracleSessionHistory.Core.Services;

/// <summary>1 回分のポーリング結果</summary>
public sealed record PollOutcome(bool Success, int Observed, int Added, int Disconnected, string? Error);

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
        target.LastPollAt = snapshot.CapturedAt;
        target.LastSuccessAt = snapshot.CapturedAt;
        target.ConsecutiveFailures = 0;
        target.LastSessionCount = snapshot.Sessions.Count;
        target.LastError = snapshot.Warnings.Count > 0 ? "（警告）" + string.Join(" / ", snapshot.Warnings) : null;
        await db.SaveChangesAsync(ct);

        if (result.Added.Count > 0 || result.Disconnected.Count > 0)
            logger.LogInformation("[{Target}] 取得 {Observed} 件 / 新規 {Added} 件 / 切断 {Disconnected} 件",
                target.Name, snapshot.Sessions.Count, result.Added.Count, result.Disconnected.Count);

        return new PollOutcome(true, snapshot.Sessions.Count, result.Added.Count, result.Disconnected.Count, null);
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
            try
            {
                var found = await inspectors.Get(source.Provider).FindUsagesAsync(source, pidMap.Keys, ct);
                await UpsertUsagesAsync(db, source, found.Usages, pidMap, now, ct);
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
        }
    }

    private static async Task UpsertUsagesAsync(AppDbContext db, LinkSource source, IReadOnlyList<ObservedLinkUsage> usages,
        Dictionary<string, long> pidMap, DateTime now, CancellationToken ct)
    {
        if (usages.Count == 0) return;

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
                };
                db.DbLinkUsages.Add(row);
                byKey[key] = row;
            }
            row.MatchType = u.MatchType;
            row.Username = u.Username;
            row.SchemaName = u.SchemaName;
            row.OsUser = u.OsUser;
            row.Machine = u.Machine;
            row.Terminal = u.Terminal;
            row.Program = u.Program;
            row.Module = u.Module;
            row.Action = u.Action;
            row.ServerProcess = u.ServerProcess;
            row.SqlId = u.SqlId ?? row.SqlId;
            row.SqlText = u.SqlText ?? row.SqlText;
            row.LastSeenAt = now;
            row.HitCount++;
            if (matchedBySession.TryGetValue((u.InstId, u.Sid, u.Serial, u.LogonTime), out var sessionId))
                row.MatchedSessionId = sessionId;
        }
    }

    /// <summary>保持期間を過ぎた履歴を削除する</summary>
    public async Task<int> CleanupAsync(int retentionDays, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await AppDbMaintenance.DeleteExpiredAsync(db, DateTime.Now.AddDays(-retentionDays), ct);
    }
}
