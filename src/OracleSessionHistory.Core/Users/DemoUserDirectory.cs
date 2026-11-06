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

    public Task<IReadOnlyDictionary<string, string?>> LookupAsync(IReadOnlyCollection<string> accounts, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, string?>>(accounts.ToDictionary(a => a, a =>
        {
            var user = UserAccountRules.Parse(a)?.User;
            var key = user?.Split('@')[0] ?? "";
            return Names.TryGetValue(key, out var name) ? name : null;
        }, StringComparer.OrdinalIgnoreCase));
}
