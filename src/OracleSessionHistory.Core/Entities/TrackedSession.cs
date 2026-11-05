namespace OracleSessionHistory.Core.Entities;

/// <summary>
/// 追跡しているセッション。1 つの Oracle セッション（inst_id, sid, serial#, logon_time）につき 1 行。
/// 接続中は毎回同じ行を更新し、切断を検知したら IsConnected=false にして履歴として残す。
/// </summary>
public class TrackedSession : IProcessInfoRecord
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

    // ---- 接続アプリの特定（接続元サーバーのプロセス情報） ----
    /// <summary>アプリ名（一覧・集計のキー）。プロセス情報が取れれば「IIS: プール名」等、取れなければ PROGRAM</summary>
    public string AppName { get; set; } = "";
    /// <summary>アプリの補足（IIS のサイト／パス、実行ファイルのフォルダ等）</summary>
    public string? AppDetail { get; set; }
    /// <summary>プロセス情報の取得状態（<see cref="ProcInfoStates"/>）</summary>
    public string ProcInfoState { get; set; } = ProcInfoStates.Pending;
    /// <summary>プロセス情報を取得できなかった理由</summary>
    public string? ProcInfoError { get; set; }
    /// <summary>プロセス情報の取得を試みた時刻</summary>
    public DateTime? ProcInfoAt { get; set; }
    public string? ProcName { get; set; }
    public string? ProcPath { get; set; }
    public string? ProcCommandLine { get; set; }
    /// <summary>プロセスの実行ユーザ（DOMAIN\User）</summary>
    public string? ProcOwner { get; set; }
    public DateTime? ProcStartedAt { get; set; }
    public string? ProcParentName { get; set; }
    /// <summary>実行元（親から順に 1 行ずつ「名前: コマンドライン」。パスワードは隠す）</summary>
    public string? ProcAncestry { get; set; }

    /// <summary>
    /// DB Link の可能性があるセッションで、接続元 DB 上で特定した利用元の端末・アプリ（最後に観測したもの。<c>LinkOriginSync</c> が更新）。
    /// 接続アプリ表・ランキング・完全一致の検索は、この値がある行を接続元の端末・アプリとして扱う。
    /// </summary>
    public string? LinkOriginMachine { get; set; }
    public string? LinkOriginApp { get; set; }
    public string? IisAppPool { get; set; }
    public string? IisSite { get; set; }

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
    /// <summary>このシステム自身（OracleSessionHistory の Batch・Server）の接続。既定で非表示</summary>
    public const string Self = "Self";

    /// <summary>
    /// 画面に既定で表示する種別（接続アプリの特定に必要なもの）。それ以外の Oracle 内部プロセスは既定で非表示。
    /// 変更する場合は <see cref="Data.SessionQueries.WhereVisible"/> も合わせて変更する。
    /// </summary>
    public static readonly IReadOnlyList<string> Visible = [Client, DbLinkSuspect];
}

/// <summary>プロセス情報の取得状態</summary>
public static class ProcInfoStates
{
    /// <summary>未取得</summary>
    public const string Pending = "Pending";
    /// <summary>取得済み</summary>
    public const string Resolved = "Resolved";
    /// <summary>問い合わせは成功したがプロセスが存在しない（終了済み）</summary>
    public const string NotFound = "NotFound";
    /// <summary>PID の再利用等で別プロセスと判断した</summary>
    public const string Mismatch = "Mismatch";
    /// <summary>接続できない・アクセス拒否など（時間をおいて再試行）</summary>
    public const string Failed = "Failed";
    /// <summary>対象外（PID・端末名なし、対象外端末、DB Link・Oracle 内部プロセス等）</summary>
    public const string NotApplicable = "NotApplicable";
}
