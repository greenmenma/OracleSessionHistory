namespace OracleSessionHistory.Core.Entities;

/// <summary>
/// 監視対象のデータベース（例：基幹公開DB）。
/// 対象は画面から追加でき、Provider を切り替えることで Oracle 以外のソースにも拡張できる。
/// </summary>
public class MonitorTarget
{
    public int Id { get; set; }

    /// <summary>表示名（例：基幹公開DB）</summary>
    public string Name { get; set; } = "";

    /// <summary>データソースの種類（<see cref="SourceProviders"/> 参照）</summary>
    public string Provider { get; set; } = SourceProviders.Oracle;

    /// <summary>接続文字列（Oracle の場合は ODP.NET 形式）</summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>セッション取得間隔（秒）</summary>
    public int IntervalSeconds { get; set; } = 5;

    /// <summary>DB Link 利用元の取得間隔（秒）。SQL テキスト検索を伴うため長めにする。</summary>
    public int LinkIntervalSeconds { get; set; } = 30;

    /// <summary>BACKGROUND セッション（PMON 等）も記録するか</summary>
    public bool IncludeBackground { get; set; }

    /// <summary>SQL テキストを取得するか</summary>
    public bool CollectSqlText { get; set; } = true;

    public bool Enabled { get; set; } = true;

    public string? Description { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // ---- 収集状況（Batch が更新する） ----
    public DateTime? LastPollAt { get; set; }
    public DateTime? LastSuccessAt { get; set; }
    public string? LastError { get; set; }
    public int ConsecutiveFailures { get; set; }
    public int? LastSessionCount { get; set; }

    // ---- 監査証跡（DBA_AUDIT_SESSION）のログオン件数（Batch が更新する。UpdatedAt は変えない） ----
    /// <summary>監査の件数を使う期間の開始日（この日を含む）。未取得は null</summary>
    public DateTime? AuditFrom { get; set; }
    /// <summary>監査の件数を使う期間の終了日（この日を含まない）。このシステムの履歴が丸 1 日そろう最初の日</summary>
    public DateTime? AuditTo { get; set; }
    public DateTime? AuditFetchedAt { get; set; }
    /// <summary>直近の取得に失敗した理由（成功したら null）</summary>
    public string? AuditError { get; set; }

    public List<LinkSource> LinkSources { get; set; } = [];
}

/// <summary>データソース種別の定数</summary>
public static class SourceProviders
{
    public const string Oracle = "Oracle";

    /// <summary>Oracle 無しで画面確認・デバッグするための疑似データソース</summary>
    public const string Demo = "Demo";

    public static readonly string[] All = [Oracle, Demo];
}
