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

    [Fact]
    public async Task 接続アプリ表ではDBLink経由の接続を接続元の端末とアプリでまとめる()
    {
        Add(ProcessKinds.Client, "KIKAN-AP02", "在庫照会.exe", @"D:\Kikan", Today.AddHours(9));
        Add(ProcessKinds.DbLinkSuspect, "KIKAN-DB01", "ORACLE.EXE", null, Today.AddHours(10), connected: true, origin: ("KIKAN-AP02", "在庫照会.exe"));
        Add(ProcessKinds.DbLinkSuspect, "KIKAN-DB01", "ORACLE.EXE", null, Today.AddHours(11));

        var (groups, _) = await ReportEndpoints.QueryConnectionCountGroupsAsync(_db, null, null, Today, default);

        // 直接の接続：端末 → アプリ
        var ap = groups.Single(g => g is { Kind: ConnectionCountKinds.Machine, Machine: "KIKAN-AP02" });
        Assert.Equal(("在庫照会.exe", 1, 0), (ap.Children.Single().AppName, ap.Children.Single().Today, ap.Children.Single().Connected));
        // DB Link 経由：経由した DB サーバー → 接続元の端末・アプリ
        var link = groups.Single(g => g.Kind == ConnectionCountKinds.DbLink);
        Assert.Equal(("KIKAN-DB01", 1, 1), (link.Machine, link.Today, link.Connected));
        Assert.Equal(("KIKAN-AP02", "在庫照会.exe", "KIKAN-DB01"), (link.Children.Single().Machine, link.Children.Single().AppName, link.Children.Single().LinkVia));
        // 接続元を特定できていない ORACLE.EXE は DB サーバーの端末の下に残る
        var db = groups.Single(g => g is { Kind: ConnectionCountKinds.Machine, Machine: "KIKAN-DB01" });
        Assert.Equal(("ORACLE.EXE", 1), (db.Children.Single().AppName, db.Today));
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

        var (groups, _) = await ReportEndpoints.QueryConnectionCountGroupsAsync(_db, null, null, Today, default);
        var rows = groups.SelectMany(g => g.Children).ToList();

        Assert.Equal(3, rows.Count);
        var web01 = groups.Single(g => g.Machine == "WEB01");
        Assert.Equal((2, 3, 1), (web01.Today, web01.Month1, web01.Connected)); // 端末の行は子の合計
        Assert.Equal("IIS: Foo", web01.Children[0].AppName); // 多い順
        Assert.Empty(web01.AuditPeriods);
        var foo = rows.Single(r => r.Machine == "WEB01" && r.AppName == "IIS: Foo");
        Assert.Equal(1, foo.Today);
        Assert.Equal(1, foo.D1);
        Assert.Equal(2, foo.Month1);
        Assert.Equal(1, foo.Connected);
        Assert.Equal("Default Web Site/foo", foo.AppDetail);
        Assert.Equal(@"IIS APPPOOL\Foo", foo.Owner);
        Assert.DoesNotContain(rows, r => r.Machine == "DB01");
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

        var (groups, audit) = await ReportEndpoints.QueryConnectionCountGroupsAsync(_db, null, null, Today, default);

        var web01 = groups.Single(g => g.Machine == "WEB01");
        Assert.Equal((1, 1, 10, 0, 4), (web01.Today, web01.D2, web01.D3, web01.D4, web01.D5));
        Assert.Equal((1 + 1 + 10 + 4, 7), (web01.Month1, web01.Month2));
        Assert.Equal(["d3", "d4", "d5", "d6", "month1", "month2"], web01.AuditPeriods);
        // 子の行（アプリ）は履歴の件数のみ
        Assert.Equal(3, web01.Children.Single().Month1);
        // 履歴に無い端末も監査の件数だけで親の行に出す
        var old = groups.Single(g => g.Machine == "PC-OLD");
        Assert.Equal((2, 0), (old.D4, old.Children.Count));
        Assert.Equal(Today.AddDays(-3), audit.Single().LastDay);
    }

    [Fact]
    public async Task 絞り込み時は端末名が一致する場合だけ監査証跡で補う()
    {
        Add(ProcessKinds.Client, "WEB01", "IIS: Foo", logon: Today.AddHours(9));
        SetAudit(Today.AddMonths(-2), Today.AddDays(-2), (Today.AddDays(-3), "WEB01", 10));

        var (byApp, _) = await ReportEndpoints.QueryConnectionCountGroupsAsync(_db, null, "Foo", Today, default);
        var (byMachine, _) = await ReportEndpoints.QueryConnectionCountGroupsAsync(_db, null, "web0", Today, default);

        Assert.Equal(0, byApp.Single().D3);
        Assert.Equal(10, byMachine.Single().D3);
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
}

public class ApiQuerySqlServerTests
{
    [Fact]
    public void セッション検索はSqlServerでも変換できる()
    {
        using var db = new AppDbContext((DbContextOptions<AppDbContext>)AppDatabase.Configure(new DbContextOptionsBuilder<AppDbContext>(),
            AppDbProviders.SqlServer, "Server=localhost;Database=x;Integrated Security=true;TrustServerCertificate=true").Options);
        var sql = SessionEndpoints.Filter(db.Sessions,
            new SessionSearch(1, "connected", "foo", "WEB", null, null, "user", null, null, null, App: "IIS")).ToQueryString();
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
