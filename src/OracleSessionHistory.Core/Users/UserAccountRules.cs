namespace OracleSessionHistory.Core.Users;

/// <summary>実行ユーザのアカウントの規則（AD に問い合わせるか・形式の判別。DB・OS に依存しない純粋関数）</summary>
public static class UserAccountRules
{
    /// <summary>Windows の組み込みアカウント・仮想アカウントのドメイン（AD のユーザではない）</summary>
    private static readonly string[] BuiltinDomains =
        ["IIS APPPOOL", "NT AUTHORITY", "NT SERVICE", "WINDOW MANAGER", "FONT DRIVER HOST", "NT VIRTUAL MACHINE", "BUILTIN"];

    private static readonly string[] BuiltinUsers = ["SYSTEM", "LOCAL SERVICE", "NETWORK SERVICE"];

    /// <summary>表示名の対応表のキー（前後の空白を除いて大文字にする。空なら null）</summary>
    public static string? Normalize(string? account)
    {
        var a = account?.Trim();
        return string.IsNullOrEmpty(a) ? null : a.ToUpperInvariant();
    }

    /// <summary>AD に問い合わせるアカウントか（組み込みアカウント・コンピュータアカウントは除く）</summary>
    public static bool IsDirectoryAccount(string? account)
    {
        if (Parse(account) is not { } p) return false;
        if (p.Domain is { } d && BuiltinDomains.Contains(d, StringComparer.OrdinalIgnoreCase)) return false;
        if (BuiltinUsers.Contains(p.User, StringComparer.OrdinalIgnoreCase)) return false;
        // コンピュータアカウント（HOST$）
        return !p.User.EndsWith('$');
    }

    /// <summary>
    /// アカウントを分ける。DOMAIN\user は (DOMAIN, user, false)、user@domain は (domain, user@domain, true)、user は (null, user, false)
    /// </summary>
    public static (string? Domain, string User, bool IsUpn)? Parse(string? account)
    {
        var a = account?.Trim();
        if (string.IsNullOrEmpty(a)) return null;
        var bs = a.IndexOf('\\');
        if (bs >= 0)
        {
            var domain = a[..bs].Trim();
            var user = a[(bs + 1)..].Trim();
            return user.Length == 0 ? null : (domain.Length == 0 ? null : domain, user, false);
        }
        var at = a.LastIndexOf('@');
        if (at > 0 && at < a.Length - 1) return (a[(at + 1)..], a, true);
        return (null, a, false);
    }

    /// <summary>LDAP の検索フィルタに埋め込む値のエスケープ（RFC 4515）</summary>
    public static string EscapeLdap(string value)
    {
        var sb = new System.Text.StringBuilder(value.Length);
        foreach (var c in value)
        {
            sb.Append(c switch
            {
                '\\' => @"\5c",
                '*' => @"\2a",
                '(' => @"\28",
                ')' => @"\29",
                '\0' => @"\00",
                _ => c.ToString(),
            });
        }
        return sb.ToString();
    }
}
