namespace OracleSessionHistory.Core.Entities;

/// <summary>
/// AD（Active Directory）から取得した、実行ユーザ（OSUSER・プロセスの実行ユーザ）の表示名。
/// Batch の UserNameService が更新し、画面はアカウントの代わりに表示名を出す。
/// </summary>
public class DirectoryUser
{
    /// <summary>アカウント（DOMAIN\user・user@domain・user。前後の空白を除き大文字にした値）</summary>
    public string Account { get; set; } = "";

    /// <summary>AD の displayName（見つからない・未設定の場合は null）</summary>
    public string? DisplayName { get; set; }

    /// <summary>AD でアカウントが見つかったか</summary>
    public bool Found { get; set; }

    public DateTime ResolvedAt { get; set; }
}
