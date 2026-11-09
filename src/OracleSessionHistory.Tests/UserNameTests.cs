using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OracleSessionHistory.Core.Data;
using OracleSessionHistory.Core.Entities;
using OracleSessionHistory.Core.Services;
using OracleSessionHistory.Core.Users;
using OracleSessionHistory.Server.Api;

namespace OracleSessionHistory.Tests;

public class UserAccountRulesTests
{
    [Theory]
    [InlineData(@"CORP\yamada", true)]
    [InlineData("yamada@corp.local", true)]
    [InlineData("yamada", true)]
    [InlineData(@"IIS APPPOOL\Koukai", false)]
    [InlineData(@"NT AUTHORITY\SYSTEM", false)]
    [InlineData(@"NT AUTHORITY\NETWORK SERVICE", false)]
    [InlineData("SYSTEM", false)]
    [InlineData(@"CORP\WEB01$", false)] // コンピュータアカウント
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData(@"CORP\", false)]
    public void ADに問い合わせるアカウントか(string? account, bool expected)
    {
        Assert.Equal(expected, UserAccountRules.IsDirectoryAccount(account));
    }

    [Fact]
    public void アカウントの形式を分ける()
    {
        Assert.Equal(("CORP", "yamada", false), UserAccountRules.Parse(@" CORP\yamada "));
        Assert.Equal(("corp.local", "yamada@corp.local", true), UserAccountRules.Parse("yamada@corp.local"));
        Assert.Equal(((string?)null, "yamada", false), UserAccountRules.Parse("yamada"));
        Assert.Equal(@"CORP\YAMADA", UserAccountRules.Normalize(@" corp\Yamada "));
    }

    [Fact]
    public void LDAPのフィルタに埋め込む値をエスケープする()
    {
        Assert.Equal(@"a\2a\28b\29\5c", UserAccountRules.EscapeLdap(@"a*(b)\"));
    }
}

/// <summary>表示名の取得と、表示名でのセッション検索（SQLite インメモリ）</summary>
public sealed class UserNameServiceTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly TestDbFactory _factory;
    private readonly FakeDirectory _directory = new();
    private readonly UserNameService _service;
    private readonly int _targetId;
    private int _sid;

    public UserNameServiceTests()
    {
        _conn.Open();
        _factory = new TestDbFactory(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_conn, o => o.MigrationsAssembly(AppDatabase.SqliteMigrationsAssembly)).Options);
        using var db = _factory.CreateDbContext();
        db.Database.Migrate();
        var t = new MonitorTarget { Name = "T", ConnectionString = "x" };
        db.Targets.Add(t);
        db.SaveChanges();
        _targetId = t.Id;
        _service = new UserNameService(_factory, _directory, NullLogger<UserNameService>.Instance);
    }

    public void Dispose() => _conn.Dispose();

    private void AddSession(string? osUser, string? owner = null, DateTime? lastSeen = null)
    {
        using var db = _factory.CreateDbContext();
        var at = lastSeen ?? DateTime.Now;
        db.Sessions.Add(new TrackedSession
        {
            TargetId = _targetId, InstId = 1, Sid = ++_sid, Serial = 1, LogonTime = at, Machine = "PC01", AppName = "x",
            OsUser = osUser, ProcOwner = owner, FirstSeenAt = at, LastSeenAt = at,
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task 実行ユーザの表示名を取得して保存する()
    {
        AddSession("yamada", @"CORP\yamada");
        AddSession(@"IIS APPPOOL\Koukai"); // 組み込みアカウントは問い合わせない
        AddSession("suzuki", lastSeen: DateTime.Now.AddDays(-100)); // 古いセッションは対象外
        AddSession("unknown");
        _directory.Names["YAMADA"] = "山田 太郎";
        _directory.Names[@"CORP\YAMADA"] = "山田 太郎";

        var r = await _service.RunOnceAsync(default);

        Assert.Equal((3, 2, (string?)null), (r.Looked, r.Found, r.Error));
        Assert.Equal(["CORP\\YAMADA", "UNKNOWN", "YAMADA"], _directory.Calls.Single().Order());
        using var db = _factory.CreateDbContext();
        var users = await db.DirectoryUsers.OrderBy(d => d.Account).ToListAsync();
        Assert.Equal([("CORP\\YAMADA", "山田 太郎", true), ("UNKNOWN", null, false), ("YAMADA", "山田 太郎", true)],
            users.Select(u => (u.Account, u.DisplayName, u.Found)));

        // 1 日以内に調べたアカウントは問い合わせ直さない
        await _service.RunOnceAsync(default);
        Assert.Single(_directory.Calls);
    }

    [Fact]
    public async Task ADに接続できない場合は何も保存しない()
    {
        AddSession("yamada");
        _directory.Fail = true;

        var r = await _service.RunOnceAsync(default);

        Assert.NotNull(r.Error);
        using var db = _factory.CreateDbContext();
        Assert.Empty(db.DirectoryUsers);
    }

    [Fact]
    public void 実行ユーザの条件とキーワードで表示名も探す()
    {
        AddSession("yamada");
        AddSession("suzuki", @"CORP\suzuki");
        AddSession("tanaka");
        using var db = _factory.CreateDbContext();
        db.DirectoryUsers.AddRange(
            new DirectoryUser { Account = "YAMADA", DisplayName = "山田 太郎", Found = true, ResolvedAt = DateTime.Now },
            new DirectoryUser { Account = @"CORP\SUZUKI", DisplayName = "鈴木 花子", Found = true, ResolvedAt = DateTime.Now });
        db.SaveChanges();

        SessionSearch Search(string? q = null, string? osUser = null) =>
            new(null, null, q, null, null, null, osUser, null, null, null);

        Assert.Equal("yamada", SessionEndpoints.Filter(db.Sessions, Search(osUser: "山田"), db.DirectoryUsers).Single().OsUser);
        Assert.Equal("suzuki", SessionEndpoints.Filter(db.Sessions, Search(q: "花子"), db.DirectoryUsers).Single().OsUser);
        Assert.Equal("tanaka", SessionEndpoints.Filter(db.Sessions, Search(osUser: "tana"), db.DirectoryUsers).Single().OsUser);
        // 表示名を渡さない場合はアカウントだけで探す
        Assert.Empty(SessionEndpoints.Filter(db.Sessions, Search(osUser: "山田")));
    }

    [Fact]
    public void 実行ユーザの一覧と表示名の検索はSqlServerでも変換できる()
    {
        using var db = new AppDbContext((DbContextOptions<AppDbContext>)AppDatabase.Configure(new DbContextOptionsBuilder<AppDbContext>(),
            AppDbProviders.SqlServer, "Server=localhost;Database=x;Integrated Security=true;TrustServerCertificate=true").Options);
        Assert.Contains("UNION", UserNameService.AccountsQuery(db, DateTime.Today).ToQueryString());
        var sql = SessionEndpoints.Filter(db.Sessions, new SessionSearch(null, null, "山田", null, null, null, "山田", null, null, null),
            db.DirectoryUsers).ToQueryString();
        Assert.Contains("[DirectoryUsers]", sql);
    }

    private sealed class TestDbFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    private sealed class FakeDirectory : IUserDirectory
    {
        public Dictionary<string, string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<List<string>> Calls { get; } = [];
        public bool Fail { get; set; }

        public Task<IReadOnlyDictionary<string, string?>> LookupAsync(IReadOnlyCollection<string> accounts, CancellationToken ct)
        {
            Calls.Add(accounts.ToList());
            if (Fail) throw new UserDirectoryException("AD に接続できません");
            return Task.FromResult<IReadOnlyDictionary<string, string?>>(
                accounts.ToDictionary(a => a, a => Names.TryGetValue(a, out var n) ? n : null));
        }

        public Task<IReadOnlyList<DirectoryPerson>> SearchAsync(string query, int max, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
