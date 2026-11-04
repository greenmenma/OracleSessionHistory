namespace OpenDbSession.Server.Support;

public static class QueryHelpers
{
    /// <summary>LIKE 用に % _ をエスケープして部分一致パターンにする</summary>
    public static string Contains(string value) =>
        "%" + value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);
