namespace OracleSessionHistory.Core.Entities;

/// <summary>
/// 接続アプリ表の行（端末・実行ファイル・アプリ・DB Link 接続元）に画面から設定する情報（日本語名・メモ・対応担当者）。
/// 行は木の上の段からの経路（<see cref="NodeKey"/>）で識別し、監視対象によらず共通にする。
/// </summary>
public class AppNodeNote
{
    /// <summary>行のキー（段ごとの「種類:名前」を改行でつないだもの。Server の ConnectionTreeKeys が作る）</summary>
    public string NodeKey { get; set; } = "";

    /// <summary>日本語の名前（例：受注管理システム）</summary>
    public string? DisplayName { get; set; }

    /// <summary>メモ（このアプリは何か、問い合わせ先など）</summary>
    public string? Memo { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>対応担当者（AD から選んだ人。メイン 1 人とサブ複数）</summary>
    public List<AppNodeAssignee> Assignees { get; set; } = [];
}

/// <summary>接続アプリ表の行の対応担当者（AD から選んだ人）</summary>
public class AppNodeAssignee
{
    public long Id { get; set; }

    public string NodeKey { get; set; } = "";

    /// <summary>メインの担当者か（false はサブ）</summary>
    public bool IsMain { get; set; }

    /// <summary>サブの並び順</summary>
    public int SortOrder { get; set; }

    /// <summary>AD のアカウント（userPrincipalName、無ければ sAMAccountName）</summary>
    public string Account { get; set; } = "";

    /// <summary>選んだ時点の AD の表示名</summary>
    public string DisplayName { get; set; } = "";
}

/// <summary><see cref="AppNodeNote"/> の列の長さ</summary>
public static class AppNodeNoteLimits
{
    /// <summary>行のキーの最大長（SQL Server の主キー 900 バイト = nvarchar 450 文字）</summary>
    public const int NodeKeyLength = 450;

    /// <summary>日本語名の最大長</summary>
    public const int DisplayNameLength = 100;

    /// <summary>メモの最大長</summary>
    public const int MemoLength = 1000;

    /// <summary>担当者のアカウント・表示名の最大長</summary>
    public const int AccountLength = 256;

    /// <summary>サブの担当者の最大人数</summary>
    public const int MaxSubAssignees = 10;
}
