using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using OracleSessionHistory.Core.Data;
using OracleSessionHistory.Core.Entities;
using OracleSessionHistory.Core.Processes;

namespace OracleSessionHistory.Tests;

/// <summary>履歴 DB のマイグレーションの検証</summary>
public sealed class AppDbMigrationTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly DbContextOptions<AppDbContext> _sqliteOptions;

    public AppDbMigrationTests()
    {
        _conn.Open();
        _sqliteOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_conn, o => o.MigrationsAssembly(AppDatabase.SqliteMigrationsAssembly)).Options;
    }

    public void Dispose() => _conn.Dispose();

    private static AppDbContext Context(string provider, string connectionString) =>
        new((DbContextOptions<AppDbContext>)AppDatabase.Configure(new DbContextOptionsBuilder<AppDbContext>(), provider, connectionString).Options);

    /// <summary>接続はしない（モデルとマイグレーションの比較のみ）</summary>
    public static TheoryData<string, string> Providers => new()
    {
        { AppDbProviders.Sqlite, "Data Source=:memory:" },
        { AppDbProviders.SqlServer, "Server=localhost;Database=OracleSessionHistory;Integrated Security=true;TrustServerCertificate=true" },
    };

    [Theory]
    [MemberData(nameof(Providers))]
    public void エンティティの変更がマイグレーションに反映されている(string provider, string cs)
    {
        using var db = Context(provider, cs);
        Assert.False(db.Database.HasPendingModelChanges(),
            $"{provider} のマイグレーションがモデルと一致しません。docs/development.md の手順で dotnet ef migrations add を実行してください。");
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void 最初のマイグレーションは両プロバイダーで同じ名前(string provider, string cs)
    {
        using var db = Context(provider, cs);
        Assert.EndsWith("_" + AppDatabase.InitialMigrationName, db.Database.GetMigrations().First());
    }

    [Fact]
    public void SqlServerのマイグレーションスクリプトを生成できる()
    {
        using var db = Context(AppDbProviders.SqlServer, "Server=localhost;Database=OracleSessionHistory;Integrated Security=true;TrustServerCertificate=true");
        var script = db.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        Assert.Contains("CREATE TABLE [Sessions]", script);
    }

    [Fact]
    public async Task 新しいDBにすべてのマイグレーションを適用する()
    {
        await using var db = new AppDbContext(_sqliteOptions);
        await AppDatabase.MigrateAsync(db, NullLogger.Instance);

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(await db.Sessions.AnyAsync());
    }

    [Fact]
    public async Task EnsureCreatedで作成したDBはデータを残して引き継ぐ()
    {
        // マイグレーション導入前の状態（Initial 時点のスキーマ・履歴テーブル無し）
        await using (var legacy = new AppDbContext(_sqliteOptions))
        {
            await CreateLegacySchemaAsync(legacy);
            await legacy.Database.ExecuteSqlRawAsync(
                "INSERT INTO Targets (Name, Provider, ConnectionString, IntervalSeconds, LinkIntervalSeconds, IncludeBackground, CollectSqlText, Enabled, CreatedAt, UpdatedAt, ConsecutiveFailures) " +
                "VALUES ('既存', 'Oracle', 'x', 5, 30, 0, 1, 1, '2026-11-01 00:00:00', '2026-11-01 00:00:00', 0)");
        }

        await using var db = new AppDbContext(_sqliteOptions);
        await AppDatabase.MigrateAsync(db, NullLogger.Instance);
        // 2 回目は何もしない
        await AppDatabase.MigrateAsync(db, NullLogger.Instance);

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Contains(await db.Database.GetAppliedMigrationsAsync(), m => m.EndsWith("_" + AppDatabase.InitialMigrationName));
        Assert.Equal("既存", (await db.Targets.SingleAsync()).Name);
    }

    [Fact]
    public async Task 一部のテーブルだけ存在するDBはエラー()
    {
        await using var db = new AppDbContext(_sqliteOptions);
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE Targets (Id INTEGER PRIMARY KEY)");

        await Assert.ThrowsAsync<InvalidOperationException>(() => AppDatabase.MigrateAsync(db, NullLogger.Instance));
    }
    [Fact]
    public async Task 既存のセッションはアプリ名をPROGRAMで埋め接続中のクライアントだけ取得対象にする()
    {
        await using var db = new AppDbContext(_sqliteOptions);
        var migrator = db.GetService<IMigrator>();
        var all = db.Database.GetMigrations().ToList();
        var before = all[all.FindIndex(m => m.EndsWith("_AddProcessInfo")) - 1];
        await migrator.MigrateAsync(before);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Targets (Id, Name, Provider, ConnectionString, IntervalSeconds, LinkIntervalSeconds, IncludeBackground, CollectSqlText, Enabled, CreatedAt, UpdatedAt, ConsecutiveFailures) " +
            "VALUES (1, 'T', 'Oracle', 'x', 5, 30, 0, 1, 1, '2026-11-01 00:00:00', '2026-11-01 00:00:00', 0)");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Sessions (TargetId, InstId, Sid, Serial, LogonTime, Program, ProcessKind, FirstSeenAt, LastSeenAt, IsConnected, DisconnectUncertain) VALUES " +
            "(1, 1, 1, 1, '2026-11-01 09:00:00', 'w3wp.exe', 'Client', '2026-11-01 09:00:00', '2026-11-01 09:00:00', 0, 0), " +
            "(1, 1, 2, 1, '2026-11-01 09:00:00', NULL, 'Client', '2026-11-01 09:00:00', '2026-11-01 09:00:00', 0, 0), " +
            "(1, 1, 3, 1, '2026-11-01 09:00:00', 'w3wp.exe', 'Client', '2026-11-01 09:00:00', '2026-11-01 09:00:00', 1, 0), " +
            "(1, 1, 4, 1, '2026-11-01 09:00:00', 'ORACLE.EXE', 'DbLinkSuspect', '2026-11-01 09:00:00', '2026-11-01 09:00:00', 1, 0)");

        await migrator.MigrateAsync();

        var rows = await db.Sessions.OrderBy(s => s.Sid).ToListAsync();
        Assert.Equal(["w3wp.exe", "(不明)", "w3wp.exe", "ORACLE.EXE"], rows.Select(r => r.AppName));
        // 接続中のクライアント（長時間つながる接続プール等）は移行後にプロセス情報を取得する
        Assert.Equal([ProcInfoStates.NotApplicable, ProcInfoStates.NotApplicable, ProcInfoStates.Pending, ProcInfoStates.NotApplicable],
            rows.Select(r => r.ProcInfoState));
    }

    [Fact]
    public async Task このシステム自身の接続を種別Selfにし既存のDBLink利用元は取得待ちにする()
    {
        await using var db = new AppDbContext(_sqliteOptions);
        var migrator = db.GetService<IMigrator>();
        var all = db.Database.GetMigrations().ToList();
        var before = all[all.FindIndex(m => m.EndsWith("_AddLinkOriginAndTargetPrograms")) - 1];
        await migrator.MigrateAsync(before);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Targets (Id, Name, Provider, ConnectionString, IntervalSeconds, LinkIntervalSeconds, IncludeBackground, CollectSqlText, Enabled, CreatedAt, UpdatedAt, ConsecutiveFailures) " +
            "VALUES (1, 'T', 'Oracle', 'x', 5, 30, 0, 1, 1, '2026-11-01 00:00:00', '2026-11-01 00:00:00', 0)");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Sessions (TargetId, InstId, Sid, Serial, LogonTime, Program, Module, ProcessKind, AppName, ProcInfoState, FirstSeenAt, LastSeenAt, IsConnected, DisconnectUncertain) VALUES " +
            "(1, 1, 1, 1, '2026-11-01 09:00:00', 'w3wp.exe', 'OracleSessionHistory', 'Client', 'w3wp.exe', 'Pending', '2026-11-01 09:00:00', '2026-11-01 09:00:00', 1, 0), " +
            "(1, 1, 2, 1, '2026-11-01 09:00:00', 'OracleSessionHistory.Batch.exe', NULL, 'Client', 'x', 'Pending', '2026-11-01 09:00:00', '2026-11-01 09:00:00', 1, 0), " +
            "(1, 1, 3, 1, '2026-11-01 09:00:00', 'w3wp.exe', '受注', 'Client', 'w3wp.exe', 'Pending', '2026-11-01 09:00:00', '2026-11-01 09:00:00', 1, 0)");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO LinkSources (Id, TargetId, Name, Provider, ConnectionString, Enabled, SelectedLinks, CreatedAt, UpdatedAt) " +
            "VALUES (1, 1, 'L', 'Oracle', 'y', 1, '[]', '2026-11-01 00:00:00', '2026-11-01 00:00:00')");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO DbLinkUsages (LinkSourceId, TargetId, DbLinkName, MatchType, InstId, Sid, Serial, LogonTime, Program, FirstSeenAt, LastSeenAt, HitCount) VALUES " +
            "(1, 1, '(PROCESS)', 'PROCESS', 1, 1, 1, '2026-11-01 09:00:00', '受注入力.exe', '2026-11-01 09:00:00', '2026-11-01 09:00:00', 1), " +
            "(1, 1, '(PROCESS)', 'PROCESS', 1, 2, 1, '2026-11-01 09:00:00', NULL, '2026-11-01 09:00:00', '2026-11-01 09:00:00', 1), " +
            "(1, 1, '(PROCESS)', 'PROCESS', 1, 3, 1, '2026-11-01 09:00:00', ' ', '2026-11-01 09:00:00', '2026-11-01 09:00:00', 1)");

        // 後のマイグレーション（w3wp.exe・sqlplus.exe 以外を対象外にする）の前まで適用する
        await migrator.MigrateAsync(all[all.FindIndex(m => m.EndsWith("_SkipProcessInfoExceptIisAndSqlPlus")) - 1]);

        var sessions = await db.Sessions.OrderBy(s => s.Sid).ToListAsync();
        Assert.Equal([ProcessKinds.Self, ProcessKinds.Self, ProcessKinds.Client], sessions.Select(s => s.ProcessKind));
        Assert.Equal([ProcInfoStates.NotApplicable, ProcInfoStates.NotApplicable, ProcInfoStates.Pending], sessions.Select(s => s.ProcInfoState));
        var usages = await db.DbLinkUsages.OrderBy(u => u.Sid).ToListAsync();
        Assert.Equal(["受注入力.exe", "(不明)", "(不明)"], usages.Select(u => u.AppName));
        // 移行時点で使われ続けている利用元も調べる（観測されなくなったものは ProcessInfoService が理由を付けて終える）
        Assert.All(usages, u => Assert.Equal(ProcInfoStates.Pending, u.ProcInfoState));
    }

    public static TheoryData<string?, string?, string?> NormalizeCases => new()
    {
        { "w3wp.exe", null, @"IIS APPPOOL\Koukai" },
        { " W3WP.EXE ", "w3wp.exe", @" iis apppool\Juchu " },
        { "w3wp.exe", null, @"IIS APPPOOL\" },
        { "w3wp.exe", "受注", "CORP\\yamada" },
        { "販売管理.exe", "販売管理", "yamada" },
        { "販売管理.exe", "販売管理.EXE", "yamada" },
        { "sqlplus.exe", "  ", "dev" },
        { null, "SQL*Plus", null },
        { "  ", null, null },
    };

    /// <summary>SQL のリテラル（NULL を含むため、パラメータではなく値を埋め込む）</summary>
    private static string Lit(string? v) => v is null ? "NULL" : "'" + v.Replace("'", "''") + "'";

    [Theory]
    [MemberData(nameof(NormalizeCases))]
    public async Task 既存のアプリ名を新しい行と同じ規則にそろえる(string? program, string? module, string? osUser)
    {
        await using var db = new AppDbContext(_sqliteOptions);
        var migrator = db.GetService<IMigrator>();
        var all = db.Database.GetMigrations().ToList();
        await migrator.MigrateAsync(all[all.FindIndex(m => m.EndsWith("_NormalizeAppNames")) - 1]);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Targets (Id, Name, Provider, ConnectionString, IntervalSeconds, LinkIntervalSeconds, IncludeBackground, CollectSqlText, Enabled, CreatedAt, UpdatedAt, ConsecutiveFailures) " +
            "VALUES (1, 'T', 'Oracle', 'x', 5, 30, 0, 1, 1, '2026-11-01 00:00:00', '2026-11-01 00:00:00', 0)");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO LinkSources (Id, TargetId, Name, Provider, ConnectionString, Enabled, SelectedLinks, CreatedAt, UpdatedAt) " +
            "VALUES (1, 1, 'L', 'Oracle', 'y', 1, '[]', '2026-11-01 00:00:00', '2026-11-01 00:00:00')");
        // 埋め込むのはテストで決めた値だけ（NULL を含むためパラメータにできない）
#pragma warning disable EF1003
        foreach (var (sid, state) in new[] { (1, ProcInfoStates.NotApplicable), (2, ProcInfoStates.Resolved) })
        {
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Sessions (TargetId, InstId, Sid, Serial, LogonTime, Program, Module, OsUser, ProcessKind, AppName, ProcInfoState, FirstSeenAt, LastSeenAt, IsConnected, DisconnectUncertain) " +
                $"VALUES (1, 1, {sid}, 1, '2026-11-01 09:00:00', {Lit(program)}, {Lit(module)}, {Lit(osUser)}, 'Client', 'old', {Lit(state)}, '2026-11-01 09:00:00', '2026-11-01 09:00:00', 0, 0)");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO DbLinkUsages (LinkSourceId, TargetId, DbLinkName, MatchType, InstId, Sid, Serial, LogonTime, Program, Module, OsUser, AppName, ProcInfoState, FirstSeenAt, LastSeenAt, HitCount) " +
                $"VALUES (1, 1, '(PROCESS)', 'PROCESS', 1, {sid}, 1, '2026-11-01 09:00:00', {Lit(program)}, {Lit(module)}, {Lit(osUser)}, 'old', {Lit(state)}, '2026-11-01 09:00:00', '2026-11-01 09:00:00', 1)");
        }

#pragma warning restore EF1003
        await migrator.MigrateAsync();

        var expected = AppIdentifier.Identify(null, program, module, osUser);
        var sessions = await db.Sessions.OrderBy(x => x.Sid).ToListAsync();
        Assert.Equal(expected, (sessions[0].AppName, sessions[0].AppDetail));
        Assert.Equal("old", sessions[1].AppName); // プロセス情報で確定した行は変えない
        var usages = await db.DbLinkUsages.OrderBy(x => x.Sid).ToListAsync();
        Assert.Equal(expected, (usages[0].AppName, usages[0].AppDetail));
        Assert.Equal("old", usages[1].AppName);
    }

    [Fact]
    public async Task 登録外で対象外にした行を調べ直し端末名とsqlplusのコマンドラインとDBLink経由の接続元を整える()
    {
        await using var db = new AppDbContext(_sqliteOptions);
        var migrator = db.GetService<IMigrator>();
        var all = db.Database.GetMigrations().ToList();
        await migrator.MigrateAsync(all[all.FindIndex(m => m.EndsWith("_AddAncestryAndLinkOrigin")) - 1]);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Targets (Id, Name, Provider, ConnectionString, IntervalSeconds, LinkIntervalSeconds, IncludeBackground, CollectSqlText, Enabled, CreatedAt, UpdatedAt, ConsecutiveFailures) " +
            "VALUES (1, 'T', 'Oracle', 'x', 5, 30, 0, 1, 1, '2026-11-01 00:00:00', '2026-11-01 00:00:00', 0)");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Sessions (Id, TargetId, InstId, Sid, Serial, LogonTime, Machine, Program, ProcessKind, AppName, ProcInfoState, ProcInfoError, ProcName, ProcCommandLine, FirstSeenAt, LastSeenAt, IsConnected, DisconnectUncertain) VALUES " +
            "(1, 1, 1, 1, 1, '2026-11-01 09:00:00', ' WEB01 ', 'foo.exe', 'Client', 'foo.exe', 'NotApplicable', 'プロセス情報を取得する実行ファイルに登録されていません', NULL, NULL, '2026-11-01 09:00:00', '2026-11-01 09:00:00', 1, 0), " +
            "(2, 1, 1, 2, 1, '2026-11-01 09:00:00', 'BATCH01', 'sqlplus.exe', 'Client', 'sqlplus.exe', 'Resolved', NULL, 'sqlplus.exe', 'sqlplus scott/tiger@db', '2026-11-01 09:00:00', '2026-11-01 09:00:00', 1, 0), " +
            "(3, 1, 1, 3, 1, '2026-11-01 09:00:00', 'KIKAN-DB01', 'ORACLE.EXE', 'DbLinkSuspect', 'ORACLE.EXE', 'NotApplicable', NULL, NULL, NULL, '2026-11-01 09:00:00', '2026-11-01 09:00:00', 1, 0), " +
            "(4, 1, 1, 4, 1, '2026-11-01 09:00:00', 'PC01', 'foo.exe', 'Client', 'foo.exe', 'NotApplicable', 'プロセス情報を取得する実行ファイルに登録されていません', NULL, NULL, '2026-11-01 09:00:00', '2026-11-01 09:00:00', 0, 0)");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO LinkSources (Id, TargetId, Name, Provider, ConnectionString, Enabled, SelectedLinks, CreatedAt, UpdatedAt) " +
            "VALUES (1, 1, 'L', 'Oracle', 'y', 1, '[]', '2026-11-01 00:00:00', '2026-11-01 00:00:00')");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO DbLinkUsages (Id, LinkSourceId, TargetId, DbLinkName, MatchType, InstId, Sid, Serial, LogonTime, Machine, AppName, ProcInfoState, MatchedSessionId, FirstSeenAt, LastSeenAt, HitCount) VALUES " +
            "(1, 1, 1, '(PROCESS)', 'PROCESS', 1, 1, 1, '2026-11-01 09:00:00', 'OLD01', '古い.exe', 'Resolved', 3, '2026-11-01 09:00:00', '2026-11-01 09:00:00', 1), " +
            "(2, 1, 1, '(PROCESS)', 'PROCESS', 1, 2, 1, '2026-11-01 09:00:00', 'KIKAN-AP01 ', 'IIS: Juchu', 'Resolved', 3, '2026-11-01 09:00:00', '2026-11-01 10:00:00', 1)");

        // 後のマイグレーション（w3wp.exe・sqlplus.exe 以外を対象外にする）の影響を受けないよう、このマイグレーションまで適用する
        await migrator.MigrateAsync(all.Single(m => m.EndsWith("_AddAncestryAndLinkOrigin")));

        var s = await db.Sessions.OrderBy(x => x.Id).ToListAsync();
        Assert.Equal((ProcInfoStates.Pending, (string?)null, "WEB01"), (s[0].ProcInfoState, s[0].ProcInfoError, s[0].Machine));
        Assert.Equal(ProcInfoStates.NotApplicable, s[3].ProcInfoState); // 切断済みはそのまま
        Assert.Null(s[1].ProcCommandLine);
        Assert.Equal(ProcInfoStates.Pending, s[1].ProcInfoState);
        Assert.Equal(("KIKAN-AP01", "IIS: Juchu"), (s[2].LinkOriginMachine, s[2].LinkOriginApp));
        Assert.Null(s[0].LinkOriginApp);
    }

    [Fact]
    public async Task w3wpとsqlplus以外の取得失敗などを理由なしの対象外にする()
    {
        await using var db = new AppDbContext(_sqliteOptions);
        var migrator = db.GetService<IMigrator>();
        var all = db.Database.GetMigrations().ToList();
        await migrator.MigrateAsync(all[all.FindIndex(m => m.EndsWith("_SkipProcessInfoExceptIisAndSqlPlus")) - 1]);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Targets (Id, Name, Provider, ConnectionString, IntervalSeconds, LinkIntervalSeconds, IncludeBackground, CollectSqlText, Enabled, CreatedAt, UpdatedAt, ConsecutiveFailures) " +
            "VALUES (1, 'T', 'Oracle', 'x', 5, 30, 0, 1, 1, '2026-11-01 00:00:00', '2026-11-01 00:00:00', 0)");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Sessions (Id, TargetId, InstId, Sid, Serial, LogonTime, Program, ProcessKind, AppName, ProcInfoState, ProcInfoError, ProcInfoAt, FirstSeenAt, LastSeenAt, IsConnected, DisconnectUncertain) VALUES " +
            "(1, 1, 1, 1, 1, '2026-11-01 09:00:00', 'foo.exe', 'Client', 'foo.exe', 'Failed', 'タイムアウトしました', '2026-11-01 09:01:00', '2026-11-01 09:00:00', '2026-11-01 09:00:00', 1, 0), " +
            "(2, 1, 1, 2, 1, '2026-11-01 09:00:00', 'foo.exe', 'Client', 'foo.exe', 'NotApplicable', '切断までに取得できませんでした', '2026-11-01 09:01:00', '2026-11-01 09:00:00', '2026-11-01 09:00:00', 0, 0), " +
            "(3, 1, 1, 3, 1, '2026-11-01 09:00:00', NULL, 'Client', '(不明)', 'Pending', NULL, NULL, '2026-11-01 09:00:00', '2026-11-01 09:00:00', 1, 0), " +
            "(4, 1, 1, 4, 1, '2026-11-01 09:00:00', 'dotnet.exe', 'Client', 'Api.dll', 'Resolved', NULL, '2026-11-01 09:01:00', '2026-11-01 09:00:00', '2026-11-01 09:00:00', 1, 0), " +
            "(5, 1, 1, 5, 1, '2026-11-01 09:00:00', ' W3WP.EXE ', 'Client', 'w3wp.exe', 'Failed', 'タイムアウトしました', '2026-11-01 09:01:00', '2026-11-01 09:00:00', '2026-11-01 09:00:00', 1, 0), " +
            "(6, 1, 1, 6, 1, '2026-11-01 09:00:00', 'sqlplus.exe', 'Client', 'sqlplus.exe', 'NotFound', 'プロセスが見つかりません（終了済み）', '2026-11-01 09:01:00', '2026-11-01 09:00:00', '2026-11-01 09:00:00', 0, 0)");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO LinkSources (Id, TargetId, Name, Provider, ConnectionString, Enabled, SelectedLinks, CreatedAt, UpdatedAt) " +
            "VALUES (1, 1, 'L', 'Oracle', 'y', 1, '[]', '2026-11-01 00:00:00', '2026-11-01 00:00:00')");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO DbLinkUsages (Id, LinkSourceId, TargetId, DbLinkName, MatchType, InstId, Sid, Serial, LogonTime, Program, AppName, ProcInfoState, ProcInfoError, FirstSeenAt, LastSeenAt, HitCount) VALUES " +
            "(1, 1, 1, '(PROCESS)', 'PROCESS', 1, 1, 1, '2026-11-01 09:00:00', 'foo.exe', 'foo.exe', 'Mismatch', '別のプロセスの可能性があるため紐付けません', '2026-11-01 09:00:00', '2026-11-01 09:00:00', 1), " +
            "(2, 1, 1, '(PROCESS)', 'PROCESS', 1, 2, 1, '2026-11-01 09:00:00', 'sqlplus.exe', 'sqlplus.exe', 'Failed', 'タイムアウトしました', '2026-11-01 09:00:00', '2026-11-01 09:00:00', 1)");

        await migrator.MigrateAsync();

        var s = await db.Sessions.OrderBy(x => x.Id).ToListAsync();
        foreach (var i in new[] { 0, 1, 2 })
            Assert.Equal((ProcInfoStates.NotApplicable, (string?)null, (DateTime?)null), (s[i].ProcInfoState, s[i].ProcInfoError, s[i].ProcInfoAt));
        Assert.Equal((ProcInfoStates.Resolved, "Api.dll"), (s[3].ProcInfoState, s[3].AppName)); // 取得済みはそのまま
        Assert.Equal((ProcInfoStates.Failed, "タイムアウトしました"), (s[4].ProcInfoState, s[4].ProcInfoError));
        Assert.Equal(ProcInfoStates.NotFound, s[5].ProcInfoState);
        var u = await db.DbLinkUsages.OrderBy(x => x.Id).ToListAsync();
        Assert.Equal((ProcInfoStates.NotApplicable, (string?)null), (u[0].ProcInfoState, u[0].ProcInfoError));
        Assert.Equal(ProcInfoStates.Failed, u[1].ProcInfoState);
    }

    /// <summary>マイグレーション導入前（EnsureCreated）の DB を再現する：Initial のスキーマを作り、履歴テーブルを消す</summary>
    private static async Task CreateLegacySchemaAsync(AppDbContext db)
    {
        var initial = db.Database.GetMigrations().First(m => m.EndsWith("_" + AppDatabase.InitialMigrationName));
        await db.GetService<IMigrator>().MigrateAsync(initial);
        await db.Database.ExecuteSqlRawAsync("DROP TABLE __EFMigrationsHistory");
    }
}
