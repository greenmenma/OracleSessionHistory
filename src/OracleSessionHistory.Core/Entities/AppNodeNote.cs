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

    /// <summary>メインの対応担当者</summary>
    public string? MainAssignee { get; set; }

    /// <summary>サブの対応担当者（複数。<see cref="AppNodeNoteLimits.SubSeparator"/> 区切り）</summary>
    public string? SubAssignees { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>サブの対応担当者の一覧</summary>
    public IReadOnlyList<string> SubAssigneeList =>
        string.IsNullOrEmpty(SubAssignees) ? [] : SubAssignees.Split(AppNodeNoteLimits.SubSeparator);
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

    /// <summary>担当者 1 人の名前の最大長</summary>
    public const int AssigneeLength = 100;

    /// <summary>サブの担当者の最大人数</summary>
    public const int MaxSubAssignees = 10;

    /// <summary>サブの担当者の列の長さ（最大人数分の名前と区切り）</summary>
    public const int SubAssigneesLength = (AssigneeLength + 1) * MaxSubAssignees;

    /// <summary>サブの担当者の区切り（名前に改行は含めない）</summary>
    public const char SubSeparator = '\n';
}
