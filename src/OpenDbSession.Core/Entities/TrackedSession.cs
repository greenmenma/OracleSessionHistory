namespace OpenDbSession.Core.Entities;

/// <summary>
/// 追跡しているセッション。1 つの Oracle セッション（inst_id, sid, serial#, logon_time）につき 1 行。
/// 接続中は毎回同じ行を更新し、切断を検知したら IsConnected=false にして履歴として残す。
/// </summary>
public class TrackedSession
{
    public long Id { get; set; }
    public int TargetId { get; set; }
    public MonitorTarget? Target { get; set; }

    // ---- セッション識別キー ----
    public int InstId { get; set; }
    public int Sid { get; set; }
    public int Serial { get; set; }
    public DateTime LogonTime { get; set; }

    // ---- 利用システム特定用の情報 ----
    public long? AuditSid { get; set; }
    /// <summary>接続ユーザ（gv$session.username）</summary>
    public string? Username { get; set; }
    /// <summary>実行スキーマ（gv$session.schemaname）</summary>
    public string? SchemaName { get; set; }
    /// <summary>OS ユーザ（実行ユーザ）</summary>
    public string? OsUser { get; set; }
    /// <summary>実行端末（マシン名）</summary>
    public string? Machine { get; set; }
    public string? Terminal { get; set; }
    /// <summary>実行プログラム</summary>
    public string? Program { get; set; }
    public string? Module { get; set; }
    public string? Action { get; set; }
    public string? ClientInfo { get; set; }
    public string? ClientIdentifier { get; set; }
    public string? ServiceName { get; set; }
    /// <summary>クライアント側プロセス ID（gv$session.process）。DB Link の場合は接続元サーバープロセスの SPID。</summary>
    public string? ClientProcess { get; set; }
    /// <summary>サーバープロセス ID（gv$process.spid）</summary>
    public string? ServerProcess { get; set; }
    public int? Port { get; set; }
    /// <summary>クライアントドライバ（gv$session_connect_info.client_driver）</summary>
    public string? ClientDriver { get; set; }
    public string? ClientVersion { get; set; }
    public string? SessionType { get; set; }

    // ---- 状態 ----
    public string? Status { get; set; }
    public string? SqlId { get; set; }
    public string? LastSqlText { get; set; }
    public string? Event { get; set; }

    // ---- 分類（DB Link の可能性・ジョブ等） ----
    public string ProcessKind { get; set; } = ProcessKinds.Client;
    /// <summary>ジョブ種別（SCHEDULER / DBMS_JOB / DATAPUMP）</summary>
    public string? JobType { get; set; }
    public string? JobOwner { get; set; }
    public string? JobName { get; set; }
    public string? JobAction { get; set; }
    /// <summary>親セッション情報（パラレルスレーブの場合のコーディネータ等）</summary>
    public string? ParentInfo { get; set; }

    // ---- 追跡 ----
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public DateTime? LastActiveAt { get; set; }
    public bool IsConnected { get; set; } = true;
    /// <summary>切断時刻（推定値。最後に存在を確認した時刻）</summary>
    public DateTime? DisconnectedAt { get; set; }
    /// <summary>切断を検知した時刻</summary>
    public DateTime? DisconnectDetectedAt { get; set; }
    /// <summary>取得失敗が続いた後などで、切断時刻の精度が低い</summary>
    public bool DisconnectUncertain { get; set; }

    public List<DbLinkUsage> DbLinkOrigins { get; set; } = [];
}

/// <summary>プロセス種別</summary>
public static class ProcessKinds
{
    /// <summary>通常のクライアント接続</summary>
    public const string Client = "Client";
    /// <summary>ORACLE.EXE / oracle@host からの接続。DB Link の可能性あり</summary>
    public const string DbLinkSuspect = "DbLinkSuspect";
    /// <summary>ジョブスレーブ（Jnnn）</summary>
    public const string JobSlave = "JobSlave";
    /// <summary>パラレル実行スレーブ（Pnnn）</summary>
    public const string ParallelSlave = "ParallelSlave";
    /// <summary>AQ（アドバンスト・キューイング）スレーブ（Qnnn）</summary>
    public const string AqSlave = "AqSlave";
    /// <summary>Data Pump（DMnn / DWnn）</summary>
    public const string DataPump = "DataPump";
    /// <summary>その他 Oracle 内部プロセス</summary>
    public const string OracleInternal = "OracleInternal";
    /// <summary>BACKGROUND セッション</summary>
    public const string Background = "Background";
}
