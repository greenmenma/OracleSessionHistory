namespace OpenDbSession.Core.Entities;

/// <summary>
/// DB Link の接続元となるデータベース。
/// 監視対象に ORACLE.EXE（DB Link の可能性）からの接続がある場合に、接続元 DB の接続文字列を設定し、
/// 利用している DB Link を選択すると、その DB Link を使っているセッション（端末・プログラム・ユーザ）を収集する。
/// </summary>
public class LinkSource
{
    public int Id { get; set; }
    public int TargetId { get; set; }
    public MonitorTarget? Target { get; set; }

    public string Name { get; set; } = "";
    public string Provider { get; set; } = SourceProviders.Oracle;
    public string ConnectionString { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>監視する DB Link 名（OWNER.DB_LINK 形式。PUBLIC の場合は PUBLIC.DB_LINK）</summary>
    public List<string> SelectedLinks { get; set; } = [];

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public DateTime? LastPollAt { get; set; }
    public DateTime? LastSuccessAt { get; set; }
    public string? LastError { get; set; }
}
