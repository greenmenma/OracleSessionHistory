using OracleSessionHistory.Server.Support;

namespace OracleSessionHistory.Server.Api;

/// <summary>接続アプリ表の Excel 出力（アプリ別・アプリ別の経路・端末別のシート）</summary>
public static class ConnectionExport
{
    private static readonly Dictionary<string, string> KindNames = new()
    {
        [ConnectionCountKinds.Machine] = "端末",
        [ConnectionCountKinds.Exe] = "実行ファイル",
        [ConnectionCountKinds.App] = "アプリ",
        [ConnectionCountKinds.DbLink] = "DB Link 接続元",
        [ConnectionCountKinds.Other] = "区別なし",
    };

    internal static byte[] Create(ReportEndpoints.ConnectionTables t, IReadOnlyDictionary<string, string> userNames)
    {
        var counts = CountHeaders(t.Buckets);
        string Users(IEnumerable<string> accounts) => string.Join("、", accounts.Select(a => UserLabel(a, userNames)));

        // アプリ別：同じ名前のアプリを 1 行
        var apps = t.AppView.Select(a => (IReadOnlyList<object?>)
        [
            a.Label, a.Note?.DisplayName, a.Detail, a.Children.Count, People(a.PathAssignees ?? []), Users(a.Owners),
            a.Connected, a.LastSeenAt, .. Counts(a),
        ]).ToList();

        // アプリ別の経路：アプリごとの経路（端末別の最下段の行）
        var paths = t.AppView.SelectMany(a => a.Children.Select(p => (IReadOnlyList<object?>)
        [
            a.Label, a.Note?.DisplayName, p.Label, p.Note?.DisplayName, p.Note?.Memo,
            p.Note?.MainAssignee is { } m ? People([m]) : null, People(p.Note?.SubAssignees ?? []), Users(p.Owners),
            p.Connected, p.LastSeenAt, .. Counts(p),
        ])).ToList();

        // 端末別：木を上から順に 1 行ずつ（名前は段に合わせて字下げ）
        var machines = new List<IReadOnlyList<object?>>();
        void Walk(IEnumerable<ConnectionCountNode> nodes, int depth)
        {
            foreach (var n in nodes)
            {
                var periods = n.Kind == ConnectionCountKinds.Machine && depth == 0 ? SourceOf(n, t) : null;
                machines.Add(
                [
                    depth + 1, new XlsxIndented(n.Label, depth), KindNames.GetValueOrDefault(n.Kind, n.Kind), n.Note?.DisplayName, n.Detail, n.Note?.Memo,
                    n.Note?.MainAssignee is { } m ? People([m]) : null, People(n.Note?.SubAssignees ?? []), Users(n.Owners),
                    n.Connected, n.LastSeenAt, .. Counts(n), ReportEndpoints.PathLabel(n.Key), periods,
                ]);
                Walk(n.Children, depth + 1);
            }
        }
        Walk(t.MachineView, 0);

        return XlsxWriter.Write(
        [
            new("アプリ別", ["アプリ", "日本語名", "種類", "経路数", "担当者（経路）", "実行ユーザ", "接続中", "最終確認", .. counts], apps,
                [32, 24, 22, 8, 24, 32, 8, 20, .. counts.Select(_ => 11.0)]),
            new("アプリ別の経路", ["アプリ", "アプリの日本語名", "経路", "経路の日本語名", "メモ", "担当者（メイン）", "担当者（サブ）", "実行ユーザ", "接続中", "最終確認", .. counts], paths,
                [28, 20, 60, 20, 30, 22, 30, 32, 8, 20, .. counts.Select(_ => 11.0)]),
            new("端末別", ["段", "端末 / アプリ", "種類", "日本語名", "補足", "メモ", "担当者（メイン）", "担当者（サブ）", "実行ユーザ", "接続中", "最終確認", .. counts, "経路", "端末の件数"], machines,
                [5, 40, 12, 20, 30, 30, 22, 30, 32, 8, 20, .. counts.Select(_ => 11.0), 60, 40]),
        ]);
    }

    /// <summary>件数の列の見出し（日付付き）</summary>
    internal static List<string> CountHeaders(ReportEndpoints.Buckets b) =>
    [
        $"本日 {b.Today:MM/dd}", $"1日前 {b.D1:MM/dd}", $"2日前 {b.D2:MM/dd}", $"3日前 {b.D3:MM/dd}", $"4日前 {b.D4:MM/dd}",
        $"5日前 {b.D5:MM/dd}", $"6日前 {b.D6:MM/dd}", "本日〜1ヶ月", "1ヶ月〜2ヶ月",
    ];

    private static object?[] Counts(ConnectionCountNode n) => [n.Today, n.D1, n.D2, n.D3, n.D4, n.D5, n.D6, n.Month1, n.Month2];

    /// <summary>端末の行の件数の出典（監査証跡の件数を使った列）</summary>
    private static string SourceOf(ConnectionCountNode n, ReportEndpoints.ConnectionTables t)
    {
        if (t.Audit.Count == 0) return "履歴の件数";
        if (n.AuditPeriods.Count == 0) return "履歴の件数（監査証跡の件数なし）";
        var headers = CountHeaders(t.Buckets);
        var keys = t.Buckets.Periods.Select(p => p.Key).ToList();
        return "監査証跡の件数：" + string.Join("・", n.AuditPeriods.Select(k => keys.IndexOf(k) is var i and >= 0 ? headers[i] : k));
    }

    private static string? People(IEnumerable<AssigneeDto> people) =>
        string.Join("、", people.Select(p => $"{p.DisplayName}（{p.Account}）")) is { Length: > 0 } s ? s : null;

    /// <summary>「表示名（アカウント）」。表示名が無ければアカウント（画面の UserList と同じ）</summary>
    private static string UserLabel(string account, IReadOnlyDictionary<string, string> userNames) =>
        userNames.TryGetValue(account.Trim().ToUpperInvariant(), out var name) ? $"{name}（{account}）" : account;
}
