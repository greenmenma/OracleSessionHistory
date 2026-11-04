namespace OpenDbSession.Core.Entities;

/// <summary>
/// DB Link 接続元 DB 上で、DB Link を使用していたセッション。
/// 1 セッション × 1 DB Link につき 1 行で、観測のたびに更新する。
/// </summary>
public class DbLinkUsage
{
    public long Id { get; set; }
    public int LinkSourceId { get; set; }
    public LinkSource? LinkSource { get; set; }
    public int TargetId { get; set; }

    /// <summary>使用された DB Link（プロセス一致のみで特定した場合は "(PROCESS)"）</summary>
    public string DbLinkName { get; set; } = "";

    /// <summary>特定方法（<see cref="DbLinkMatchTypes"/>）</summary>
    public string MatchType { get; set; } = DbLinkMatchTypes.Sql;

    // ---- 接続元セッションの識別キー ----
    public int InstId { get; set; }
    public int Sid { get; set; }
    public int Serial { get; set; }
    public DateTime LogonTime { get; set; }

    // ---- 接続元セッションの利用システム特定用の情報 ----
    public string? Username { get; set; }
    public string? SchemaName { get; set; }
    public string? OsUser { get; set; }
    public string? Machine { get; set; }
    public string? Terminal { get; set; }
    public string? Program { get; set; }
    public string? Module { get; set; }
    public string? Action { get; set; }
    public string? ServerProcess { get; set; }
    public string? SqlId { get; set; }
    public string? SqlText { get; set; }

    /// <summary>監視対象側の対応セッション（プロセス ID で一致した場合）</summary>
    public long? MatchedSessionId { get; set; }
    public TrackedSession? MatchedSession { get; set; }

    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public int HitCount { get; set; }
}

public static class DbLinkMatchTypes
{
    /// <summary>SQL テキスト中の @DBLINK で一致</summary>
    public const string Sql = "SQL";
    /// <summary>DB Link を参照するシノニム名で一致</summary>
    public const string Synonym = "SYNONYM";
    /// <summary>監視対象側セッションの process と接続元の spid で一致（最も確度が高い）</summary>
    public const string Process = "PROCESS";

    public const string ProcessOnlyLinkName = "(PROCESS)";
}
