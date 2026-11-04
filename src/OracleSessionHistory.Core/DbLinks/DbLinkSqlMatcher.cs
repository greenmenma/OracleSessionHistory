using System.Text.RegularExpressions;

namespace OracleSessionHistory.Core.DbLinks;

/// <summary>SQL テキスト中の DB Link 参照を判定する</summary>
public static class DbLinkSqlMatcher
{
    /// <summary>"OWNER.LINK.DOMAIN" から SQL 中で使われる短い名前（LINK）を取り出す</summary>
    public static string ShortName(string fullName)
    {
        var name = fullName;
        // OWNER. を除去
        var dot = name.IndexOf('.');
        if (dot >= 0) name = name[(dot + 1)..];
        // ドメイン部（.WORLD 等）を除去
        dot = name.IndexOf('.');
        if (dot >= 0) name = name[..dot];
        return name.Trim('"').ToUpperInvariant();
    }

    /// <summary>"@LINK" / "@LINK.WORLD" / "@\"LINK\"" を検出する</summary>
    public static bool ReferencesLink(string? sql, string shortName) =>
        sql is not null && Regex.IsMatch(sql, $@"@\s*""?{Regex.Escape(shortName)}(""|\b)", RegexOptions.IgnoreCase);

    /// <summary>シノニム名が単語として含まれるか</summary>
    public static bool ReferencesWord(string? sql, string word) =>
        sql is not null && Regex.IsMatch(sql, $@"(?<![\w$#]){Regex.Escape(word)}(?![\w$#])", RegexOptions.IgnoreCase);
}
