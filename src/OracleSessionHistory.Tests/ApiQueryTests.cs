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
        bool connected = false, string? owner = null)
    {
        var l = logon ?? Today.AddHours(9);
        _db.Sessions.Add(new TrackedSession
        {
            TargetId = _targetId, InstId = 1, Sid = ++_sid, Serial = 1, LogonTime = l, Machine = machine, Program = "w3wp.exe",
            AppName = app, AppDetail = detail, ProcessKind = kind, ProcOwner = owner, IsConnected = connected,
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

        var kinds = SessionEndpoints.Filter(_db.Sessions, Search()).Select(s => s.ProcessKind).ToList();

        Assert.Equal([ProcessKinds.Client, ProcessKinds.DbLinkSuspect], kinds.Order());
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
    public async Task 接続アプリ表は端末とアプリ単位で集計し内部プロセスを除く()
    {
        Add(ProcessKinds.Client, "WEB01", "IIS: Foo", "Default Web Site/foo", Today.AddHours(9), owner: @"IIS APPPOOL\Foo");
        Add(ProcessKinds.Client, "WEB01", "IIS: Foo", "Default Web Site/foo", Today.AddDays(-1).AddHours(9), connected: true);
        Add(ProcessKinds.Client, "WEB01", "IIS: Bar", null, Today.AddHours(10));
        Add(ProcessKinds.Client, "WEB02", "IIS: Foo", null, Today.AddHours(10));
        Add(ProcessKinds.JobSlave, "DB01", "ORACLE.EXE (J000)", null, Today.AddHours(10));

        var rows = await ReportEndpoints.QueryConnectionCountsAsync(_db, null, null, Today, default);

        Assert.Equal(3, rows.Count);
        var foo = rows.Single(r => r.Machine == "WEB01" && r.AppName == "IIS: Foo");
        Assert.Equal(1, foo.Today);
        Assert.Equal(1, foo.D1);
        Assert.Equal(2, foo.Month1);
        Assert.Equal(1, foo.Connected);
        Assert.Equal("Default Web Site/foo", foo.AppDetail);
        Assert.Equal(@"IIS APPPOOL\Foo", foo.Owner);
        Assert.DoesNotContain(rows, r => r.Machine == "DB01");
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
