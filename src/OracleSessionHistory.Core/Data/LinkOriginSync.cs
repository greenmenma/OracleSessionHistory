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
                        .OrderByDescending(u => u.LastSeenAt).ThenByDescending(u => u.Id).Select(u => u.AppName).FirstOrDefault()), ct);
        }
        return n;
    }
}
