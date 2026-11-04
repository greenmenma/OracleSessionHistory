using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenDbSession.Core.Collection;
using OpenDbSession.Core.Data;
using OpenDbSession.Core.DbLinks;
using OpenDbSession.Core.Entities;
using OpenDbSession.Core.Services;

namespace OpenDbSession.Tests;

/// <summary>SQLite インメモリ DB を使った収集処理のテスト</summary>
public sealed class CollectionServiceTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly TestDbFactory _factory;
    private readonly FakeSource _source = new();
    private readonly FakeInspector _inspector = new();
    private readonly CollectionService _service;
    private readonly int _targetId;

    public CollectionServiceTests()
    {
        _conn.Open();
        _factory = new TestDbFactory(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
        var t = new MonitorTarget { Name = "基幹公開DB", Provider = "Fake", ConnectionString = "x", IntervalSeconds = 5 };
        db.Targets.Add(t);
        db.SaveChanges();
        _targetId = t.Id;

        _service = new CollectionService(_factory, new SessionSourceFactory([_source]),
            new DbLinkInspectorFactory([_inspector]), NullLogger<CollectionService>.Instance);
    }

    public void Dispose() => _conn.Dispose();

    private static ObservedSession Obs(int sid, string program = "app.exe", string? process = null) => new()
    {
        InstId = 1, Sid = sid, Serial = 1, LogonTime = new DateTime(2026, 11, 4, 9, 0, sid % 60),
        Program = program, Machine = "PC" + sid, ClientProcess = process, SessionType = "USER",
    };

    [Fact]
    public async Task 取得を繰り返しても同じセッションは1行()
    {
        _source.Next = [Obs(1), Obs(2)];
        await _service.PollSessionsAsync(_targetId, default);
        await _service.PollSessionsAsync(_targetId, default);
        await _service.PollSessionsAsync(_targetId, default);

        using var db = _factory.CreateDbContext();
        Assert.Equal(2, await db.Sessions.CountAsync());
    }

    [Fact]
    public async Task 取得失敗時は切断扱いにしない()
    {
        _source.Next = [Obs(1), Obs(2)];
        await _service.PollSessionsAsync(_targetId, default);

        _source.Fail = true;
        var outcome = await _service.PollSessionsAsync(_targetId, default);
        await _service.PollSessionsAsync(_targetId, default);

        Assert.False(outcome.Success);
        using var db = _factory.CreateDbContext();
        Assert.Equal(2, await db.Sessions.CountAsync(s => s.IsConnected));
        var t = await db.Targets.SingleAsync();
        Assert.Equal(2, t.ConsecutiveFailures);
        Assert.NotNull(t.LastError);
    }

    [Fact]
    public async Task 切断を検知して履歴に残す()
    {
        _source.Next = [Obs(1), Obs(2)];
        await _service.PollSessionsAsync(_targetId, default);
        _source.Next = [Obs(1)];
        var outcome = await _service.PollSessionsAsync(_targetId, default);

        Assert.Equal(1, outcome.Disconnected);
        using var db = _factory.CreateDbContext();
        var history = await db.Sessions.SingleAsync(s => !s.IsConnected);
        Assert.Equal(2, history.Sid);
        Assert.NotNull(history.DisconnectedAt);
    }

    [Fact]
    public async Task DBLink接続元をプロセスIDで監視対象セッションに紐付ける()
    {
        _source.Next = [Obs(1, "ORACLE.EXE", "3120:4410")];
        await _service.PollSessionsAsync(_targetId, default);
        using (var db = _factory.CreateDbContext())
        {
            db.LinkSources.Add(new LinkSource { TargetId = _targetId, Name = "基幹DB", Provider = "Fake", ConnectionString = "y", SelectedLinks = ["PUBLIC.KOUKAI_LINK"] });
            await db.SaveChangesAsync();
        }

        await _service.PollLinkSourcesAsync(_targetId, default);
        await _service.PollLinkSourcesAsync(_targetId, default);

        Assert.Contains("4410", _inspector.LastProcessIds);
        using var db2 = _factory.CreateDbContext();
        var usages = await db2.DbLinkUsages.ToListAsync();
        Assert.Equal(2, usages.Count); // PROCESS 一致と SQL 一致
        var sessionId = await db2.Sessions.Select(s => s.Id).SingleAsync();
        Assert.All(usages, u => Assert.Equal(sessionId, u.MatchedSessionId));
        Assert.All(usages, u => Assert.Equal(2, u.HitCount));
    }

    private sealed class TestDbFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    private sealed class FakeSource : ISessionSource
    {
        public List<ObservedSession> Next { get; set; } = [];
        public bool Fail { get; set; }
        public string Provider => "Fake";

        public Task<SessionSnapshot> CaptureAsync(MonitorTarget target, CancellationToken ct)
        {
            if (Fail) throw new InvalidOperationException("ORA-03113");
            return Task.FromResult(new SessionSnapshot
            {
                CapturedAt = DateTime.Now,
                RespondedInstances = new HashSet<int> { 1 },
                Sessions = Next.ToList(),
            });
        }

        public Task<string> TestConnectionAsync(string connectionString, CancellationToken ct) => Task.FromResult("ok");
    }

    private sealed class FakeInspector : IDbLinkInspector
    {
        public IReadOnlyCollection<string> LastProcessIds { get; private set; } = [];
        public string Provider => "Fake";

        public Task<IReadOnlyList<DbLinkInfo>> ListLinksAsync(string connectionString, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DbLinkInfo>>([]);

        public Task<IReadOnlyList<ObservedLinkUsage>> FindUsagesAsync(LinkSource source, IReadOnlyCollection<string> processIds, CancellationToken ct)
        {
            LastProcessIds = processIds.ToList();
            var u = new ObservedLinkUsage
            {
                DbLinkName = DbLinkMatchTypes.ProcessOnlyLinkName, MatchType = DbLinkMatchTypes.Process,
                InstId = 1, Sid = 77, Serial = 7, LogonTime = new DateTime(2026, 11, 4, 8, 0, 0),
                Machine = "KIKAN-AP01", Program = "受注入力.exe", ServerProcess = "4410",
            };
            return Task.FromResult<IReadOnlyList<ObservedLinkUsage>>(
            [
                u,
                u with { DbLinkName = "PUBLIC.KOUKAI_LINK", MatchType = DbLinkMatchTypes.Sql, SqlText = "select * from T@KOUKAI_LINK" },
            ]);
        }
    }
}
