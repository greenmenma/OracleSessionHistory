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

    [SupportedOSPlatform("windows")]
    private static string? First(SearchResult r, string name) =>
        r.Properties[name] is { Count: > 0 } v ? v[0]?.ToString() : null;
}
