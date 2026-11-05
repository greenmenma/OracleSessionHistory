using Oracle.ManagedDataAccess.Client;
using OracleSessionHistory.Core.Collection.Oracle;
using OracleSessionHistory.Core.Entities;

namespace OracleSessionHistory.Core.DbLinks;

public sealed class OracleDbLinkInspector : IDbLinkInspector
{
    public string Provider => SourceProviders.Oracle;

    private const int MaxSynonymPatterns = 100;

    private const string SessionColumns = """
        s.inst_id, s.sid, s.serial# as serial, s.logon_time, s.username, s.schemaname, s.osuser, s.machine,
        s.terminal, s.program, s.module, s.action, s.process, p.spid
        """;

    public async Task<IReadOnlyList<DbLinkInfo>> ListLinksAsync(string connectionString, CancellationToken ct)
    {
        await using var conn = await OracleDb.OpenAsync(connectionString, ct);
        foreach (var view in new[] { "dba_db_links", "all_db_links" })
        {
            try
            {
                return await OracleDb.QueryAsync(conn,
                    $"select owner, db_link, username, host, created from {view} order by owner, db_link",
                    r => new DbLinkInfo(r.Str("owner")!, r.Str("db_link")!, r.Str("username"), r.Str("host"), r.Date("created")), ct);
            }
            catch (OracleException ex) when (ex.Number == 942)
            {
                // 権限が無ければ all_db_links で再試行
            }
        }
        return [];
    }

    /// <summary>利用元の特定に必須のビュー（参照できなければエラー）</summary>
    private static readonly string[] RequiredViews = ["gv$session", "gv$process"];

    /// <summary>SQL 一致に使うビュー（参照できなければ警告にとどめ、プロセス一致は続ける）</summary>
    private const string SqlAreaView = "gv$sqlarea";

    public async Task<DbLinkUsageResult> FindUsagesAsync(LinkSource source, IReadOnlyCollection<string> processIds, CancellationToken ct)
    {
        await using var conn = await OracleDb.OpenAsync(source.ConnectionString, ct);
        var result = new List<ObservedLinkUsage>();
        var warnings = new List<string>();

        // 権限不足（ORA-00942）のとき、どのビューが足りないか分かるように先に確認する
        var missing = await MissingViewsAsync(conn, RequiredViews, ct);
        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"接続元 DB で {string.Join(", ", missing)} を参照できません（ORA-00942: 表またはビューが存在しません）。" +
                $"接続ユーザーに権限を付与してください: {GrantHint(missing, conn)}");

        // ---- 1. プロセス ID 一致（監視対象側 gv$session.process = 接続元 gv$process.spid） ----
        if (processIds.Count > 0)
        {
            var ids = processIds.Distinct().Take(500).ToList();
            var binds = ids.Select((_, i) => $":s{i}");
            var sql = $"""
                select {SessionColumns}, s.sql_id, null as sql_text
                  from gv$session s
                  join gv$process p on p.inst_id = s.inst_id and p.addr = s.paddr
                 where s.type = 'USER' and p.spid in ({string.Join(",", binds)})
                """;
            var prms = ids.Select((v, i) => new OracleParameter($"s{i}", v)).ToArray();
            result.AddRange(await OracleDb.QueryAsync(conn, sql, r => Map(r, DbLinkMatchTypes.ProcessOnlyLinkName, DbLinkMatchTypes.Process), ct, 30, prms));
        }

        if (source.SelectedLinks.Count == 0) return new DbLinkUsageResult(result, warnings);

        if ((await MissingViewsAsync(conn, [SqlAreaView], ct)).Count > 0)
        {
            warnings.Add($"SQL 一致: {SqlAreaView} を参照できないため、SQL テキストでの検索を行いません（ORA-00942）。{GrantHint([SqlAreaView], conn)}");
            return new DbLinkUsageResult(result, warnings);
        }

        // ---- 2. SQL テキスト一致（@DBLINK またはそれを参照するシノニム） ----
        var synonyms = await LoadSynonymsAsync(conn, ct);
        foreach (var link in source.SelectedLinks)
        {
            var shortName = DbLinkSqlMatcher.ShortName(link);
            var synNames = synonyms
                .Where(s => DbLinkSqlMatcher.ShortName("X." + s.DbLink) == shortName)
                .Select(s => s.Name.ToUpperInvariant())
                .Distinct()
                .Take(MaxSynonymPatterns)
                .ToList();

            var patterns = new List<string> { $"%@{shortName}%", $"%@\"{shortName}%" };
            patterns.AddRange(synNames.Select(n => $"%{n}%"));
            var where = string.Join(" or ", patterns.Select((_, i) => $"upper(a.sql_fulltext) like :p{i}"));

            // セッションが現在または直前に実行した SQL に絞って検索する（共有プール全体を走査しない）
            var sql = $"""
                select {SessionColumns}, a.sql_id, dbms_lob.substr(a.sql_fulltext, 4000, 1) as sql_text
                  from gv$session s
                  join gv$process p on p.inst_id = s.inst_id and p.addr = s.paddr
                  join gv$sqlarea a on a.inst_id = s.inst_id and a.sql_id in (s.sql_id, s.prev_sql_id)
                 where s.type = 'USER'
                   and not (s.inst_id = sys_context('USERENV', 'INSTANCE') and s.sid = sys_context('USERENV', 'SID'))
                   and ({where})
                """;
            var prms = patterns.Select((v, i) => new OracleParameter($"p{i}", v)).ToArray();
            var rows = await OracleDb.QueryAsync(conn, sql, r =>
            {
                var text = r.Str("sql_text");
                var matchType = DbLinkSqlMatcher.ReferencesLink(text, shortName) ? DbLinkMatchTypes.Sql
                    : synNames.Any(n => DbLinkSqlMatcher.ReferencesWord(text, n)) ? DbLinkMatchTypes.Synonym
                    : null;
                return matchType is null ? null : Map(r, link, matchType);
            }, ct, 60, prms);
            result.AddRange(rows.OfType<ObservedLinkUsage>());
        }

        return new DbLinkUsageResult(result, warnings);
    }

    /// <summary>参照できない（ORA-00942）ビューを返す。行は読まないため負荷は無い</summary>
    private static async Task<List<string>> MissingViewsAsync(OracleConnection conn, IEnumerable<string> views, CancellationToken ct)
    {
        var missing = new List<string>();
        foreach (var view in views)
        {
            try
            {
                await OracleDb.QueryAsync(conn, $"select 1 from {view} where 1 = 0", _ => 0, ct);
            }
            catch (OracleException ex) when (ex.Number == 942)
            {
                missing.Add(view);
            }
        }
        return missing;
    }

    /// <summary>付与が必要な GRANT 文（gv$xxx の実体は sys.gv_$xxx）</summary>
    internal static string GrantHint(IEnumerable<string> views, OracleConnection conn)
    {
        var user = new OracleConnectionStringBuilder(conn.ConnectionString).UserID;
        if (string.IsNullOrWhiteSpace(user)) user = "<接続ユーザー>";
        return string.Join(" ", views.Select(v => $"GRANT SELECT ON sys.{v.Replace("v$", "v_$")} TO {user};"));
    }

    private static async Task<List<(string Name, string DbLink)>> LoadSynonymsAsync(OracleConnection conn, CancellationToken ct)
    {
        foreach (var view in new[] { "dba_synonyms", "all_synonyms" })
        {
            try
            {
                return await OracleDb.QueryAsync(conn,
                    $"select synonym_name, db_link from {view} where db_link is not null",
                    r => (r.Str("synonym_name")!, r.Str("db_link")!), ct);
            }
            catch (OracleException ex) when (ex.Number == 942)
            {
            }
        }
        return [];
    }

    private static ObservedLinkUsage Map(System.Data.IDataRecord r, string link, string matchType) => new()
    {
        DbLinkName = link,
        MatchType = matchType,
        InstId = r.Int("inst_id"),
        Sid = r.Int("sid"),
        Serial = r.Int("serial"),
        LogonTime = r.Date("logon_time"),
        Username = r.Str("username"),
        SchemaName = r.Str("schemaname"),
        OsUser = r.Str("osuser"),
        Machine = r.Str("machine"),
        Terminal = r.Str("terminal"),
        Program = r.Str("program"),
        Module = r.Str("module"),
        Action = r.Str("action"),
        ServerProcess = r.Str("spid"),
        ClientProcess = r.Str("process"),
        SqlId = r.Str("sql_id"),
        SqlText = r.Str("sql_text"),
    };
}
