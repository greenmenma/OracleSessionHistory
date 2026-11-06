using OracleSessionHistory.Core.DbLinks;
using OracleSessionHistory.Core.Entities;

namespace OracleSessionHistory.Core.Collection.Demo;

/// <summary>
/// Oracle が無い環境で画面・収集処理を確認するための疑似データ。
/// 接続・切断をランダムに発生させ、定期的に取得失敗も発生させる。
/// </summary>
public sealed class DemoWorld
{
    /// <summary>疑似的な接続元プロファイル</summary>
    internal sealed record Profile(string Machine, string Program, string Username, string OsUser, string? Module,
        string Kind, int Weight, string? Driver = "ODP.NET Managed Driver");

    internal static readonly Profile[] Profiles =
    [
        new("PC-SALES01", "販売管理.exe", "SALES", "yamada", "販売管理", "client", 6),
        new("PC-SALES02", "販売管理.exe", "SALES", "suzuki", "販売管理", "client", 6),
        new("PC-SALES03", "販売管理.exe", "SALES", "tanaka", "販売管理", "client", 4),
        new("PC-ACC01", "会計システム.exe", "ACCOUNT", "sato", "会計", "client", 4),
        new("APSV01", "w3wp.exe", "WEBAPP", "IIS APPPOOL\\Koukai", null, "client", 8),
        new("APSV02", "w3wp.exe", "WEBAPP", "IIS APPPOOL\\Koukai", null, "client", 6),
        new("BATCHSV", "JP1Batch.exe", "BATCH", "jp1admin", "夜間集計", "client", 2),
        new("BATCHSV02", "sqlplus.exe", "BATCH", "jp1admin", "SQL*Plus", "client", 2, "SQL*PLUS"),
        new("OPE-PC01", "sqlplus.exe", "OPE", "ope01", "SQL*Plus", "client", 1, "SQL*PLUS"),
        new("DEV-PC07", "sqlplus.exe", "SYSTEM", "dev07", "SQL*Plus", "client", 1, "SQL*PLUS"),
        new("DEV-PC08", "SQL Developer", "SALES", "dev08", "SQL Developer", "client", 1, "jdbcthin"),
        // このシステム自身の接続（既定で一覧に出ないことを確認する）
        new("OSH-SV01", "OracleSessionHistory.Batch.exe", "MONITOR", "svc_osh", "OracleSessionHistory", "client", 1),
        new("KIKAN-DB01", "ORACLE.EXE", "LINKUSER", "SYSTEM", null, "dblink", 4, null),
        new("koukai-db", "oracle@koukai-db (J000)", "BATCH", "oracle", "DBMS_SCHEDULER", "job", 2, null),
        new("koukai-db", "oracle@koukai-db (P001)", "SALES", "oracle", null, "px", 1, null),
    ];

    /// <summary>DB Link 接続元側のセッション（接続元 spid → 利用システム）</summary>
    internal static readonly (string Machine, string Program, string Username, string OsUser, string Sql)[] LinkOrigins =
    [
        ("KIKAN-AP01", "w3wp.exe", "JUCHU", "IIS APPPOOL\\JuchuWeb", "select * from ITEM_MASTER@KOUKAI_LINK where ITEM_CD = :1"),
        ("KIKAN-AP02", "在庫照会.exe", "ZAIKO", "kikan02", "select QTY from STOCK@KOUKAI_LINK.WORLD where WH = :1"),
        ("KIKAN-BAT", "日次連携.exe", "RENKEI", "batch", "insert into SALES_SUMMARY@KOUKAI_LINK select * from W_SALES"),
    ];

    private sealed class TargetState
    {
        public int Polls;
        public int NextSid = 100;
        public readonly List<(ObservedSession Session, int OriginIndex)> Active = [];
    }

    private readonly Lock _lock = new();
    private readonly Dictionary<int, TargetState> _states = [];
    private readonly Random _rand = new();

    internal SessionSnapshot Capture(MonitorTarget target)
    {
        lock (_lock)
        {
            if (!_states.TryGetValue(target.Id, out var st)) _states[target.Id] = st = new TargetState();
            st.Polls++;

            // 約 20 回に 1 回は取得失敗を発生させる（切断扱いにならないことの確認用）
            if (st.Polls % 20 == 0)
                throw new InvalidOperationException("ORA-12170: TNS: 接続タイムアウトが発生しました（デモ用の疑似エラー）");

            var now = DateTime.Now;
            if (st.Active.Count == 0)
            {
                for (var i = 0; i < 25; i++) st.Active.Add(NewSession(st, now.AddMinutes(-_rand.Next(1, 300))));
            }

            // ランダムに切断・接続
            for (var i = st.Active.Count - 1; i >= 0; i--)
                if (_rand.NextDouble() < 0.04) st.Active.RemoveAt(i);
            var add = _rand.Next(0, 3);
            for (var i = 0; i < add; i++) st.Active.Add(NewSession(st, now));

            // 状態を更新
            for (var i = 0; i < st.Active.Count; i++)
            {
                var (s, o) = st.Active[i];
                var active = _rand.NextDouble() < 0.3;
                st.Active[i] = (s with
                {
                    Status = active ? "ACTIVE" : "INACTIVE",
                    Event = active ? "db file sequential read" : "SQL*Net message from client",
                }, o);
            }

            return new SessionSnapshot
            {
                CapturedAt = now,
                RespondedInstances = new HashSet<int> { 1 },
                Sessions = st.Active.Select(x => x.Session).ToList(),
            };
        }
    }

    private (ObservedSession, int) NewSession(TargetState st, DateTime logon)
    {
        var p = PickProfile();
        var sid = st.NextSid++;
        var origin = p.Kind == "dblink" ? _rand.Next(LinkOrigins.Length) : -1;
        var spid = 10000 + sid;
        var s = new ObservedSession
        {
            InstId = 1,
            Sid = sid,
            Serial = _rand.Next(1, 60000),
            LogonTime = new DateTime(logon.Year, logon.Month, logon.Day, logon.Hour, logon.Minute, logon.Second),
            AuditSid = sid * 10,
            Username = p.Username,
            SchemaName = p.Username,
            OsUser = p.OsUser,
            Machine = p.Machine,
            Terminal = p.Kind == "client" ? p.Machine : "UNKNOWN",
            Program = p.Program,
            Module = p.Kind == "job" ? "DBMS_SCHEDULER" : p.Module ?? p.Program,
            Action = p.Kind == "job" ? "NIGHTLY_AGGREGATE" : null,
            ServiceName = "KOUKAI",
            // DB Link の場合は接続元サーバーの "pid:tid"（Windows）形式
            ClientProcess = p.Kind == "dblink" ? $"{3000 + origin}:{20000 + origin}" : _rand.Next(1000, 9999).ToString(),
            ServerProcess = spid.ToString(),
            Port = _rand.Next(49152, 65535),
            ClientDriver = p.Driver,
            SessionType = "USER",
            Status = "INACTIVE",
            SqlId = "demo" + _rand.Next(100000, 999999),
            SqlText = p.Kind switch
            {
                "dblink" => "SELECT \"A1\".\"ITEM_CD\" FROM \"ITEM_MASTER\" \"A1\" WHERE \"A1\".\"ITEM_CD\"=:1",
                "job" => "BEGIN PKG_AGGREGATE.RUN_NIGHTLY; END;",
                _ => "SELECT * FROM ORDERS WHERE ORDER_DATE >= :1",
            },
            JobType = p.Kind == "job" ? "SCHEDULER/STORED_PROCEDURE" : null,
            JobOwner = p.Kind == "job" ? "BATCH" : null,
            JobName = p.Kind == "job" ? "NIGHTLY_AGGREGATE" : null,
            JobAction = p.Kind == "job" ? "PKG_AGGREGATE.RUN_NIGHTLY" : null,
            ParentInfo = p.Kind == "px" ? "PXコーディネータ inst=1 sid=101 user=SALES machine=PC-SALES01 program=販売管理.exe" : null,
        };
        return (s, origin);
    }

    private Profile PickProfile()
    {
        var total = Profiles.Sum(p => p.Weight);
        var n = _rand.Next(total);
        foreach (var p in Profiles)
        {
            if (n < p.Weight) return p;
            n -= p.Weight;
        }
        return Profiles[0];
    }

    internal static IReadOnlyList<DbLinkInfo> Links =>
    [
        new("PUBLIC", "KOUKAI_LINK", "LINKUSER", "KOUKAI", new DateTime(2019, 4, 1)),
        new("SALES", "KOUKAI_LINK.WORLD", "LINKUSER", "KOUKAI", new DateTime(2020, 10, 1)),
        new("BATCH", "OLD_REPORT_LINK", "REPORT", "REPORTDB", new DateTime(2015, 6, 1)),
    ];

    internal IReadOnlyList<ObservedLinkUsage> FindUsages(LinkSource source, IReadOnlyCollection<string> processIds)
    {
        var list = new List<ObservedLinkUsage>();
        var today = DateTime.Today;
        for (var i = 0; i < LinkOrigins.Length; i++)
        {
            var o = LinkOrigins[i];
            var spid = (20000 + i).ToString();
            var baseUsage = new ObservedLinkUsage
            {
                DbLinkName = DbLinkMatchTypes.ProcessOnlyLinkName,
                MatchType = DbLinkMatchTypes.Process,
                InstId = 1,
                Sid = 500 + i,
                Serial = 1000 + i,
                LogonTime = today.AddHours(8).AddMinutes(i * 7),
                Username = o.Username,
                SchemaName = o.Username,
                OsUser = o.OsUser,
                Machine = o.Machine,
                Terminal = o.Machine,
                Program = o.Program,
                Module = o.Program,
                ServerProcess = spid,
                ClientProcess = $"{6000 + i}:{100 + i}",
                SqlId = "dlnk" + i,
                SqlText = o.Sql,
            };
            if (processIds.Contains(spid)) list.Add(baseUsage);
            foreach (var link in source.SelectedLinks)
            {
                if (DbLinkSqlMatcher.ReferencesLink(o.Sql, DbLinkSqlMatcher.ShortName(link)))
                    list.Add(baseUsage with { DbLinkName = link, MatchType = DbLinkMatchTypes.Sql });
            }
        }
        return list;
    }
}

public sealed class DemoSessionSource(DemoWorld world) : ISessionSource
{
    public string Provider => SourceProviders.Demo;

    public Task<SessionSnapshot> CaptureAsync(MonitorTarget target, CancellationToken ct) => Task.FromResult(world.Capture(target));

    public Task<string> TestConnectionAsync(string connectionString, CancellationToken ct) => Task.FromResult("接続成功（デモ）");
}

public sealed class DemoDbLinkInspector(DemoWorld world) : IDbLinkInspector
{
    public string Provider => SourceProviders.Demo;

    public Task<IReadOnlyList<DbLinkInfo>> ListLinksAsync(string connectionString, CancellationToken ct) => Task.FromResult(DemoWorld.Links);

    public Task<DbLinkUsageResult> FindUsagesAsync(LinkSource source, IReadOnlyCollection<string> processIds, CancellationToken ct) =>
        Task.FromResult(new DbLinkUsageResult(world.FindUsages(source, processIds), []));
}

/// <summary>デモ用の過去履歴（接続数表の確認用に約 2 ヶ月分）</summary>
public static class DemoHistory
{
    public static List<TrackedSession> Generate(int targetId, DateTime now, int seed = 42)
    {
        var rand = new Random(seed);
        var list = new List<TrackedSession>();
        var sid = 10;
        for (var day = 65; day >= 0; day--)
        {
            var date = now.Date.AddDays(-day);
            foreach (var p in DemoWorld.Profiles)
            {
                // 土日は少なめ
                var weekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
                var count = rand.Next(0, p.Weight * (weekend ? 1 : 3) + 1);
                for (var i = 0; i < count; i++)
                {
                    var logon = date.AddHours(7 + rand.Next(0, 12)).AddMinutes(rand.Next(60)).AddSeconds(rand.Next(60));
                    var end = logon.AddMinutes(rand.Next(1, 240));
                    if (end >= now.AddMinutes(-10)) continue; // 現在接続中のものは収集に任せる
                    var kind = Tracking.ProgramClassifier.Classify(p.Program, "USER", p.Module);
                    // 接続アプリの表示を確認できるよう、クライアント（w3wp.exe・sqlplus.exe のみ）は疑似プロセス情報で取得済みにしておく
                    var pid = rand.Next(1000, 9999);
                    var proc = kind == ProcessKinds.Client && Processes.ClientProcessRules.IsInspectedProgram(p.Program) ? Processes.DemoProcessInspector.Profile(p.Machine, pid, logon.AddHours(-1)) : null;
                    var (appName, appDetail) = Processes.AppIdentifier.Identify(proc, p.Program, p.Module ?? p.Program, p.OsUser);
                    list.Add(new TrackedSession
                    {
                        AppName = appName,
                        AppDetail = appDetail,
                        ProcInfoState = proc is not null ? ProcInfoStates.Resolved : ProcInfoStates.NotApplicable,
                        ProcInfoAt = proc is not null ? logon.AddSeconds(10) : null,
                        ProcName = proc?.Name,
                        ProcPath = proc?.ExecutablePath,
                        ProcCommandLine = proc?.CommandLine,
                        ProcOwner = proc?.Owner,
                        ProcStartedAt = proc?.StartedAt,
                        ProcParentName = proc?.ParentName,
                        ProcAncestry = proc?.Ancestors is { Count: > 0 } ancestors
                            ? string.Join("\n", ancestors.Select(x => string.IsNullOrWhiteSpace(x.CommandLine) ? x.Name : $"{x.Name}: {x.CommandLine}"))
                            : null,
                        IisAppPool = proc?.IisAppPool,
                        IisSite = proc?.IisSite,
                        ClientProcess = pid.ToString(),
                        TargetId = targetId,
                        InstId = 1,
                        Sid = sid++ % 3000 + 10,
                        Serial = rand.Next(1, 65000),
                        LogonTime = logon,
                        Username = p.Username,
                        SchemaName = p.Username,
                        OsUser = p.OsUser,
                        Machine = p.Machine,
                        Terminal = p.Machine,
                        Program = p.Program,
                        Module = p.Kind == "job" ? "DBMS_SCHEDULER" : p.Module ?? p.Program,
                        ServiceName = "KOUKAI",
                        ClientDriver = p.Driver,
                        SessionType = "USER",
                        Status = "INACTIVE",
                        ProcessKind = kind,
                        JobType = p.Kind == "job" ? "SCHEDULER/STORED_PROCEDURE" : null,
                        JobOwner = p.Kind == "job" ? "BATCH" : null,
                        JobName = p.Kind == "job" ? "NIGHTLY_AGGREGATE" : null,
                        JobAction = p.Kind == "job" ? "PKG_AGGREGATE.RUN_NIGHTLY" : null,
                        FirstSeenAt = logon.AddSeconds(rand.Next(1, 5)),
                        LastSeenAt = end,
                        IsConnected = false,
                        DisconnectedAt = end,
                        DisconnectDetectedAt = end.AddSeconds(5),
                    });
                }
            }
        }
        return list;
    }
}
