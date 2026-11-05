using OracleSessionHistory.Core.Entities;

namespace OracleSessionHistory.Core.Data;

/// <summary>セッションの検索条件（SQLite・SQL Server の両方で変換できる形で書く）</summary>
public static class SessionQueries
{
    /// <summary>
    /// 画面に既定で表示する種別（<see cref="ProcessKinds.Visible"/>）に絞る。
    /// 配列の Contains は SQL Server の互換性レベルによって変換方法が変わるため、比較を並べて書く。
    /// </summary>
    public static IQueryable<TrackedSession> WhereVisible(this IQueryable<TrackedSession> q) =>
        q.Where(x => x.ProcessKind == ProcessKinds.Client || x.ProcessKind == ProcessKinds.DbLinkSuspect);
}
