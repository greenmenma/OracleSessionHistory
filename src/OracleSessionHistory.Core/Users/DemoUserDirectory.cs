namespace OracleSessionHistory.Core.Users;

/// <summary>Windows 以外（デバッグ用）の疑似ディレクトリ。デモの実行ユーザに表示名を返す</summary>
public sealed class DemoUserDirectory : IUserDirectory
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["yamada"] = "山田 太郎", ["suzuki"] = "鈴木 花子", ["tanaka"] = "田中 一郎", ["sato"] = "佐藤 次郎",
        ["pc-sales01"] = "山田 太郎", ["pc-sales02"] = "鈴木 花子", ["pc-sales03"] = "田中 一郎",
        ["jp1admin"] = "JP1 管理者", ["ope01"] = "運用 担当01", ["dev07"] = "開発 担当07", ["dev08"] = "開発 担当08",
        ["svc_osh"] = "接続履歴サービス", ["kikan02"] = "基幹 担当02", ["batch"] = "基幹バッチ",
    };

    /// <summary>デモの人（担当者の検索用）。アカウントは user@demo.local</summary>
    private static readonly DirectoryPerson[] People =
    [
        new("yamada@demo.local", "山田 太郎", "情報システム課", "yamada@demo.local"),
        new("suzuki@demo.local", "鈴木 花子", "情報システム課", "suzuki@demo.local"),
        new("tanaka@demo.local", "田中 一郎", "営業部", "tanaka@demo.local"),
        new("sato@demo.local", "佐藤 次郎", "経理部", "sato@demo.local"),
        new("takahashi@demo.local", "高橋 三郎", "情報システム課", "takahashi@demo.local"),
        new("ito@demo.local", "伊藤 美咲", "製造部", "ito@demo.local"),
    ];

    public Task<IReadOnlyList<DirectoryPerson>> SearchAsync(string query, int max, CancellationToken ct)
    {
        var q = query.Trim();
        return Task.FromResult<IReadOnlyList<DirectoryPerson>>(People
            .Where(p => p.DisplayName.Replace(" ", "").Contains(q.Replace(" ", ""), StringComparison.OrdinalIgnoreCase)
                        || p.Account.StartsWith(q, StringComparison.OrdinalIgnoreCase))
            .Take(max).ToList());
    }

    public Task<IReadOnlyDictionary<string, string?>> LookupAsync(IReadOnlyCollection<string> accounts, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, string?>>(accounts.ToDictionary(a => a, a =>
        {
            var user = UserAccountRules.Parse(a)?.User;
            var key = user?.Split('@')[0] ?? "";
            return Names.TryGetValue(key, out var name) ? name : null;
        }, StringComparer.OrdinalIgnoreCase));
}
