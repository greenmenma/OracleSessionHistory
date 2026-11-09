namespace OracleSessionHistory.Core.Entities;

/// <summary>
/// 接続アプリ表の行（端末・実行ファイル・アプリ・DB Link 接続元）の対応担当者。
/// 行は木の上の段からの経路（<see cref="NodeKey"/>）で識別し、監視対象によらず共通にする。
/// </summary>
public class AppAssignee
{
    /// <summary>行のキー（段ごとの「種類:名前」を改行でつないだもの。Server の ConnectionTreeKeys が作る）</summary>
    public string NodeKey { get; set; } = "";

    /// <summary>対応担当者（自由入力）</summary>
    public string Assignee { get; set; } = "";

    public DateTime UpdatedAt { get; set; }
}

/// <summary><see cref="AppAssignee"/> の列の長さ</summary>
public static class AppAssigneeLimits
{
    /// <summary>行のキーの最大長（SQL Server の主キー 900 バイト = nvarchar 450 文字）</summary>
    public const int NodeKeyLength = 450;

    /// <summary>対応担当者の最大長</summary>
    public const int AssigneeLength = 100;
}
