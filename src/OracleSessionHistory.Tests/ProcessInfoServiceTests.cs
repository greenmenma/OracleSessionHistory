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
    private readonly int _linkSourceId;
    private readonly BeforeNextSaveInterceptor _beforeSave = new();

    public ProcessInfoServiceTests()
    {
        _conn.Open();
        _factory = new TestDbFactory(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_conn, o => o.MigrationsAssembly(AppDatabase.SqliteMigrationsAssembly)).AddInterceptors(_beforeSave).Options);
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
        var l = new LinkSource { TargetId = t.Id, Name = "基幹DB", Provider = SourceProviders.Oracle, ConnectionString = "y" };
        db.LinkSources.Add(l);
        db.SaveChanges();
        _linkSourceId = l.Id;
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
        Assert.Equal(ProcInfoStates.NotApplicable, Get(id).ProcInfoState); // 「取得待ち」のまま表示し続けない
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

    [Fact]
    public async Task 取得中に行が削除されても他の行の結果は保存する()
    {
        var deleted = AddSession(1, "WEB01", "1");
        var kept = AddSession(2, "WEB01", "2");
        _inspector.Results["WEB01"] = [W3wp(1, "Foo"), W3wp(2, "Foo")];
        _beforeSave.Arm($"DELETE FROM Sessions WHERE Id = {deleted}");

        await _service.RunOnceAsync(default);

        using var db = _factory.CreateDbContext();
        Assert.False(await db.Sessions.AnyAsync(s => s.Id == deleted));
        Assert.Equal(ProcInfoStates.Resolved, Get(kept).ProcInfoState);
    }

    [Fact]
    public async Task 取得中に多くの行が削除されても残りの行の結果は保存する()
    {
        var ids = Enumerable.Range(1, 6).Select(i => AddSession(i, "WEB01", i.ToString())).ToList();
        _inspector.Results["WEB01"] = Enumerable.Range(1, 6).Select(i => W3wp(i, "Foo")).ToList();
        // 監視対象の削除などで、接続中のセッションがまとめて消えた状況
        _beforeSave.Arm($"DELETE FROM Sessions WHERE Id IN ({string.Join(",", ids.Take(5))})");

        await _service.RunOnceAsync(default);

        Assert.Equal(ProcInfoStates.Resolved, Get(ids[5]).ProcInfoState);
    }

    [Fact]
    public async Task JDBCの既定のPROCESSは問い合わせず理由を残す()
    {
        var id = AddSession(1, "APP01", "1234", program: "JDBC Thin Client");

        await _service.RunOnceAsync(default);

        var s = Get(id);
        Assert.Equal(ProcInfoStates.NotApplicable, s.ProcInfoState);
        Assert.Contains("JDBC", s.ProcInfoError);
        Assert.Empty(_inspector.Calls);
    }

    private sealed class TestDbFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    private long AddUsage(int sid, string machine, string? process, string program, DateTime? lastSeen = null,
        string state = ProcInfoStates.Pending, long? matchedSessionId = null)
    {
        using var db = _factory.CreateDbContext();
        var u = new DbLinkUsage
        {
            LinkSourceId = _linkSourceId, TargetId = _targetId, DbLinkName = DbLinkMatchTypes.ProcessOnlyLinkName,
            MatchType = DbLinkMatchTypes.Process, InstId = 1, Sid = sid, Serial = 1, LogonTime = Logon, Machine = machine,
            ClientProcess = process, Program = program, AppName = program, ProcInfoState = state,
            FirstSeenAt = Logon, LastSeenAt = lastSeen ?? DateTime.Now, HitCount = 1, MatchedSessionId = matchedSessionId,
        };
        db.DbLinkUsages.Add(u);
        db.SaveChanges();
        return u.Id;
    }

    private DbLinkUsage GetUsage(long id)
    {
        using var db = _factory.CreateDbContext();
        return db.DbLinkUsages.AsNoTracking().Single(u => u.Id == id);
    }

    [Fact]
    public async Task 取得が無効の間は未取得の行を対象外にする()
    {
        using (var db = _factory.CreateDbContext())
            db.ProcessInspectionSettings.ExecuteUpdate(x => x.SetProperty(s => s.Enabled, false));
        var session = AddSession(1, "WEB01", "1");
        var usage = AddUsage(1, "KIKAN-AP01", "2", "w3wp.exe");

        await _service.RunOnceAsync(default);

        Assert.Empty(_inspector.Calls);
        Assert.Equal(ProcInfoStates.NotApplicable, Get(session).ProcInfoState);
        Assert.Contains("無効", Get(session).ProcInfoError);
        Assert.Equal(ProcInfoStates.NotApplicable, GetUsage(usage).ProcInfoState);

        // 有効にして保存すると調べ直す
        using (var db = _factory.CreateDbContext())
        {
            db.ProcessInspectionSettings.ExecuteUpdate(x => x.SetProperty(s => s.Enabled, true));
            await ProcessInfoService.RequeueSkippedAsync(db, default);
        }
        _inspector.Results["WEB01"] = [W3wp(1, "Foo")];
        await _service.RunOnceAsync(default);
        Assert.Equal(ProcInfoStates.Resolved, Get(session).ProcInfoState);
    }

    [Fact]
    public async Task 取得前に切断したセッションは取得待ちのまま残さない()
    {
        var pending = AddSession(1, "WEB01", "1", connected: false);
        var failed = AddSession(2, "WEB01", "2", connected: false, state: ProcInfoStates.Failed);

        await _service.RunOnceAsync(default);

        Assert.Equal(ProcInfoStates.NotApplicable, Get(pending).ProcInfoState);
        Assert.Contains("切断", Get(pending).ProcInfoError);
        // 失敗の理由は残す
        Assert.Equal(ProcInfoStates.Failed, Get(failed).ProcInfoState);
    }

    [Fact]
    public async Task sqlplusは実行元を保存しコマンドラインのパスワードを隠す()
    {
        var id = AddSession(1, "BATCH01", "300", program: "sqlplus.exe");
        _inspector.Results["BATCH01"] =
        [
            new(300, "sqlplus.exe", @"C:\oracle\bin\sqlplus.exe", "sqlplus.exe scott/tiger@KOUKAI @D:\\sql\\monthly.sql", @"CORP\batch",
                Logon.AddMinutes(-1), "cmd.exe", null, null,
                [
                    new(290, "cmd.exe", "cmd.exe /c D:\\Batch\\月次.bat scott/tiger", Logon.AddMinutes(-2)),
                    new(10, "jp1ajs2.exe", null, Logon.AddDays(-3)),
                ]),
        ];

        await _service.RunOnceAsync(default);

        var s = Get(id);
        Assert.Equal(ProcInfoStates.Resolved, s.ProcInfoState);
        Assert.Equal("sqlplus.exe: 月次.bat", s.AppName);
        Assert.Equal("sqlplus.exe scott/********@KOUKAI @D:\\sql\\monthly.sql", s.ProcCommandLine);
        Assert.Equal("cmd.exe: cmd.exe /c D:\\Batch\\月次.bat scott/********\njp1ajs2.exe", s.ProcAncestry);
        Assert.DoesNotContain("tiger", s.AppDetail ?? "");
    }

    [Fact]
    public async Task DBLink利用元のプロセスを調べてアプリ名を確定する()
    {
        var id = AddUsage(1, @"CORP\KIKAN-AP01", "5120:88", "w3wp.exe");
        _inspector.Results["KIKAN-AP01"] = [W3wp(5120, "Juchu")];

        await _service.RunOnceAsync(default);

        var u = GetUsage(id);
        Assert.Equal(ProcInfoStates.Resolved, u.ProcInfoState);
        Assert.Equal("IIS: Juchu", u.AppName);
        Assert.Equal("Default Web Site/foo", u.AppDetail);
        Assert.Equal(@"IIS APPPOOL\Juchu", u.ProcOwner);
        Assert.Equal(@"c:\windows\system32\inetsrv\w3wp.exe", u.ProcPath);
        Assert.Equal([5120], _inspector.Calls.Single().Pids);
    }

    [Fact]
    public async Task アプリ名はパスワードを隠す前の値で決める()
    {
        var id = AddSession(1, "BATCH01", "300", program: "sqlplus.exe");
        _inspector.Results["BATCH01"] =
        [
            new(300, "sqlplus.exe", null, "sqlplus scott/tiger@db", null, Logon.AddMinutes(-1), "powershell.exe", null, null,
                [new(290, "powershell.exe", "powershell -File scripts/run.ps1", Logon.AddMinutes(-2)), new(10, "jp1.exe", null, null)]),
        ];

        await _service.RunOnceAsync(default);

        Assert.Equal("sqlplus.exe: run.ps1", Get(id).AppName);
    }

    [Fact]
    public async Task DBLink利用元のアプリが確定したら監視対象側のセッションに反映する()
    {
        var link = AddSession(1, "KIKAN-DB01", "3120:4410", program: "ORACLE.EXE", kind: ProcessKinds.DbLinkSuspect, state: ProcInfoStates.NotApplicable);
        AddUsage(1, "KIKAN-AP01", "5120", "w3wp.exe", matchedSessionId: link);
        _inspector.Results["KIKAN-AP01"] = [W3wp(5120, "Juchu")];

        await _service.RunOnceAsync(default);

        var s = Get(link);
        Assert.Equal(("KIKAN-AP01", "IIS: Juchu"), (s.LinkOriginMachine, s.LinkOriginApp));
    }

    [Fact]
    public async Task DBLink利用元のexeのフォルダを記録する()
    {
        var id = AddUsage(1, "KIKAN-AP02", "77", "在庫照会.exe");
        _inspector.Results["KIKAN-AP02"] = [new(77, "在庫照会.exe", @"D:\Kikan\在庫照会.exe", null, @"CORP\kikan02", Logon.AddHours(-1), "explorer.exe", null, null)];

        await _service.RunOnceAsync(default);

        var u = GetUsage(id);
        Assert.Equal(ProcInfoStates.Resolved, u.ProcInfoState);
        Assert.Equal(("在庫照会.exe", @"D:\Kikan"), (u.AppName, u.AppDetail));
    }

    [Fact]
    public async Task DBLink利用元も別のプロセスなら紐付けない()
    {
        var id = AddUsage(1, "KIKAN-AP01", "5120", "w3wp.exe");
        _inspector.Results["KIKAN-AP01"] = [W3wp(5120, "Juchu", started: Logon.AddHours(1))];

        await _service.RunOnceAsync(default);

        var u = GetUsage(id);
        Assert.Equal(ProcInfoStates.Mismatch, u.ProcInfoState);
        Assert.Equal("w3wp.exe", u.AppName);
    }

    [Theory]
    [InlineData("ORACLE.EXE (J001)")]
    [InlineData("oracle@kikan-db (J000)")]
    [InlineData("ORACLE.EXE")]
    public async Task DBLink利用元がOracle内部プロセスや別のDBLinkなら調べない(string program)
    {
        var id = AddUsage(1, "KIKAN-DB01", "3120:4410", program);

        await _service.RunOnceAsync(default);

        Assert.Empty(_inspector.Calls);
        var u = GetUsage(id);
        Assert.Equal(ProcInfoStates.NotApplicable, u.ProcInfoState);
        Assert.Equal(program, u.AppName);
    }

    [Fact]
    public async Task 取得中に設定が変わったら対象外の判断を保存しない()
    {
        var target = AddSession(1, "WEB01", "1");
        var excluded = AddSession(2, "PC-01", "2");
        _inspector.Results["WEB01"] = [W3wp(1, "Foo")];
        // 問い合わせ中に画面から設定（対象外の端末を解除）を保存した想定
        _inspector.OnInspect = () =>
        {
            using var db = _factory.CreateDbContext();
            db.ProcessInspectionSettings.ExecuteUpdate(x => x
                .SetProperty(s => s.ExcludeMachines, (string?)null).SetProperty(s => s.UpdatedAt, DateTime.Now));
        };

        await _service.RunOnceAsync(default);

        Assert.Equal(ProcInfoStates.Resolved, Get(target).ProcInfoState);
        Assert.Equal(ProcInfoStates.Pending, Get(excluded).ProcInfoState);
    }

    [Fact]
    public async Task 取得中にDBLink利用元が削除されても他の結果は保存する()
    {
        var deleted = AddUsage(1, "KIKAN-AP01", "1", "w3wp.exe");
        var kept = AddUsage(2, "KIKAN-AP01", "2", "w3wp.exe");
        _inspector.Results["KIKAN-AP01"] = [W3wp(1, "A"), W3wp(2, "B")];
        _beforeSave.Arm($"DELETE FROM DbLinkUsages WHERE Id = {deleted}");

        await _service.RunOnceAsync(default);

        Assert.Equal("IIS: B", GetUsage(kept).AppName);
    }

    [Fact]
    public async Task 一回に処理する件数はセッションとDBLink利用元の合計で上限まで()
    {
        _service.BatchSize = 1;
        AddSession(1, "WEB01", "1");
        AddUsage(1, "WEB01", "2", "w3wp.exe");

        await _service.RunOnceAsync(default);

        Assert.Equal([1], _inspector.Calls.Single().Pids);
    }

    [Fact]
    public async Task 同じ端末のセッションとDBLink利用元はまとめて問い合わせる()
    {
        AddSession(1, "WEB01", "1");
        AddUsage(1, "web01", "2", "w3wp.exe");

        await _service.RunOnceAsync(default);

        Assert.Equal([1, 2], _inspector.Calls.Single().Pids);
    }

    [Fact]
    public async Task しばらく観測されていないDBLink利用元は調べない()
    {
        var old = AddUsage(1, "KIKAN-AP01", "5120", "w3wp.exe", lastSeen: DateTime.Now.AddHours(-1));
        var oldFailed = AddUsage(2, "KIKAN-AP01", "5121", "w3wp.exe", lastSeen: DateTime.Now.AddHours(-1), state: ProcInfoStates.Failed);

        await _service.RunOnceAsync(default);

        Assert.Empty(_inspector.Calls);
        var u = GetUsage(old);
        Assert.Equal(ProcInfoStates.NotApplicable, u.ProcInfoState);
        Assert.Contains("終了", u.ProcInfoError);
        Assert.Equal(ProcInfoStates.NotApplicable, GetUsage(oldFailed).ProcInfoState);
        Assert.Contains("終了", GetUsage(oldFailed).ProcInfoError);
    }

    [Fact]
    public async Task 設定変更で対象外にした接続中のセッションを調べ直す()
    {
        var skipped = AddSession(1, "WEB01", "1", state: ProcInfoStates.NotApplicable);
        var resolved = AddSession(2, "WEB01", "2", state: ProcInfoStates.Resolved);
        var disconnected = AddSession(3, "WEB01", "3", connected: false, state: ProcInfoStates.NotApplicable);
        var internalKind = AddSession(4, "DB01", "4", program: "ORACLE.EXE", kind: ProcessKinds.DbLinkSuspect, state: ProcInfoStates.NotApplicable);

        using (var db = _factory.CreateDbContext())
            await ProcessInfoService.RequeueSkippedAsync(db, default);

        Assert.Equal(ProcInfoStates.Pending, Get(skipped).ProcInfoState);
        Assert.Null(Get(skipped).ProcInfoError);
        Assert.Equal(ProcInfoStates.Resolved, Get(resolved).ProcInfoState);
        Assert.Equal(ProcInfoStates.NotApplicable, Get(disconnected).ProcInfoState);
        Assert.Equal(ProcInfoStates.NotApplicable, Get(internalKind).ProcInfoState);
    }

    private sealed class FakeInspector : IProcessInspector
    {
        public Dictionary<string, List<ClientProcessInfo>> Results { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Failures { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Exception> Unexpected { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<(string Host, int[] Pids, ProcessInspectorOptions Options)> Calls { get; } = [];
        /// <summary>キャンセルを無視して応答しない端末（DCOM の接続待ちなど）</summary>
        public HashSet<string> Hang { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>問い合わせ中に実行する処理（設定の同時変更の再現用）</summary>
        public Action? OnInspect { get; set; }

        public Task<IReadOnlyList<ClientProcessInfo>> InspectAsync(string host, IReadOnlyCollection<int> pids, ProcessInspectorOptions options, CancellationToken ct)
        {
            lock (Calls) Calls.Add((host, pids.Order().ToArray(), options));
            OnInspect?.Invoke();
            if (Hang.Contains(host)) return new TaskCompletionSource<IReadOnlyList<ClientProcessInfo>>().Task;
            if (Failures.TryGetValue(host, out var msg)) throw new ProcessInspectionException(msg);
            if (Unexpected.TryGetValue(host, out var ex)) throw ex;
            return Task.FromResult<IReadOnlyList<ClientProcessInfo>>(Results.TryGetValue(host, out var r) ? r : []);
        }

        public Task<string> TestAsync(string host, ProcessInspectorOptions options, CancellationToken ct) => Task.FromResult("ok");
    }
}
