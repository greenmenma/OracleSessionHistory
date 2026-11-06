using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OracleSessionHistory.Core.Collection;
using OracleSessionHistory.Core.Data;
using OracleSessionHistory.Core.DbLinks;
using OracleSessionHistory.Core.Entities;
using OracleSessionHistory.Core.Services;

namespace OracleSessionHistory.Tests;

/// <summary>SQLite インメモリ DB を使った収集処理のテスト</summary>
public sealed class CollectionServiceTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly TestDbFactory _factory;
    private readonly FakeSource _source = new();
    private readonly FakeInspector _inspector = new();
    private readonly CollectionService _service;
    private readonly int _targetId;
    private readonly BeforeNextSaveInterceptor _beforeSave = new();

    public CollectionServiceTests()
    {
        _conn.Open();
        _factory = new TestDbFactory(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn, o => o.MigrationsAssembly(AppDatabase.SqliteMigrationsAssembly)).AddInterceptors(_beforeSave).Options);
        using var db = _factory.CreateDbContext();
        db.Database.Migrate();
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
    public async Task 収集中にプロセス情報で確定したアプリ名を上書きしない()
    {
        _source.Next = [Obs(1, "w3wp.exe")];
        await _service.PollSessionsAsync(_targetId, default);

        // 収集処理が行を読み込んだ後・保存する前に、プロセス情報の取得処理がアプリ名を確定した状況
        _beforeSave.Arm("UPDATE Sessions SET ProcInfoState = 'Resolved', AppName = 'IIS: Foo', AppDetail = 'Default Web Site/foo'");
        _source.Next = [Obs(1, "w3wp.exe") with { Module = "別のモジュール" }];
        await _service.PollSessionsAsync(_targetId, default);

        using var db = _factory.CreateDbContext();
        var s = await db.Sessions.SingleAsync();
        Assert.Equal("IIS: Foo", s.AppName);
        Assert.Equal("Default Web Site/foo", s.AppDetail);
        Assert.Equal("別のモジュール", s.Module);
    }

    [Fact]
    public async Task 未確定のアプリ名はモジュールの変化を保存する()
    {
        _source.Next = [Obs(1, "販売.exe")];
        await _service.PollSessionsAsync(_targetId, default);
        _source.Next = [Obs(1, "販売.exe") with { Module = "受注入力" }];
        await _service.PollSessionsAsync(_targetId, default);

        using var db = _factory.CreateDbContext();
        Assert.Equal("受注入力", (await db.Sessions.SingleAsync()).AppDetail);
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
        var session = await db2.Sessions.SingleAsync();
        Assert.Equal(("KIKAN-AP01", "受注入力.exe", "基幹DB"), (session.LinkOriginMachine, session.LinkOriginApp, session.LinkOriginSource));
    }

    [Fact]
    public async Task DBLink利用元が無くなったセッションは接続元のアプリを外す()
    {
        _source.Next = [Obs(1, "ORACLE.EXE", "3120:4410")];
        await _service.PollSessionsAsync(_targetId, default);
        int linkSourceId;
        using (var db = _factory.CreateDbContext())
        {
            var l = new LinkSource { TargetId = _targetId, Name = "基幹DB", Provider = "Fake", ConnectionString = "y" };
            db.LinkSources.Add(l);
            await db.SaveChangesAsync();
            linkSourceId = l.Id;
        }
        await _service.PollLinkSourcesAsync(_targetId, default);

        using (var db = _factory.CreateDbContext())
        {
            // DB Link 接続元の削除（利用元は連鎖削除）・保持期間による削除の後
            await db.LinkSources.Where(l => l.Id == linkSourceId).ExecuteDeleteAsync();
            await LinkOriginSync.ClearOrphansAsync(db, default);
        }

        using var db2 = _factory.CreateDbContext();
        var s = await db2.Sessions.SingleAsync();
        Assert.Null(s.LinkOriginApp);
        Assert.Null(s.LinkOriginMachine);
    }

    [Fact]
    public async Task 保持期間で利用元だけ削除されたら接続元のアプリを外す()
    {
        _source.Next = [Obs(1, "ORACLE.EXE", "3120:4410")];
        await _service.PollSessionsAsync(_targetId, default);
        using (var db = _factory.CreateDbContext())
        {
            db.LinkSources.Add(new LinkSource { TargetId = _targetId, Name = "基幹DB", Provider = "Fake", ConnectionString = "y" });
            await db.SaveChangesAsync();
        }
        await _service.PollLinkSourcesAsync(_targetId, default);
        using (var db = _factory.CreateDbContext())
            await db.DbLinkUsages.ExecuteUpdateAsync(x => x.SetProperty(u => u.LastSeenAt, DateTime.Now.AddDays(-200)));

        using (var db = _factory.CreateDbContext())
            await AppDbMaintenance.DeleteExpiredAsync(db, DateTime.Now.AddDays(-100), default);

        using var db2 = _factory.CreateDbContext();
        Assert.Null((await db2.Sessions.SingleAsync()).LinkOriginApp);
    }

    [Fact]
    public async Task DBLink利用元の付け替えで前のセッションの接続元のアプリを外す()
    {
        _source.Next = [Obs(1, "ORACLE.EXE", "3120:4410"), Obs(2, "ORACLE.EXE", "3120:9999")];
        await _service.PollSessionsAsync(_targetId, default);
        using (var db = _factory.CreateDbContext())
        {
            db.LinkSources.Add(new LinkSource { TargetId = _targetId, Name = "基幹DB", Provider = "Fake", ConnectionString = "y" });
            await db.SaveChangesAsync();
        }
        await _service.PollLinkSourcesAsync(_targetId, default);
        // 同じ接続元セッションが、監視対象側の別のセッション（spid 9999）と一致するようになった
        _inspector.ServerProcess = "9999";

        await _service.PollLinkSourcesAsync(_targetId, default);

        using var db2 = _factory.CreateDbContext();
        var rows = await db2.Sessions.OrderBy(s => s.Sid).ToListAsync();
        Assert.Null(rows[0].LinkOriginApp);
        Assert.Equal("受注入力.exe", rows[1].LinkOriginApp);
    }

    [Fact]
    public async Task DBLink接続元のクライアントPIDと仮のアプリ名を記録する()
    {
        _source.Next = [Obs(1, "ORACLE.EXE", "3120:4410")];
        await _service.PollSessionsAsync(_targetId, default);
        using (var db = _factory.CreateDbContext())
        {
            db.LinkSources.Add(new LinkSource { TargetId = _targetId, Name = "基幹DB", Provider = "Fake", ConnectionString = "y" });
            await db.SaveChangesAsync();
        }

        await _service.PollLinkSourcesAsync(_targetId, default);

        using var db2 = _factory.CreateDbContext();
        var u = await db2.DbLinkUsages.SingleAsync();
        Assert.Equal("5120:88", u.ClientProcess);
        Assert.Equal("受注入力.exe", u.AppName);
        Assert.Equal("受注", u.AppDetail);
        // w3wp.exe・sqlplus.exe 以外はプロセス情報を取得しない
        Assert.Equal(ProcInfoStates.NotApplicable, u.ProcInfoState);
    }

    [Fact]
    public async Task DBLink接続元の確定したアプリ名は再観測で上書きしない()
    {
        _source.Next = [Obs(1, "ORACLE.EXE", "3120:4410")];
        await _service.PollSessionsAsync(_targetId, default);
        using (var db = _factory.CreateDbContext())
        {
            db.LinkSources.Add(new LinkSource { TargetId = _targetId, Name = "基幹DB", Provider = "Fake", ConnectionString = "y" });
            await db.SaveChangesAsync();
        }
        await _service.PollLinkSourcesAsync(_targetId, default);
        using (var db = _factory.CreateDbContext())
        {
            await db.DbLinkUsages.ExecuteUpdateAsync(x => x
                .SetProperty(u => u.AppName, "IIS: Juchu").SetProperty(u => u.ProcInfoState, ProcInfoStates.Resolved));
        }

        await _service.PollLinkSourcesAsync(_targetId, default);

        using var db2 = _factory.CreateDbContext();
        var row = await db2.DbLinkUsages.SingleAsync();
        Assert.Equal("IIS: Juchu", row.AppName);
        Assert.Equal(ProcInfoStates.Resolved, row.ProcInfoState);
        Assert.Equal(2, row.HitCount);
    }

    [Fact]
    public async Task DBLink接続元の警告は記録して検索結果は保存する()
    {
        _source.Next = [Obs(1, "ORACLE.EXE", "3120:4410")];
        await _service.PollSessionsAsync(_targetId, default);
        using (var db = _factory.CreateDbContext())
        {
            db.LinkSources.Add(new LinkSource { TargetId = _targetId, Name = "基幹DB", Provider = "Fake", ConnectionString = "y", SelectedLinks = ["PUBLIC.KOUKAI_LINK"] });
            await db.SaveChangesAsync();
        }
        _inspector.Warnings.Add("SQL 一致: gv$sqlarea を参照できません");

        await _service.PollLinkSourcesAsync(_targetId, default);

        using var db2 = _factory.CreateDbContext();
        var source = await db2.LinkSources.SingleAsync();
        Assert.StartsWith("（警告）", source.LastError);
        Assert.Contains("gv$sqlarea", source.LastError);
        Assert.NotNull(source.LastSuccessAt);
        Assert.Equal(2, await db2.DbLinkUsages.CountAsync());
    }

    [Fact]
    public async Task 監査証跡のログオン件数を履歴が丸1日そろう前日まで取得する()
    {
        var firstSeen = DateTime.Today.AddDays(-10).AddHours(13);
        using (var db = _factory.CreateDbContext())
        {
            db.Sessions.Add(new TrackedSession
            {
                TargetId = _targetId, InstId = 1, Sid = 1, Serial = 1, LogonTime = firstSeen, Machine = "WEB01", AppName = "x",
                FirstSeenAt = firstSeen, LastSeenAt = firstSeen,
            });
            db.SaveChanges();
        }
        _source.Audit =
        [
            new(DateTime.Today.AddDays(-20), "WEB01", 5),
            new(DateTime.Today.AddDays(-20), " web01 ", 2), // 大文字小文字・空白の違いは同じ端末
            new(DateTime.Today.AddDays(-10), null, 3),
        ];

        var r = await _service.PollAuditLogonsAsync(_targetId, default);

        Assert.True(r is { Needed: true, Success: true, Rows: 2 });
        var from = DateTime.Today.AddMonths(-2);
        var to = DateTime.Today.AddDays(-9);
        Assert.Equal((from, to), _source.AuditCalls.Single());
        using var db2 = _factory.CreateDbContext();
        var rows = await db2.AuditLogonCounts.OrderBy(a => a.Day).ToListAsync();
        Assert.Equal([("WEB01", 7), ("", 3)], rows.Select(a => (a.Machine, a.Count)));
        var t = await db2.Targets.SingleAsync();
        Assert.Equal((from, to, (string?)null), (t.AuditFrom, t.AuditTo, t.AuditError));
    }

    [Fact]
    public async Task 履歴が2ヶ月分たまったら監査証跡を取得しない()
    {
        var firstSeen = DateTime.Today.AddMonths(-2).AddDays(-1);
        using (var db = _factory.CreateDbContext())
        {
            db.Sessions.Add(new TrackedSession
            {
                TargetId = _targetId, InstId = 1, Sid = 1, Serial = 1, LogonTime = firstSeen, Machine = "WEB01", AppName = "x",
                FirstSeenAt = firstSeen, LastSeenAt = firstSeen,
            });
            db.SaveChanges();
        }

        var r = await _service.PollAuditLogonsAsync(_targetId, default);

        Assert.False(r.Needed);
        Assert.Empty(_source.AuditCalls);
    }

    [Fact]
    public async Task 監査証跡の取得に失敗したら理由を残し保存済みの件数は消さない()
    {
        using (var db = _factory.CreateDbContext())
        {
            db.AuditLogonCounts.Add(new AuditLogonCount { TargetId = _targetId, Day = DateTime.Today.AddDays(-3), Machine = "WEB01", Count = 4 });
            db.SaveChanges();
        }
        _source.AuditFail = true;

        var r = await _service.PollAuditLogonsAsync(_targetId, default);

        Assert.False(r.Success);
        using var db2 = _factory.CreateDbContext();
        Assert.Contains("ORA-00942", (await db2.Targets.SingleAsync()).AuditError);
        Assert.Equal(1, await db2.AuditLogonCounts.CountAsync());
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

        public List<AuditLogonDay> Audit { get; set; } = [];
        public bool AuditFail { get; set; }
        public List<(DateTime From, DateTime To)> AuditCalls { get; } = [];

        public Task<IReadOnlyList<AuditLogonDay>> CaptureAuditLogonsAsync(MonitorTarget target, DateTime from, DateTime to, CancellationToken ct)
        {
            AuditCalls.Add((from, to));
            if (AuditFail) throw new InvalidOperationException("ORA-00942: 表またはビューが存在しません");
            return Task.FromResult<IReadOnlyList<AuditLogonDay>>(Audit.Where(a => a.Day >= from && a.Day < to).ToList());
        }
    }

    private sealed class FakeInspector : IDbLinkInspector
    {
        public IReadOnlyCollection<string> LastProcessIds { get; private set; } = [];
        public string ServerProcess { get; set; } = "4410";
        public string Provider => "Fake";

        public Task<IReadOnlyList<DbLinkInfo>> ListLinksAsync(string connectionString, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DbLinkInfo>>([]);

        public List<string> Warnings { get; } = [];

        public Task<DbLinkUsageResult> FindUsagesAsync(LinkSource source, IReadOnlyCollection<string> processIds, CancellationToken ct)
        {
            LastProcessIds = processIds.ToList();
            var u = new ObservedLinkUsage
            {
                DbLinkName = DbLinkMatchTypes.ProcessOnlyLinkName, MatchType = DbLinkMatchTypes.Process,
                InstId = 1, Sid = 77, Serial = 7, LogonTime = new DateTime(2026, 11, 4, 8, 0, 0),
                Machine = "KIKAN-AP01", Program = "受注入力.exe", Module = "受注", ServerProcess = ServerProcess, ClientProcess = "5120:88",
            };
            List<ObservedLinkUsage> list = [u];
            if (source.SelectedLinks.Count > 0)
                list.Add(u with { DbLinkName = "PUBLIC.KOUKAI_LINK", MatchType = DbLinkMatchTypes.Sql, SqlText = "select * from T@KOUKAI_LINK" });
            return Task.FromResult(new DbLinkUsageResult(list, Warnings.ToList()));
        }
    }
}
