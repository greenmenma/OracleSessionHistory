using System.Data.Common;

namespace OracleSessionHistory.Server.Support;

/// <summary>接続文字列のパスワードを画面に返さないためのマスク処理</summary>
public static class ConnectionStringMasker
{
    public const string Mask = "********";
    private static readonly string[] PasswordKeys = ["password", "pwd"];

    public static string MaskPassword(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return "";
        if (!TryParse(connectionString, out var b)) return Mask;
        foreach (var key in PasswordKeys)
            if (b.ContainsKey(key)) b[key] = Mask;
        return b.ConnectionString;
    }

    /// <summary>画面から返ってきた接続文字列のパスワードがマスクのままなら、保存済みのパスワードに戻す</summary>
    public static string Merge(string incoming, string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored) || !incoming.Contains(Mask)) return incoming;
        if (!TryParse(incoming, out var nb) || !TryParse(stored, out var ob)) return incoming;
        foreach (var key in PasswordKeys)
        {
            if (nb.TryGetValue(key, out var v) && Equals(v, Mask))
            {
                if (ob.TryGetValue(key, out var old)) nb[key] = old;
                else nb.Remove(key);
            }
        }
        return nb.ConnectionString;
    }

    private static bool TryParse(string cs, out DbConnectionStringBuilder builder)
    {
        builder = new DbConnectionStringBuilder();
        try
        {
            builder.ConnectionString = cs;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
