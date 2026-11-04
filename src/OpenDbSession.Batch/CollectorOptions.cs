namespace OpenDbSession.Batch;

/// <summary>Batch の設定（appsettings.json の "Collector" セクション）</summary>
public sealed class CollectorOptions
{
    /// <summary>監視対象の設定変更を確認する間隔（秒）</summary>
    public int ReloadSeconds { get; set; } = 10;

    /// <summary>履歴の保持日数（接続数表で 2 ヶ月分を使うため 70 日以上を推奨）</summary>
    public int RetentionDays { get; set; } = 100;

    /// <summary>監視対象が 1 件も無い場合にデモ用の監視対象を登録する</summary>
    public bool SeedDemoTarget { get; set; }
}
