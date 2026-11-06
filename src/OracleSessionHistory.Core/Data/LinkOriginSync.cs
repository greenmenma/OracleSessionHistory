using Microsoft.EntityFrameworkCore;

namespace OracleSessionHistory.Core.Data;

/// <summary>
/// DB Link の可能性があるセッション（監視対象側）に、接続元 DB 上で特定した利用元の端末・アプリを反映する。
/// 接続アプリ表・ランキングで接続元のアプリ単位にまとめるため、利用元（DbLinkUsage）からセッションの列へ写す。
/// 利用元の記録（CollectionService）とアプリの確定（ProcessInfoService）のたびに呼ぶ。
/// </summary>
public static class LinkOriginSync
{
    private const int Chunk = 500;

    /// <summary>指定したセッションの接続元を、最後に観測した利用元の値にする（利用元が無ければ null）</summary>
    public static async Task<int> RefreshAsync(AppDbContext db, IReadOnlyCollection<long> sessionIds, CancellationToken ct)
    {
        var n = 0;
        foreach (var chunk in sessionIds.Distinct().Chunk(Chunk))
        {
            n += await db.Sessions
                .Where(s => chunk.Contains(s.Id))
                .ExecuteUpdateAsync(x => x
                    .SetProperty(s => s.LinkOriginMachine, s => s.DbLinkOrigins
                        .OrderByDescending(u => u.LastSeenAt).ThenByDescending(u => u.Id).Select(u => u.Machine).FirstOrDefault())
                    .SetProperty(s => s.LinkOriginApp, s => s.DbLinkOrigins
                        .OrderByDescending(u => u.LastSeenAt).ThenByDescending(u => u.Id).Select(u => u.AppName).FirstOrDefault())
                    .SetProperty(s => s.LinkOriginSource, s => s.DbLinkOrigins
                        .OrderByDescending(u => u.LastSeenAt).ThenByDescending(u => u.Id).Select(u => u.LinkSource!.Name).FirstOrDefault()), ct);
        }
        return n;
    }

    /// <summary>
    /// 利用元が無くなったセッション（DB Link 接続元の削除・保持期間による利用元の削除の後）の接続元を外す。
    /// 外さないと、接続アプリ表では接続元のアプリとして数え続けるのに、詳細・DB Link 利用元画面には出ず食い違う。
    /// </summary>
    public static Task<int> ClearOrphansAsync(AppDbContext db, CancellationToken ct) =>
        db.Sessions
            .Where(s => s.LinkOriginApp != null && !s.DbLinkOrigins.Any())
            .ExecuteUpdateAsync(x => x
                .SetProperty(s => s.LinkOriginMachine, (string?)null)
                .SetProperty(s => s.LinkOriginApp, (string?)null)
                .SetProperty(s => s.LinkOriginSource, (string?)null), ct);
}
