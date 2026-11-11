namespace OracleSessionHistory.Core.Entities;

/// <summary>
/// 監視対象 DB の監査証跡（DBA_AUDIT_SESSION）から集計した、日・端末ごとのログオン件数。
/// 接続アプリ表の端末の行の件数（ログオン数）に使う（Batch が更新する）。
/// </summary>
public class AuditLogonCount
{
    public long Id { get; set; }
    public int TargetId { get; set; }

    /// <summary>ログオン日（DB サーバーの時刻の 0:00）</summary>
    public DateTime Day { get; set; }

    /// <summary>端末（DBA_AUDIT_SESSION.USERHOST。前後の空白を除く。無い場合は空文字）</summary>
    public string Machine { get; set; } = "";

    /// <summary>成功したログオンの件数</summary>
    public int Count { get; set; }

    public MonitorTarget? Target { get; set; }
}

/// <summary>DBA_AUDIT_SESSION から取得した 1 件（日・端末ごとの件数）</summary>
public sealed record AuditLogonDay(DateTime Day, string? Machine, int Count);
