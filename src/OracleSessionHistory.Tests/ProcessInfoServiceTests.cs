using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OracleSessionHistory.Core.Data;
using OracleSessionHistory.Core.Entities;
using OracleSessionHistory.Core.Processes;
using OracleSessionHistory.Core.Services;

namespace OracleSessionHistory.Tests;

/// <summary>プロセス情報の取得処理（SQLite インメモリ＋偽の取得元）</summary>
public sealed class ProcessInfoServiceTests : IDisposable
{
    private static readonly DateTime Logon = new(2026, 11, 5, 9, 0, 0);
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly TestDbFactory _factory;
    private readonly FakeInspector _inspector = new();
    private readonly ProcessInfoService _service;
    private readonly int _targetId;

    public ProcessInfoServiceTests()
    {
        _conn.Open();
        _factory = new TestDbFactory(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_conn, o => o.MigrationsAssembly(AppDatabase.SqliteMigrationsAssembly)).Options);
        using var db = _factory.CreateDbContext();
        db.Database.Migrate();
        var t = new MonitorTarget { Name = "T", Provider = SourceProviders.Oracle, ConnectionString = "x" };
        db.Targets.Add(t);
        db.ProcessInspectionSettings.Add(new ProcessInspectionSettings
        {
            Id = ProcessInspectionSettings.SingletonId, Enabled = true, UserName = @"CORP\svc", Password = "p", ExcludeMachines = "PC-*",
        });
        db.SaveChanges();
        _targetId = t.Id;
        _service = new ProcessInfoService(_factory, new ProcessInspectorResolver(_inspector, new DemoProcessInspector()),
            new HostBackoff(), NullLogger<ProcessInfoService>.Instance);
    }

    public void Dispose() => _conn.Dispose();

    private long AddSession(int sid, string machine, string? process, string program = "w3wp.exe", bool connected = true,
        string kind = ProcessKinds.Client, string state = ProcInfoStates.Pending)
    {
        using var db = _factory.CreateDbContext();
        var s = new TrackedSession
        {
            TargetId = _targetId, InstId = 1, Sid = sid, Serial = 1, LogonTime = Logon, Machine = machine, ClientProcess = process,
            Program = program, AppName = program, ProcessKind = kind, ProcInfoState = state, IsConnected = connected,
            FirstSeenAt = Logon, LastSeenAt = Logon,
        };
        db.Sessions.Add(s);
        db.SaveChanges();
        return s.Id;
    }

    private TrackedSession Get(long id)
    {
        using var db = _factory.CreateDbContext();
        return db.Sessions.AsNoTracking().Single(s => s.Id == id);
    }

    private static ClientProcessInfo W3wp(int pid, string pool, DateTime? started = null) =>
        new(pid, "w3wp.exe", @"c:\windows\system32\inetsrv\w3wp.exe", $"w3wp.exe -ap \"{pool}\"", @"IIS APPPOOL\" + pool,
            started ?? Logon.AddHours(-1), "svchost.exe", pool, "Default Web Site/foo");

    [Fact]
    public async Task 取得できたらアプリ名とプロセス情報を保存する()
    {
        var id = AddSession(1, @"CORP\WEB01", "1234:5678");
        _inspector.Results["WEB01"] = [W3wp(1234, "Foo")];

        await _service.RunOnceAsync(default);

        var s = Get(id);
        Assert.Equal(ProcInfoStates.Resolved, s.ProcInfoState);
        Assert.Equal("IIS: Foo", s.AppName);
        Assert.Equal("Default Web Site/foo", s.AppDetail);
        Assert.Equal("Foo", s.IisAppPool);
        Assert.Equal(@"IIS APPPOOL\Foo", s.ProcOwner);
        Assert.Equal("svchost.exe", s.ProcParentName);
        Assert.NotNull(s.ProcInfoAt);
        Assert.Equal([1234], _inspector.Calls.Single().Pids);
        Assert.Equal(@"CORP\svc", _inspector.Calls.Single().Options.UserName);
    }

    [Fact]
    public async Task プロセスが無ければ終了済みとして再試行しない()
    {
        var id = AddSession(1, "WEB01", "1234");
        _inspector.Results["WEB01"] = [];

        await _service.RunOnceAsync(default);
        await _service.RunOnceAsync(default);

        Assert.Equal(ProcInfoStates.NotFound, Get(id).ProcInfoState);
        Assert.Single(_inspector.Calls);
    }

    [Fact]
    public async Task 開始時刻がログオンより後のプロセスは紐付けない()
    {
        var id = AddSession(1, "WEB01", "1234");
        _inspector.Results["WEB01"] = [W3wp(1234, "Foo", Logon.AddMinutes(10))];

        await _service.RunOnceAsync(default);

        var s = Get(id);
        Assert.Equal(ProcInfoStates.Mismatch, s.ProcInfoState);
        Assert.Equal("w3wp.exe", s.AppName);
        Assert.Null(s.IisAppPool);
    }

    [Fact]
    public async Task ホストの失敗は再試行待ちにし他のホストは処理する()
    {
        var bad = AddSession(1, "WEB01", "1");
        var good = AddSession(2, "WEB02", "2");
        _inspector.Failures["WEB01"] = "アクセスが拒否されました";
        _inspector.Results["WEB02"] = [W3wp(2, "Bar")];

        await _service.RunOnceAsync(default);
        await _service.RunOnceAsync(default);

        var b = Get(bad);
        Assert.Equal(ProcInfoStates.Failed, b.ProcInfoState);
        Assert.Contains("アクセスが拒否されました", b.ProcInfoError);
        Assert.Equal(ProcInfoStates.Resolved, Get(good).ProcInfoState);
        // 2 回目は再試行待ちのため WEB01 に問い合わせない
        Assert.Single(_inspector.Calls, c => c.Host == "WEB01");
    }

    [Fact]
    public async Task 切断済みやクライアント以外は対象外()
    {
        AddSession(1, "WEB01", "1", connected: false);
        AddSession(2, "DB01", "2:3", program: "ORACLE.EXE", kind: ProcessKinds.DbLinkSuspect, state: ProcInfoStates.NotApplicable);

        await _service.RunOnceAsync(default);

        Assert.Empty(_inspector.Calls);
    }

    [Fact]
    public async Task 端末名やPIDが無い行と対象外端末は対象外にする()
    {
        var noPid = AddSession(1, "WEB01", "abc");
        var noHost = AddSession(2, "", "1");
        var excluded = AddSession(3, "PC-001", "1");

        await _service.RunOnceAsync(default);

        Assert.Equal(ProcInfoStates.NotApplicable, Get(noPid).ProcInfoState);
        Assert.Equal(ProcInfoStates.NotApplicable, Get(noHost).ProcInfoState);
        Assert.Equal(ProcInfoStates.NotApplicable, Get(excluded).ProcInfoState);
        Assert.Contains("対象外", Get(excluded).ProcInfoError);
        Assert.Empty(_inspector.Calls);
    }

    [Fact]
    public async Task 無効の場合は問い合わせない()
    {
        using (var db = _factory.CreateDbContext())
        {
            (await db.ProcessInspectionSettings.SingleAsync()).Enabled = false;
            await db.SaveChangesAsync();
        }
        var id = AddSession(1, "WEB01", "1");

        await _service.RunOnceAsync(default);

        Assert.Empty(_inspector.Calls);
        Assert.Equal(ProcInfoStates.Pending, Get(id).ProcInfoState);
    }

    [Fact]
    public async Task 想定外の例外でもその端末を失敗として続ける()
    {
        var id = AddSession(1, "WEB01", "1");
        _inspector.Unexpected["WEB01"] = new InvalidOperationException("boom");

        await _service.RunOnceAsync(default);

        Assert.Equal(ProcInfoStates.Failed, Get(id).ProcInfoState);
    }

    [Fact]
    public async Task 応答しない端末はタイムアウトで打ち切り他の端末を処理する()
    {
        using (var db = _factory.CreateDbContext())
        {
            (await db.ProcessInspectionSettings.SingleAsync()).TimeoutSeconds = 1;
            await db.SaveChangesAsync();
        }
        var hung = AddSession(1, "HUNG01", "1");
        var good = AddSession(2, "WEB02", "2");
        _inspector.Hang.Add("HUNG01");
        _inspector.Results["WEB02"] = [W3wp(2, "Bar")];

        var run = _service.RunOnceAsync(default);
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(15)));

        Assert.Same(run, finished);
        Assert.Equal(ProcInfoStates.Failed, Get(hung).ProcInfoState);
        Assert.Equal(ProcInfoStates.Resolved, Get(good).ProcInfoState);
    }

    [Fact]
    public async Task 再試行待ちの失敗行が多くても新しい行を処理する()
    {
        _service.BatchSize = 3;
        for (var i = 1; i <= 5; i++) AddSession(i, "DEAD01", i.ToString(), state: ProcInfoStates.Failed);
        _inspector.Failures["DEAD01"] = "接続できません";
        await _service.RunOnceAsync(default); // DEAD01 を再試行待ちにする
        var fresh = AddSession(10, "WEB02", "10");
        _inspector.Results["WEB02"] = [W3wp(10, "Bar")];

        await _service.RunOnceAsync(default);

        Assert.Equal(ProcInfoStates.Resolved, Get(fresh).ProcInfoState);
    }

    private sealed class TestDbFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    private sealed class FakeInspector : IProcessInspector
    {
        public Dictionary<string, List<ClientProcessInfo>> Results { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Failures { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Exception> Unexpected { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<(string Host, int[] Pids, ProcessInspectorOptions Options)> Calls { get; } = [];
        /// <summary>キャンセルを無視して応答しない端末（DCOM の接続待ちなど）</summary>
        public HashSet<string> Hang { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlyList<ClientProcessInfo>> InspectAsync(string host, IReadOnlyCollection<int> pids, ProcessInspectorOptions options, CancellationToken ct)
        {
            lock (Calls) Calls.Add((host, pids.Order().ToArray(), options));
            if (Hang.Contains(host)) return new TaskCompletionSource<IReadOnlyList<ClientProcessInfo>>().Task;
            if (Failures.TryGetValue(host, out var msg)) throw new ProcessInspectionException(msg);
            if (Unexpected.TryGetValue(host, out var ex)) throw ex;
            return Task.FromResult<IReadOnlyList<ClientProcessInfo>>(Results.TryGetValue(host, out var r) ? r : []);
        }

        public Task<string> TestAsync(string host, ProcessInspectorOptions options, CancellationToken ct) => Task.FromResult("ok");
    }
}
