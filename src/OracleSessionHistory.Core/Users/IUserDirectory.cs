namespace OracleSessionHistory.Core.Users;

/// <summary>アカウントから表示名を引くディレクトリ（AD 等）</summary>
public interface IUserDirectory
{
    /// <summary>
    /// アカウント（<see cref="UserAccountRules.Normalize"/> した値）ごとの表示名を返す。見つからないアカウントは値が null。
    /// ディレクトリに接続できない場合は例外。
    /// </summary>
    Task<IReadOnlyDictionary<string, string?>> LookupAsync(IReadOnlyCollection<string> accounts, CancellationToken ct);

    /// <summary>
    /// 名前・アカウント・メールアドレスで人（有効なユーザ）を探す（接続アプリ表の担当者の選択）。最大 <paramref name="max"/> 件。
    /// ディレクトリに接続できない場合は例外。
    /// </summary>
    Task<IReadOnlyList<DirectoryPerson>> SearchAsync(string query, int max, CancellationToken ct);
}

/// <summary>ディレクトリで見つけた人</summary>
/// <param name="Account">アカウント（userPrincipalName、無ければ sAMAccountName）</param>
/// <param name="DisplayName">表示名（displayName。空ならアカウント）</param>
/// <param name="Department">部署（department）</param>
/// <param name="Mail">メールアドレス（mail）</param>
public sealed record DirectoryPerson(string Account, string DisplayName, string? Department, string? Mail);

/// <summary>ディレクトリに接続できない・この OS では使えない場合の例外</summary>
public sealed class UserDirectoryException(string message, Exception? inner = null) : Exception(message, inner);
