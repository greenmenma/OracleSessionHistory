namespace OracleSessionHistory.Core.Users;

/// <summary>アカウントから表示名を引くディレクトリ（AD 等）</summary>
public interface IUserDirectory
{
    /// <summary>
    /// アカウント（<see cref="UserAccountRules.Normalize"/> した値）ごとの表示名を返す。見つからないアカウントは値が null。
    /// ディレクトリに接続できない場合は例外。
    /// </summary>
    Task<IReadOnlyDictionary<string, string?>> LookupAsync(IReadOnlyCollection<string> accounts, CancellationToken ct);
}

/// <summary>ディレクトリに接続できない・この OS では使えない場合の例外</summary>
public sealed class UserDirectoryException(string message, Exception? inner = null) : Exception(message, inner);
