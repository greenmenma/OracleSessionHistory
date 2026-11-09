using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OracleSessionHistory.Core.Data;
using OracleSessionHistory.Core.Entities;
using OracleSessionHistory.Server.Api;

namespace OracleSessionHistory.Tests;

/// <summary>Server API の検索・集計（SQLite インメモリ）</summary>
public sealed class ApiQueryTests : IDisposable
{
    private static readonly DateTime Today = new(2026, 11, 5);
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly AppDbContext _db;
    private readonly int _targetId;

    public ApiQueryTests()
    {
        _conn.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_conn, o => o.MigrationsAssembly(AppDatabase.SqliteMigrationsAssembly)).Options);
        _db.Database.Migrate();
        var t = new MonitorTarget { Name = "T", ConnectionString = "x" };
        _db.Targets.Add(t);
        _db.SaveChanges();
        _targetId = t.Id;
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    private int _sid;

    private void Add(string kind, string machine = "WEB01", string app = "IIS: Foo", string? detail = null, DateTime? logon = null,
        bool connected = false, string? owner = null, (string Machine, string App)? origin = null)
    {
        var l = logon ?? Today.AddHours(9);
        _db.Sessions.Add(new TrackedSession
        {
            TargetId = _targetId, InstId = 1, Sid = ++_sid, Serial = 1, LogonTime = l, Machine = machine, Program = "w3wp.exe",
            AppName = app, AppDetail = detail, ProcessKind = kind, ProcOwner = owner, IsConnected = connected,
            LinkOriginMachine = origin?.Machine, LinkOriginApp = origin?.App,
            FirstSeenAt = l, LastSeenAt = l.AddMinutes(1),
        });
        _db.SaveChanges();
    }

    private static SessionSearch Search(string? kind = null, bool includeInternal = false, string? app = null, string? q = null) =>
        new(null, null, q, null, null, null, null, kind, null, null, App: app, IncludeInternal: includeInternal);

    [Fact]
    public void 既定ではOracle内部プロセスを表示しない()
    {
        Add(ProcessKinds.Client);
        Add(ProcessKinds.DbLinkSuspect, app: "ORACLE.EXE");
        Add(ProcessKinds.JobSlave, app: "ORACLE.EXE (J000)");
        Add(ProcessKinds.Background, app: "ORACLE.EXE (PMON)");
        Add(ProcessKinds.Self, app: "w3wp.exe");

        var kinds = SessionEndpoints.Filter(_db.Sessions, Search()).Select(s => s.ProcessKind).ToList();

        Assert.Equal([ProcessKinds.Client, ProcessKinds.DbLinkSuspect], kinds.Order());
    }

    [Fact]
    public async Task DBLinkの可能性がある行には最後に観測した接続元のアプリを出す()
    {
        Add(ProcessKinds.DbLinkSuspect, app: "ORACLE.EXE");
        var sessionId = _db.Sessions.Single().Id;
        var l = new LinkSource { TargetId = _targetId, Name = "L", ConnectionString = "y" };
        _db.LinkSources.Add(l);
        _db.SaveChanges();
        foreach (var (sid, app, machine, seen) in new[] { (1, "古い.exe", "OLD01", Today.AddHours(8)), (2, "IIS: Juchu", "KIKAN-AP01", Today.AddHours(9)) })
        {
            _db.DbLinkUsages.Add(new DbLinkUsage
            {
                LinkSourceId = l.Id, TargetId = _targetId, DbLinkName = "(PROCESS)", InstId = 1, Sid = sid, Serial = 1, LogonTime = Today,
                AppName = app, Machine = machine, MatchedSessionId = sessionId, FirstSeenAt = seen, LastSeenAt = seen,
            });
        }
        _db.SaveChanges();

        await LinkOriginSync.RefreshAsync(_db, [sessionId], default);
        _db.ChangeTracker.Clear();
        var item = SessionEndpoints.Project(_db.Sessions).Single();

        Assert.Equal(("IIS: Juchu", "KIKAN-AP01"), (item.LinkOriginApp, item.LinkOriginMachine));
        Assert.Equal(2, item.DbLinkOriginCount);
    }

    [Fact]
    public void 内部プロセスも表示を指定すると全件()
    {
        Add(ProcessKinds.Client);
        Add(ProcessKinds.JobSlave);
        Assert.Equal(2, SessionEndpoints.Filter(_db.Sessions, Search(includeInternal: true)).Count());
    }

    [Fact]
    public void 種別を指定した場合は内部プロセスでも表示する()
    {
        Add(ProcessKinds.Client);
        Add(ProcessKinds.JobSlave);
        Assert.Equal(ProcessKinds.JobSlave, SessionEndpoints.Filter(_db.Sessions, Search(kind: ProcessKinds.JobSlave)).Single().ProcessKind);
    }

    [Fact]
    public void アプリとキーワードでアプリ名と補足を検索する()
    {
        Add(ProcessKinds.Client, app: "IIS: Foo", detail: "Default Web Site/foo");
        Add(ProcessKinds.Client, app: "IIS: Bar", detail: "Default Web Site/bar");

        Assert.Equal("IIS: Foo", SessionEndpoints.Filter(_db.Sessions, Search(app: "foo")).Single().AppName);
        Assert.Equal("IIS: Bar", SessionEndpoints.Filter(_db.Sessions, Search(q: "site/bar")).Single().AppName);
    }

    [Fact]
    public void 完全一致では端末とアプリが同じものだけ()
    {
        Add(ProcessKinds.Client, machine: "WEB01", app: "IIS: Pool");
        Add(ProcessKinds.Client, machine: "WEB01", app: "IIS: Pool2");
        Add(ProcessKinds.Client, machine: "WEB011", app: "IIS: Pool");

        var rows = SessionEndpoints.Filter(_db.Sessions,
            new SessionSearch(null, null, null, "WEB01", null, null, null, null, null, null, App: "IIS: Pool", Exact: true)).ToList();

        Assert.Equal(("WEB01", "IIS: Pool"), (rows.Single().Machine, rows.Single().AppName));
    }

    [Fact]
    public void 完全一致で端末が不明の場合は端末の無い行()
    {
        Add(ProcessKinds.Client, machine: "", app: "IIS: Pool");
        Add(ProcessKinds.Client, machine: "WEB01", app: "IIS: Pool");

        var rows = SessionEndpoints.Filter(_db.Sessions,
            new SessionSearch(null, null, null, SessionEndpoints.UnknownMachine, null, null, null, null, null, null, App: "IIS: Pool", Exact: true)).ToList();

        Assert.Equal("", rows.Single().Machine);
    }

    /// <summary>木の中から、ラベルの並びで行を探す</summary>
    private static ConnectionCountNode Find(IEnumerable<ConnectionCountNode> nodes, params string[] path)
    {
        var node = nodes.Single(n => n.Label == path[0]);
        foreach (var label in path.Skip(1)) node = node.Children.Single(n => n.Label == label);
        return node;
    }

    private void AddSql(string machine, string program, string app, DateTime logon, string? origin = null, string? originApp = null,
        string? source = null, string? user = null, string? originUser = null)
    {
        _db.Sessions.Add(new TrackedSession
        {
            TargetId = _targetId, InstId = 1, Sid = ++_sid, Serial = 1, LogonTime = logon, Machine = machine, Program = program,
            AppName = app, ProcessKind = originApp is null && program != "ORACLE.EXE" ? ProcessKinds.Client : ProcessKinds.DbLinkSuspect,
            LinkOriginMachine = origin, LinkOriginApp = originApp, LinkOriginSource = source, OsUser = user, LinkOriginUser = originUser,
            FirstSeenAt = logon, LastSeenAt = logon.AddMinutes(1),
        });
        _db.SaveChanges();
    }

    [Fact]
    public async Task 接続アプリ表は端末と実行ファイルとプールや実行元の木にする()
    {
        var t = Today.AddHours(9);
        AddSql("APP01", "販売管理.exe", "販売管理.exe", t);
        AddSql("APP01", "w3wp.exe", "IIS: Pool1", t);
        AddSql("APP01", "w3wp.exe", "IIS: Pool1", t.AddHours(1));
        AddSql("APP01", "w3wp.exe", "IIS: Pool0", t); // 件数の少ないものは後ろ（名前順ではない）
        AddSql("APP01", "w3wp.exe", "w3wp.exe", t); // プール未確定
        AddSql("APP01", "sqlplus.exe", "sqlplus.exe: 日次.bat", t);
        AddSql("APP01", "sqlplus.exe", "sqlplus.exe: cmd.exe", t);
        AddSql("app01", "sqlplus.exe", "sqlplus.exe", t); // 実行元が 1 つも分からない端末と同じ扱いにはならず「(実行元不明)」

        var (nodes, _) = await ReportEndpoints.QueryConnectionTreeAsync(_db, null, null, Today, default);

        var app01 = Find(nodes, "APP01");
        Assert.Equal(8, app01.Today);
        Assert.Equal(["w3wp.exe", "sqlplus.exe", "販売管理.exe"], app01.Children.Select(c => c.Label));
        var w3wp = Find(nodes, "APP01", "w3wp.exe");
        Assert.Equal((4, ConnectionCountKinds.Exe), (w3wp.Today, w3wp.Kind));
        Assert.Equal(["Pool1", "Pool0", "(プール不明)"], w3wp.Children.Select(c => c.Label));
        Assert.Equal(2, Find(nodes, "APP01", "w3wp.exe", "Pool1").Today);
        Assert.Equal(["cmd.exe", "日次.bat", "(実行元不明)"], Find(nodes, "APP01", "sqlplus.exe").Children.Select(c => c.Label));
        Assert.Empty(Find(nodes, "APP01", "販売管理.exe").Children);
        Assert.Equal("IIS: Pool1", Find(nodes, "APP01", "w3wp.exe", "Pool1").Open!["app"]);
    }

    [Fact]
    public async Task 実行ユーザはすべての段に出しDBLink経由は接続元の実行ユーザにする()
    {
        var t = Today.AddHours(9);
        AddSql("APP01", "sqlplus.exe", "sqlplus.exe: 日次.bat", t, user: @"CORP\batch");
        AddSql("APP01", "sqlplus.exe", "sqlplus.exe: 日次.bat", t, user: @"corp\BATCH"); // 大文字小文字の違いはまとめる
        AddSql("APP01", "sqlplus.exe", "sqlplus.exe: cmd.exe", t, user: @"CORP\ope01");
        AddSql("KIKAN-DB01", "ORACLE.EXE", "ORACLE.EXE", t, "KIKAN-AP01", "在庫照会.exe", "基幹DB", user: "SYSTEM", originUser: @"CORP\yamada");

        var (nodes, _) = await ReportEndpoints.QueryConnectionTreeAsync(_db, null, null, Today, default);

        Assert.Equal([@"CORP\batch", @"CORP\ope01"], Find(nodes, "APP01").Owners); // 多い順
        Assert.Equal([@"CORP\batch", @"CORP\ope01"], Find(nodes, "APP01", "sqlplus.exe").Owners);
        Assert.Equal([@"CORP\batch"], Find(nodes, "APP01", "sqlplus.exe", "日次.bat").Owners);
        Assert.Equal([@"CORP\yamada"], Find(nodes, "KIKAN-DB01", "ORACLE.EXE", "基幹DB", "KIKAN-AP01").Owners);
        Assert.Equal([@"CORP\yamada"], Find(nodes, "KIKAN-DB01", "ORACLE.EXE", "基幹DB", "KIKAN-AP01", "在庫照会.exe").Owners);
    }

    [Fact]
    public async Task DBLink経由の接続はORACLEの下にDBLink接続元と接続元のアプリの木にする()
    {
        var t = Today.AddHours(9);
        AddSql("KIKAN-DB01", "ORACLE.EXE", "ORACLE.EXE", t, "KIKAN-AP01", "IIS: Pool3", "基幹DB");
        AddSql("KIKAN-DB01", "ORACLE.EXE", "ORACLE.EXE", t, "KIKAN-AP02", "IIS: Pool3", "基幹DB");
        AddSql("KIKAN-DB01", "ORACLE.EXE", "ORACLE.EXE", t, "KIKAN-AP01", "sqlplus.exe: 月次.bat", "基幹DB");
        AddSql("KIKAN-DB01", "ORACLE.EXE", "ORACLE.EXE", t, "KIKAN-AP03", "在庫照会.exe", "基幹DB");
        AddSql("KIKAN-DB01", "ORACLE.EXE", "ORACLE.EXE", t); // 接続元を特定できていない

        var (nodes, _) = await ReportEndpoints.QueryConnectionTreeAsync(_db, null, null, Today, default);

        var oracle = Find(nodes, "KIKAN-DB01", "ORACLE.EXE");
        Assert.Equal(5, oracle.Today);
        Assert.Equal(["基幹DB", "(接続元不明)"], oracle.Children.Select(c => c.Label));
        var link = Find(nodes, "KIKAN-DB01", "ORACLE.EXE", "基幹DB");
        Assert.Equal((ConnectionCountKinds.DbLink, 4), (link.Kind, link.Today));
        // DB Link の下は接続元の端末 → 実行ファイル → プール・実行元
        Assert.Equal(["KIKAN-AP01", "KIKAN-AP02", "KIKAN-AP03"], link.Children.Select(c => c.Label));
        var ap01 = Find(nodes, "KIKAN-DB01", "ORACLE.EXE", "基幹DB", "KIKAN-AP01");
        Assert.Equal((ConnectionCountKinds.Machine, 2), (ap01.Kind, ap01.Today));
        Assert.Equal(["sqlplus.exe", "w3wp.exe"], ap01.Children.Select(c => c.Label).Order());
        Assert.Equal("月次.bat", Find(nodes, "KIKAN-DB01", "ORACLE.EXE", "基幹DB", "KIKAN-AP01", "sqlplus.exe").Children.Single().Label);
        var pool = Find(nodes, "KIKAN-DB01", "ORACLE.EXE", "基幹DB", "KIKAN-AP02", "w3wp.exe", "Pool3");
        Assert.Equal(1, pool.Today);
        Assert.Empty(Find(nodes, "KIKAN-DB01", "ORACLE.EXE", "基幹DB", "KIKAN-AP03", "在庫照会.exe").Children);
        // 接続元の端末を一番上の段にはしない
        Assert.DoesNotContain(nodes, n => n.Label.StartsWith("KIKAN-AP"));
        Assert.Equal(("KIKAN-DB01", "KIKAN-DB01", "基幹DB", "KIKAN-AP02", "IIS: Pool3"),
            (pool.Open!["host"], pool.Open["via"], pool.Open["linkSource"], pool.Open["origin"], pool.Open["app"]));
    }

    [Fact]
    public void 完全一致の一覧はDBLink経由の接続を接続元の端末とアプリで探す()
    {
        Add(ProcessKinds.Client, "KIKAN-AP02", "在庫照会.exe");
        Add(ProcessKinds.DbLinkSuspect, "KIKAN-DB01", "ORACLE.EXE", origin: ("KIKAN-AP02", "在庫照会.exe"));
        Add(ProcessKinds.DbLinkSuspect, "KIKAN-DB01", "ORACLE.EXE");

        var origin = SessionEndpoints.Filter(_db.Sessions,
            new SessionSearch(null, null, null, "KIKAN-AP02", null, null, null, null, null, null, App: "在庫照会.exe", Exact: true)).ToList();
        var unresolved = SessionEndpoints.Filter(_db.Sessions,
            new SessionSearch(null, null, null, "KIKAN-DB01", null, null, null, null, null, null, App: "ORACLE.EXE", Exact: true)).ToList();

        Assert.Equal(2, origin.Count);
        Assert.Null(unresolved.Single().LinkOriginApp);
    }

    [Fact]
    public async Task 接続アプリ表は端末とアプリ単位で集計し内部プロセスを除く()
    {
        Add(ProcessKinds.Client, "WEB01", "IIS: Foo", "Default Web Site/foo", Today.AddHours(9), owner: @"IIS APPPOOL\Foo");
        Add(ProcessKinds.Client, "WEB01", "IIS: Foo", "Default Web Site/foo", Today.AddDays(-1).AddHours(9), connected: true);
        Add(ProcessKinds.Client, "WEB01", "IIS: Bar", null, Today.AddHours(10));
        Add(ProcessKinds.Client, "WEB02", "IIS: Foo", null, Today.AddHours(10));
        Add(ProcessKinds.JobSlave, "DB01", "ORACLE.EXE (J000)", null, Today.AddHours(10));

        var (nodes, _) = await ReportEndpoints.QueryConnectionTreeAsync(_db, null, null, Today, default);

        Assert.Equal(["WEB01", "WEB02"], nodes.Select(n => n.Label));
        var web01 = Find(nodes, "WEB01");
        Assert.Equal((2, 3, 1), (web01.Today, web01.Month1, web01.Connected)); // 端末の行は下の段の合計
        Assert.Empty(web01.AuditPeriods);
        var foo = Find(nodes, "WEB01", "w3wp.exe", "Foo");
        Assert.Equal((1, 1, 2, 1), (foo.Today, foo.D1, foo.Month1, foo.Connected));
        Assert.Equal("Default Web Site/foo", foo.Detail);
        Assert.Equal([@"IIS APPPOOL\Foo"], foo.Owners);
    }

    private void SetAudit(DateTime from, DateTime to, params (DateTime Day, string Machine, int Count)[] rows)
    {
        var t = _db.Targets.Single(x => x.Id == _targetId);
        (t.AuditFrom, t.AuditTo, t.AuditFetchedAt) = (from, to, Today);
        foreach (var r in rows) _db.AuditLogonCounts.Add(new AuditLogonCount { TargetId = _targetId, Day = r.Day, Machine = r.Machine, Count = r.Count });
        _db.SaveChanges();
    }

    [Fact]
    public async Task 履歴が無い期間は端末の行を監査証跡の件数で補う()
    {
        // 履歴は 3 日前の昼から（3 日前は途中からのため監査の件数を使う。2 日前以降は履歴）
        Add(ProcessKinds.Client, "WEB01", "IIS: Foo", logon: Today.AddDays(-3).AddHours(13));
        Add(ProcessKinds.Client, "WEB01", "IIS: Foo", logon: Today.AddDays(-2).AddHours(9));
        Add(ProcessKinds.Client, "WEB01", "IIS: Foo", logon: Today.AddHours(9));
        SetAudit(Today.AddMonths(-2), Today.AddDays(-2),
            (Today.AddDays(-3), "web01", 10), (Today.AddDays(-5), "WEB01", 4), (Today.AddDays(-40), "WEB01", 7),
            (Today.AddDays(-4), "PC-OLD", 2), (Today.AddMonths(-3), "WEB01", 99)); // 期間外は数えない

        var (nodes, audit) = await ReportEndpoints.QueryConnectionTreeAsync(_db, null, null, Today, default);

        var web01 = Find(nodes, "WEB01");
        Assert.Equal((1, 1, 10, 0, 4), (web01.Today, web01.D2, web01.D3, web01.D4, web01.D5));
        Assert.Equal((1 + 1 + 10 + 4, 7), (web01.Month1, web01.Month2));
        Assert.Equal(["d3", "d4", "d5", "d6", "month1", "month2"], web01.AuditPeriods);
        // 下の段（アプリ）は履歴の件数のみ
        Assert.Equal(3, web01.Children.Single().Month1);
        // 履歴に無い端末も監査の件数だけで端末の行に出す
        var old = Find(nodes, "PC-OLD");
        Assert.Equal((2, 0), (old.D4, old.Children.Count));
        Assert.Equal(Today.AddDays(-3), audit.Single().LastDay);
    }

    [Fact]
    public async Task 絞り込み時は端末名が一致する場合だけ監査証跡で補う()
    {
        Add(ProcessKinds.Client, "WEB01", "IIS: Foo", logon: Today.AddHours(9));
        SetAudit(Today.AddMonths(-2), Today.AddDays(-2), (Today.AddDays(-3), "WEB01", 10));

        var (byApp, _) = await ReportEndpoints.QueryConnectionTreeAsync(_db, null, "Foo", Today, default);
        var (byMachine, _) = await ReportEndpoints.QueryConnectionTreeAsync(_db, null, "web0", Today, default);

        Assert.Equal(0, byApp.Single().D3);
        Assert.Equal(10, byMachine.Single().D3);
    }

    [Fact]
    public void 端末とDBLink接続元の条件で接続アプリ表の行と同じセッションを探す()
    {
        var t = Today.AddHours(9);
        AddSql("KIKAN-DB01", "ORACLE.EXE", "ORACLE.EXE", t, "KIKAN-AP01", "IIS: Pool3", "基幹DB");
        AddSql("KIKAN-DB01", "ORACLE.EXE", "ORACLE.EXE", t, "KIKAN-AP01", "IIS: Pool3", "情報系DB");
        AddSql("KIKAN-AP01", "w3wp.exe", "IIS: Pool3", t);

        List<TrackedSession> Find(string? host, string? via, string? source, string? app) => SessionEndpoints.Filter(_db.Sessions,
            new SessionSearch(null, null, null, null, null, null, null, null, null, null, App: app, Exact: app != null, Via: via,
                Host: host, LinkSource: source)).ToList();

        Assert.Equal(2, Find("KIKAN-DB01", null, null, null).Count); // DB Link 経由も DB サーバーの端末で探す
        Assert.Equal("基幹DB", Find("KIKAN-DB01", "KIKAN-DB01", "基幹DB", "IIS: Pool3").Single().LinkOriginSource);
        Assert.Equal("KIKAN-AP01", Find("KIKAN-AP01", SessionEndpoints.DirectVia, null, "IIS: Pool3").Single().Machine);
        var byOrigin = SessionEndpoints.Filter(_db.Sessions, new SessionSearch(null, null, null, null, null, null, null, null, null, null,
            Host: "KIKAN-DB01", Origin: "KIKAN-AP01")).ToList();
        Assert.Equal(2, byOrigin.Count);
    }

    [Fact]
    public void 経由の条件で直接の接続とDBLink経由の接続を分けて探す()
    {
        Add(ProcessKinds.Client, "KIKAN-AP02", "在庫照会.exe");
        Add(ProcessKinds.DbLinkSuspect, "KIKAN-DB01", "ORACLE.EXE", origin: ("KIKAN-AP02", "在庫照会.exe"));
        Add(ProcessKinds.DbLinkSuspect, "KIKAN-DB02", "ORACLE.EXE", origin: ("KIKAN-AP02", "在庫照会.exe"));

        List<TrackedSession> Find(string? machine, string? app, string via) => SessionEndpoints.Filter(_db.Sessions,
            new SessionSearch(null, null, null, machine, null, null, null, null, null, null, App: app, Exact: true, Via: via)).ToList();

        Assert.Null(Find("KIKAN-AP02", "在庫照会.exe", SessionEndpoints.DirectVia).Single().LinkOriginApp);
        Assert.Equal("KIKAN-DB01", Find("KIKAN-AP02", "在庫照会.exe", "KIKAN-DB01").Single().Machine);
        Assert.Equal("KIKAN-DB02", Find(null, null, "KIKAN-DB02").Single().Machine);
    }

    [Fact]
    public void DB_Link利用元はセッションと共通の条件で探す()
    {
        var l = new LinkSource { TargetId = _targetId, Name = "基幹DB", ConnectionString = "y" };
        _db.LinkSources.Add(l);
        _db.SaveChanges();
        void AddUsage(int sid, string machine, string app, string? owner = null) => _db.DbLinkUsages.Add(new DbLinkUsage
        {
            LinkSourceId = l.Id, TargetId = _targetId, DbLinkName = "TO_DB01", InstId = 1, Sid = sid, Serial = 1, LogonTime = Today,
            Machine = machine, AppName = app, ProcOwner = owner, FirstSeenAt = Today, LastSeenAt = Today.AddMinutes(1),
        });
        AddUsage(1, "AP01", "IIS: Pool", "CORP\\yamada");
        AddUsage(2, "AP011", "在庫照会.exe");
        _db.SaveChanges();

        List<DbLinkUsage> Find(SessionSearch s) => SessionEndpoints.FilterUsages(_db.DbLinkUsages, s).ToList();
        Assert.Equal(2, Find(new SessionSearch(null, null, null, "AP01", null, null, null, null, null, null)).Count);
        Assert.Equal(1, Find(new SessionSearch(null, null, null, "AP01", null, null, null, null, null, null, Exact: true)).Single().Sid);
        Assert.Equal(2, Find(new SessionSearch(null, null, null, null, null, null, null, null, null, null, App: "在庫")).Single().Sid);
        Assert.Equal(1, Find(new SessionSearch(null, null, "yamada", null, null, null, null, null, null, null)).Single().Sid);
        Assert.Equal(2, Find(new SessionSearch(null, null, "基幹", null, null, null, null, null, null, null)).Count); // 接続元 DB の名前
        Assert.Empty(Find(new SessionSearch(null, null, null, null, null, null, null, null, Today.AddDays(1), null)));
    }

    [Fact]
    public async Task すべてを表示する場合はセッションとDB_Link利用元をログオン順に並べてページを切り出す()
    {
        var l = new LinkSource { TargetId = _targetId, Name = "基幹DB", ConnectionString = "y" };
        _db.LinkSources.Add(l);
        _db.SaveChanges();
        // セッション 9:00・11:00、DB Link 利用元 10:00・12:00
        Add(ProcessKinds.Client, logon: Today.AddHours(9));
        Add(ProcessKinds.Client, logon: Today.AddHours(11));
        foreach (var (sid, h) in new[] { (1, 10), (2, 12) })
            _db.DbLinkUsages.Add(new DbLinkUsage
            {
                LinkSourceId = l.Id, TargetId = _targetId, DbLinkName = "TO_DB01", InstId = 1, Sid = sid, Serial = 1,
                LogonTime = Today.AddHours(h), Machine = "AP01", AppName = "x", FirstSeenAt = Today.AddHours(h), LastSeenAt = Today.AddHours(h),
            });
        _db.SaveChanges();

        async Task<List<(string, int)>> Page(int page, string? source) =>
            (await SessionEndpoints.QueryRowsAsync(_db, new SessionSearch(null, null, null, null, null, null, null, null, null, null,
                Page: page, PageSize: 3, Source: source))).Items.Select(r => (r.Type, r.LogonTime.Hour)).ToList();

        Assert.Equal([("dblink", 12), ("session", 11), ("dblink", 10)], await Page(1, SessionEndpoints.AllSource));
        Assert.Equal([("session", 9)], await Page(2, SessionEndpoints.AllSource));
        Assert.Equal(4, (await SessionEndpoints.QueryRowsAsync(_db, new SessionSearch(null, null, null, null, null, null, null, null, null, null,
            Source: SessionEndpoints.AllSource))).Total);
        Assert.Equal([("dblink", 12), ("dblink", 10)], await Page(1, SessionEndpoints.DbLinkSource));
        Assert.Equal([("session", 11), ("session", 9)], await Page(1, null));
        var usage = (await SessionEndpoints.QueryRowsAsync(_db, new SessionSearch(null, null, null, null, null, null, null, null, null, null,
            Source: SessionEndpoints.DbLinkSource))).Items[0];
        Assert.Equal("T", usage.TargetName);
        Assert.Equal("基幹DB", usage.Usage!.LinkSourceName);
    }

    [Fact]
    public void すべてを表示する場合はセッションだけの条件でDB_Link利用元を除く()
    {
        Assert.False(SessionEndpoints.HasSessionOnlyCondition(new SessionSearch(null, null, "x", "AP01", null, "u", "o", null, null, null, App: "a", Exact: true)));
        Assert.True(SessionEndpoints.HasSessionOnlyCondition(new SessionSearch(null, "connected", null, null, null, null, null, null, null, null)));
        Assert.True(SessionEndpoints.HasSessionOnlyCondition(new SessionSearch(null, null, null, null, null, null, null, ProcessKinds.Client, null, null)));
        Assert.True(SessionEndpoints.HasSessionOnlyCondition(new SessionSearch(null, null, null, null, null, null, null, null, null, null, Via: "direct")));
    }
}

public class ApiQuerySqlServerTests
{
    [Fact]
    public void セッション検索はSqlServerでも変換できる()
    {
        using var db = new AppDbContext((DbContextOptions<AppDbContext>)AppDatabase.Configure(new DbContextOptionsBuilder<AppDbContext>(),
            AppDbProviders.SqlServer, "Server=localhost;Database=x;Integrated Security=true;TrustServerCertificate=true").Options);
        var sql = SessionEndpoints.Filter(db.Sessions,
            new SessionSearch(1, "connected", "foo", "WEB", null, null, "user", null, null, null, App: "IIS"), db.DirectoryUsers).ToQueryString();
        Assert.Contains("[s].[ProcessKind] IN (N'Client', N'DbLinkSuspect')", sql);
    }

    [Fact]
    public void 完全一致の検索はSqlServerでも変換できる()
    {
        using var db = new AppDbContext((DbContextOptions<AppDbContext>)AppDatabase.Configure(new DbContextOptionsBuilder<AppDbContext>(),
            AppDbProviders.SqlServer, "Server=localhost;Database=x;Integrated Security=true;TrustServerCertificate=true").Options);
        var sql = SessionEndpoints.Filter(db.Sessions,
            new SessionSearch(null, null, null, "WEB01", null, null, null, null, null, null, App: "IIS: Pool", Exact: true)).ToQueryString();
        Assert.DoesNotContain("UPPER(", sql); // インデックスを使えるよう、保存時に正規化した値をそのまま比べる
        Assert.Contains("[s].[LinkOriginMachine]", sql);
        var unknown = SessionEndpoints.Filter(db.Sessions,
            new SessionSearch(null, null, null, SessionEndpoints.UnknownMachine, null, null, null, null, null, null, Exact: true)).ToQueryString();
        Assert.Contains("[s].[Machine] IS NULL", unknown);
    }

    [Fact]
    public async Task 接続アプリ表はSqlServerでも変換できる()
    {
        using var db = new AppDbContext((DbContextOptions<AppDbContext>)AppDatabase.Configure(new DbContextOptionsBuilder<AppDbContext>(),
            AppDbProviders.SqlServer, "Server=localhost;Database=x;Integrated Security=true;TrustServerCertificate=true").Options);
        var sql = ReportEndpoints.ConnectionCountsQuery(db, null, null, new DateTime(2026, 11, 5)).ToQueryString();
        Assert.Contains("GROUP BY", sql);
        var days = ReportEndpoints.MachineDaysQuery(db, null, "WEB", new DateTime(2026, 11, 5)).ToQueryString();
        Assert.Contains("GROUP BY", days);
        var via = SessionEndpoints.Filter(db.Sessions,
            new SessionSearch(null, null, null, "AP01", null, null, null, null, null, null, App: "x", Exact: true, Via: "DB01")).ToQueryString();
        Assert.Contains("[s].[LinkOriginApp] IS NOT NULL", via);
        await Task.CompletedTask;
    }

    [Fact]
    public void 一覧の項目はSqlServerでも変換できる()
    {
        using var db = new AppDbContext((DbContextOptions<AppDbContext>)AppDatabase.Configure(new DbContextOptionsBuilder<AppDbContext>(),
            AppDbProviders.SqlServer, "Server=localhost;Database=x;Integrated Security=true;TrustServerCertificate=true").Options);
        var sql = SessionEndpoints.Project(db.Sessions).ToQueryString();
        Assert.Contains("[LinkOriginApp]", sql);
    }

    [Fact]
    public void セッションとDB_Link利用元をまとめた並べ替えはSqlServerでも変換できる()
    {
        using var db = new AppDbContext((DbContextOptions<AppDbContext>)AppDatabase.Configure(new DbContextOptionsBuilder<AppDbContext>(),
            AppDbProviders.SqlServer, "Server=localhost;Database=x;Integrated Security=true;TrustServerCertificate=true").Options);
        var s = new SessionSearch(1, null, "foo", null, null, null, "山田", null, null, null);
        var sql = SessionEndpoints.KeyQuery(db, s, withSessions: true, withUsages: true)
            .OrderByDescending(k => k.LogonTime).ThenBy(k => k.Order).ThenByDescending(k => k.Id).Skip(50).Take(50).ToQueryString();
        Assert.Contains("UNION ALL", sql);
    }

    [Fact]
    public void DB_Link利用元の検索はSqlServerでも変換できる()
    {
        using var db = new AppDbContext((DbContextOptions<AppDbContext>)AppDatabase.Configure(new DbContextOptionsBuilder<AppDbContext>(),
            AppDbProviders.SqlServer, "Server=localhost;Database=x;Integrated Security=true;TrustServerCertificate=true").Options);
        var sql = SessionEndpoints.FilterUsages(db.DbLinkUsages,
            new SessionSearch(1, null, "foo", "AP01", "w3wp", "u", "山田", null, new DateTime(2026, 11, 1), null, App: "IIS", Exact: true),
            db.DirectoryUsers).ToQueryString();
        Assert.Contains("[d].[Machine] = ", sql);
    }
}

public class ProcessSettingsPasswordTests
{
    [Theory]
    [InlineData(null, "old", "old")]
    [InlineData("********", "old", "old")]
    [InlineData("", "old", null)]
    [InlineData("new", "old", "new")]
    [InlineData(null, null, null)]
    public void パスワードは未変更なら保存済みの値を使う(string? incoming, string? stored, string? expected)
    {
        Assert.Equal(expected, ProcessSettingsEndpoints.MergePassword(incoming, stored));
    }
}

public class ProcessSettingsTestPasswordTests
{
    [Fact]
    public void 接続テストで保存済みパスワードを使うのは同じアカウントの場合だけ()
    {
        Assert.Equal("old", ProcessSettingsEndpoints.TestPassword(@"CORP\svc", "********", @"corp\SVC", "old"));
        Assert.Null(ProcessSettingsEndpoints.TestPassword(@"CORP\other", "********", @"CORP\svc", "old"));
        Assert.Null(ProcessSettingsEndpoints.TestPassword(@"CORP\other", null, @"CORP\svc", "old"));
        Assert.Equal("typed", ProcessSettingsEndpoints.TestPassword(@"CORP\other", "typed", @"CORP\svc", "old"));
    }
}
