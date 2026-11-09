using System.DirectoryServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace OracleSessionHistory.Core.Users;

/// <summary>
/// AD（LDAP）から displayName を取得する（Windows のみ）。Batch の実行アカウントで接続する。
/// DOMAIN\user はそのドメイン（NetBIOS 名で DC を探す）の sAMAccountName、user@domain は userPrincipalName、
/// user だけの場合は実行アカウントのドメインの sAMAccountName で探す。
/// </summary>
public sealed class AdUserDirectory(ILogger<AdUserDirectory> logger) : IUserDirectory
{
    /// <summary>1 回の LDAP 検索に含めるアカウントの数</summary>
    private const int Chunk = 50;

    public Task<IReadOnlyDictionary<string, string?>> LookupAsync(IReadOnlyCollection<string> accounts, CancellationToken ct)
    {
        // System.DirectoryServices は同期 API のため、スレッドプールで実行する
        return Task.Run(() => OperatingSystem.IsWindows()
            ? Lookup(accounts, ct)
            : throw new UserDirectoryException("この OS では AD に問い合わせできません（Windows のみ）"), ct);
    }

    [SupportedOSPlatform("windows")]
    private IReadOnlyDictionary<string, string?> Lookup(IReadOnlyCollection<string> accounts, CancellationToken ct)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var parsed = accounts.Select(a => (Account: a, Parsed: UserAccountRules.Parse(a))).Where(x => x.Parsed is not null)
            .Select(x => (x.Account, Domain: x.Parsed!.Value.IsUpn ? null : x.Parsed.Value.Domain, x.Parsed.Value.User, x.Parsed.Value.IsUpn))
            .ToList();
        Exception? firstError = null;
        var anySuccess = false;
        // ドメインごとに検索する（UPN・ドメインなしは既定のドメイン）
        foreach (var domain in parsed.GroupBy(p => p.Domain?.ToUpperInvariant()))
        {
            foreach (var chunk in domain.Chunk(Chunk))
            {
                ct.ThrowIfCancellationRequested();
                foreach (var a in chunk) result[a.Account] = null;
                try
                {
                    using var root = new DirectoryEntry(domain.Key is null ? "" : "LDAP://" + domain.Key);
                    var terms = string.Concat(chunk.Select(a => a.IsUpn
                        ? $"(userPrincipalName={UserAccountRules.EscapeLdap(a.User)})"
                        : $"(sAMAccountName={UserAccountRules.EscapeLdap(a.User)})"));
                    using var searcher = new DirectorySearcher(root, $"(&(objectCategory=person)(objectClass=user)(|{terms}))",
                        ["sAMAccountName", "userPrincipalName", "displayName"])
                    {
                        PageSize = Chunk,
                        ClientTimeout = TimeSpan.FromSeconds(30),
                        ServerTimeLimit = TimeSpan.FromSeconds(30),
                    };
                    using var found = searcher.FindAll();
                    foreach (SearchResult r in found)
                    {
                        var sam = First(r, "sAMAccountName");
                        var upn = First(r, "userPrincipalName");
                        var name = First(r, "displayName");
                        foreach (var a in chunk.Where(a => a.IsUpn
                                     ? string.Equals(a.User, upn, StringComparison.OrdinalIgnoreCase)
                                     : string.Equals(a.User, sam, StringComparison.OrdinalIgnoreCase)))
                            result[a.Account] = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
                    }
                    anySuccess = true;
                }
                catch (Exception ex) when (ex is COMException or DirectoryServicesCOMException or InvalidOperationException)
                {
                    // 存在しないドメイン（ローカルアカウントの PC 名等）は、そのドメインのアカウントを「見つからない」とする
                    logger.LogDebug(ex, "{Domain} の AD を検索できませんでした", domain.Key ?? "(既定のドメイン)");
                    firstError ??= ex;
                }
            }
        }
        // 1 件も検索できなかった場合は AD に接続できないものとして扱う（見つからないとは保存しない）
        if (!anySuccess && firstError is not null)
            throw new UserDirectoryException($"AD に接続できません: {firstError.Message}", firstError);
        return result;
    }

    public Task<IReadOnlyList<DirectoryPerson>> SearchAsync(string query, int max, CancellationToken ct) =>
        Task.Run(() => OperatingSystem.IsWindows()
            ? Search(query, max)
            : throw new UserDirectoryException("この OS では AD に問い合わせできません（Windows のみ）"), ct);

    /// <summary>
    /// 実行アカウントのドメインで、有効なユーザを探す。anr（名前・姓・名・アカウント・メールの前方一致）と表示名の部分一致
    /// </summary>
    [SupportedOSPlatform("windows")]
    private IReadOnlyList<DirectoryPerson> Search(string query, int max)
    {
        var q = UserAccountRules.EscapeLdap(query.Trim());
        try
        {
            using var root = new DirectoryEntry("");
            using var searcher = new DirectorySearcher(root,
                $"(&(objectCategory=person)(objectClass=user)(!(userAccountControl:1.2.840.113556.1.4.803:=2))(|(anr={q})(displayName=*{q}*)))",
                ["sAMAccountName", "userPrincipalName", "displayName", "department", "mail"])
            {
                SizeLimit = max,
                ClientTimeout = TimeSpan.FromSeconds(10),
                ServerTimeLimit = TimeSpan.FromSeconds(10),
            };
            searcher.Sort = new SortOption("displayName", SortDirection.Ascending);
            using var found = searcher.FindAll();
            var people = new List<DirectoryPerson>();
            foreach (SearchResult r in found)
            {
                var account = First(r, "userPrincipalName") ?? First(r, "sAMAccountName");
                if (string.IsNullOrWhiteSpace(account)) continue;
                var name = First(r, "displayName");
                people.Add(new DirectoryPerson(account.Trim(), string.IsNullOrWhiteSpace(name) ? account.Trim() : name.Trim(),
                    First(r, "department"), First(r, "mail")));
            }
            return people;
        }
        catch (Exception ex) when (ex is COMException or DirectoryServicesCOMException or InvalidOperationException)
        {
            logger.LogWarning(ex, "AD でユーザを検索できませんでした（{Query}）", query);
            throw new UserDirectoryException($"AD に接続できません: {ex.Message}", ex);
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? First(SearchResult r, string name) =>
        r.Properties[name] is { Count: > 0 } v ? v[0]?.ToString() : null;
}
