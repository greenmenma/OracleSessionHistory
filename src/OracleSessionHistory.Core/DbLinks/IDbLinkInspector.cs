using OracleSessionHistory.Core.Entities;

namespace OracleSessionHistory.Core.DbLinks;

/// <summary>接続元 DB 上の DB Link 定義</summary>
public sealed record DbLinkInfo(string Owner, string DbLink, string? Username, string? Host, DateTime? Created)
{
    /// <summary>OWNER.DB_LINK 形式のキー</summary>
    public string FullName => $"{Owner}.{DbLink}";
}

/// <summary>接続元 DB 上で DB Link を使用していたセッション</summary>
public sealed record ObservedLinkUsage
{
    public required string DbLinkName { get; init; }
    public required string MatchType { get; init; }
    public int InstId { get; init; }
    public int Sid { get; init; }
    public int Serial { get; init; }
    public DateTime LogonTime { get; init; }
    public string? Username { get; init; }
    public string? SchemaName { get; init; }
    public string? OsUser { get; init; }
    public string? Machine { get; init; }
    public string? Terminal { get; init; }
    public string? Program { get; init; }
    public string? Module { get; init; }
    public string? Action { get; init; }
    public string? ServerProcess { get; init; }
    /// <summary>gv$session.process（接続元端末のクライアント PID）</summary>
    public string? ClientProcess { get; init; }
    public string? SqlId { get; init; }
    public string? SqlText { get; init; }
}

/// <summary>DB Link 利用元の検索結果</summary>
/// <param name="Warnings">一部の検索ができなかった場合の警告（権限不足等）。検索結果は返す</param>
public sealed record DbLinkUsageResult(IReadOnlyList<ObservedLinkUsage> Usages, IReadOnlyList<string> Warnings);

/// <summary>
/// DB Link の接続元 DB を調べる。
/// ・DB Link 一覧の取得
/// ・選択した DB Link を SQL で使っているセッションの取得
/// ・監視対象側のセッションの process と一致するサーバープロセスを持つセッションの取得
/// </summary>
public interface IDbLinkInspector
{
    string Provider { get; }

    Task<IReadOnlyList<DbLinkInfo>> ListLinksAsync(string connectionString, CancellationToken ct);

    /// <param name="source">接続元 DB の設定</param>
    /// <param name="processIds">監視対象側の DB Link 疑いセッションの gv$session.process（接続元の spid）</param>
    /// <exception cref="Exception">接続できない・必須のビューを参照できない場合</exception>
    Task<DbLinkUsageResult> FindUsagesAsync(LinkSource source, IReadOnlyCollection<string> processIds, CancellationToken ct);
}

public sealed class DbLinkInspectorFactory(IEnumerable<IDbLinkInspector> inspectors)
{
    private readonly Dictionary<string, IDbLinkInspector> _map =
        inspectors.ToDictionary(s => s.Provider, StringComparer.OrdinalIgnoreCase);

    public IDbLinkInspector Get(string provider) =>
        _map.TryGetValue(provider, out var s) ? s : throw new NotSupportedException($"未対応の Provider です: {provider}");
}
