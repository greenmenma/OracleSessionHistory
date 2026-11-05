namespace OracleSessionHistory.Core.Entities;

/// <summary>
/// DB Link 接続元 DB 上で、DB Link を使用していたセッション。
/// 1 セッション × 1 DB Link につき 1 行で、観測のたびに更新する。
/// </summary>
public class DbLinkUsage : IProcessInfoRecord
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
    /// <summary>接続元 DB のサーバープロセス（gv$process.spid）</summary>
    public string? ServerProcess { get; set; }
    /// <summary>接続元セッションのクライアントプロセス（gv$session.process。Windows は "pid:tid"）</summary>
    public string? ClientProcess { get; set; }
    public string? SqlId { get; set; }
    public string? SqlText { get; set; }

    // ---- 接続元端末のプロセス情報（ProcessInfoService が取得。docs/process-info.md） ----
    /// <summary>アプリ名（プロセス情報が取れるまでは PROGRAM）</summary>
    public string AppName { get; set; } = "";
    public string? AppDetail { get; set; }
    /// <summary>プロセス情報の取得状態（<see cref="ProcInfoStates"/>）</summary>
    public string ProcInfoState { get; set; } = ProcInfoStates.Pending;
    public string? ProcInfoError { get; set; }
    public DateTime? ProcInfoAt { get; set; }
    public string? ProcName { get; set; }
    public string? ProcPath { get; set; }
    public string? ProcCommandLine { get; set; }
    public string? ProcOwner { get; set; }
    public DateTime? ProcStartedAt { get; set; }
    public string? ProcParentName { get; set; }
    public string? IisAppPool { get; set; }
    public string? IisSite { get; set; }

    /// <summary>DB Link の検索では取得しない（JDBC の判定は PROGRAM で行う）</summary>
    string? IProcessInfoRecord.ClientDriver => null;

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
