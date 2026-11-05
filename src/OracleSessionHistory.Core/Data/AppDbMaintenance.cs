using Microsoft.EntityFrameworkCore;

namespace OracleSessionHistory.Core.Data;

/// <summary>
/// 複数テーブルにまたがる削除処理。
/// DbLinkUsage.MatchedSessionId は DB 側で SET NULL しない（SQL Server の制約）ため、
/// セッションを削除する前に必ずここで参照を外す。
/// </summary>
public static class AppDbMaintenance
{
    /// <summary>保持期間を過ぎた履歴を削除し、削除件数を返す</summary>
    public static async Task<int> DeleteExpiredAsync(AppDbContext db, DateTime cutoff, CancellationToken ct)
    {
        var usages = await db.DbLinkUsages.Where(u => u.LastSeenAt < cutoff).ExecuteDeleteAsync(ct);

        var expired = db.Sessions.Where(s => !s.IsConnected && s.LastSeenAt < cutoff);
        await db.DbLinkUsages
            .Where(u => u.MatchedSessionId != null && expired.Any(s => s.Id == u.MatchedSessionId))
            .ExecuteUpdateAsync(x => x.SetProperty(u => u.MatchedSessionId, (long?)null), ct);
        var sessions = await expired.ExecuteDeleteAsync(ct);
        return usages + sessions;
    }

    /// <summary>監視対象とその履歴・DB Link 接続元をまとめて削除する。対象が無ければ false</summary>
    public static async Task<bool> DeleteTargetAsync(AppDbContext db, int targetId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // 他の監視対象の利用元が、この対象のセッションを参照している場合も外す
        await db.DbLinkUsages
            .Where(u => u.MatchedSessionId != null && db.Sessions.Any(s => s.Id == u.MatchedSessionId && s.TargetId == targetId))
            .ExecuteUpdateAsync(x => x.SetProperty(u => u.MatchedSessionId, (long?)null), ct);
        // セッション・DB Link 接続元（とその利用元）は DB 側の連鎖削除で消える
        var deleted = await db.Targets.Where(t => t.Id == targetId).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
        return deleted > 0;
    }
}
