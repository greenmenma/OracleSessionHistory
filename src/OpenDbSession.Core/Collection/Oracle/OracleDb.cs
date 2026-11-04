using System.Data;
using Oracle.ManagedDataAccess.Client;

namespace OpenDbSession.Core.Collection.Oracle;

/// <summary>Oracle アクセスの共通処理</summary>
internal static class OracleDb
{
    public static async Task<OracleConnection> OpenAsync(string connectionString, CancellationToken ct)
    {
        var conn = new OracleConnection(connectionString);
        try
        {
            await conn.OpenAsync(ct);
            // 監視側の接続であることが分かるようにする
            conn.ModuleName = "OpenDbSession";
            conn.ClientInfo = "OpenDbSession.Batch";
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    public static OracleCommand Command(OracleConnection conn, string sql, int timeoutSeconds = 30)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.BindByName = true;
        cmd.CommandTimeout = timeoutSeconds;
        cmd.InitialLONGFetchSize = -1;
        return cmd;
    }

    public static async Task<List<T>> QueryAsync<T>(OracleConnection conn, string sql, Func<IDataRecord, T> map, CancellationToken ct,
        int timeoutSeconds = 30, params OracleParameter[] parameters)
    {
        await using var cmd = Command(conn, sql, timeoutSeconds);
        cmd.Parameters.AddRange(parameters);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<T>();
        while (await r.ReadAsync(ct)) list.Add(map(r));
        return list;
    }

    public static string? Str(this IDataRecord r, string name)
    {
        var i = r.GetOrdinal(name);
        return r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i));
    }

    public static int Int(this IDataRecord r, string name)
    {
        var i = r.GetOrdinal(name);
        return r.IsDBNull(i) ? 0 : Convert.ToInt32(r.GetValue(i));
    }

    public static int? IntN(this IDataRecord r, string name)
    {
        var i = r.GetOrdinal(name);
        return r.IsDBNull(i) ? null : Convert.ToInt32(r.GetValue(i));
    }

    public static long? LongN(this IDataRecord r, string name)
    {
        var i = r.GetOrdinal(name);
        return r.IsDBNull(i) ? null : Convert.ToInt64(r.GetValue(i));
    }

    public static DateTime Date(this IDataRecord r, string name)
    {
        var i = r.GetOrdinal(name);
        return r.IsDBNull(i) ? DateTime.MinValue : Convert.ToDateTime(r.GetValue(i));
    }

    /// <summary>ORA-00942（表またはビューが存在しない＝権限不足）等で付加情報が取れない場合は警告にとどめる</summary>
    public static async Task<List<T>> TryQueryAsync<T>(OracleConnection conn, string sql, Func<IDataRecord, T> map,
        List<string> warnings, string label, CancellationToken ct, int timeoutSeconds = 30)
    {
        try
        {
            return await QueryAsync(conn, sql, map, ct, timeoutSeconds);
        }
        catch (OracleException ex)
        {
            warnings.Add($"{label}: ORA-{ex.Number:00000} {ex.Message}");
            return [];
        }
    }
}
