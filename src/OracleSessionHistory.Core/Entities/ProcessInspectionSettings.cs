namespace OracleSessionHistory.Core.Entities;

/// <summary>
/// 接続元サーバーのプロセス情報（WMI/CIM）を取得するための設定。1 行だけ持つ。
/// アカウントは全サーバー共通のドメインアカウント。空の場合は Batch の実行アカウントで接続する。
/// </summary>
public class ProcessInspectionSettings
{
    /// <summary>常に <see cref="SingletonId"/></summary>
    public int Id { get; set; }
    public const int SingletonId = 1;

    public bool Enabled { get; set; }

    /// <summary>接続に使うアカウント（DOMAIN\user または user@domain）。空なら Batch の実行アカウント</summary>
    public string? UserName { get; set; }

    /// <summary>パスワード。接続文字列と同様に保存し、API では返さない</summary>
    public string? Password { get; set; }

    /// <summary>端末名で名前解決できない場合に付ける DNS サフィックス（例：corp.example.local）</summary>
    public string? DnsSuffix { get; set; }

    /// <summary>1 サーバーあたりのタイムアウト（秒）</summary>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>プロセス情報を取得しない端末のパターン（カンマ区切り、* はワイルドカード）</summary>
    public string? ExcludeMachines { get; set; }

    /// <summary>
    /// プロセス情報を取得する実行ファイル（gv$session.program に対するパターン。カンマ区切り、* はワイルドカード）。
    /// 監視対象への直接の接続は、ここに一致するものだけ調べる。空なら調べない。DB Link 利用元は常に調べる。
    /// </summary>
    public string? TargetPrograms { get; set; }

    public DateTime UpdatedAt { get; set; }
}
