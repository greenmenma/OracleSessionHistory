using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using OracleSessionHistory.Core.Data;
using OracleSessionHistory.Core.Entities;

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
    public async Task このシステム自身の接続を種別Selfにし既存のDBLink利用元は取得対象にしない()
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
            "(1, 1, '(PROCESS)', 'PROCESS', 1, 2, 1, '2026-11-01 09:00:00', NULL, '2026-11-01 09:00:00', '2026-11-01 09:00:00', 1)");

        await migrator.MigrateAsync();

        var sessions = await db.Sessions.OrderBy(s => s.Sid).ToListAsync();
        Assert.Equal([ProcessKinds.Self, ProcessKinds.Self, ProcessKinds.Client], sessions.Select(s => s.ProcessKind));
        Assert.Equal([ProcInfoStates.NotApplicable, ProcInfoStates.NotApplicable, ProcInfoStates.Pending], sessions.Select(s => s.ProcInfoState));
        var usages = await db.DbLinkUsages.OrderBy(u => u.Sid).ToListAsync();
        Assert.Equal(["受注入力.exe", "(不明)"], usages.Select(u => u.AppName));
        Assert.All(usages, u => Assert.Equal(ProcInfoStates.NotApplicable, u.ProcInfoState));
    }

    /// <summary>マイグレーション導入前（EnsureCreated）の DB を再現する：Initial のスキーマを作り、履歴テーブルを消す</summary>
    private static async Task CreateLegacySchemaAsync(AppDbContext db)
    {
        var initial = db.Database.GetMigrations().First(m => m.EndsWith("_" + AppDatabase.InitialMigrationName));
        await db.GetService<IMigrator>().MigrateAsync(initial);
        await db.Database.ExecuteSqlRawAsync("DROP TABLE __EFMigrationsHistory");
    }
}
