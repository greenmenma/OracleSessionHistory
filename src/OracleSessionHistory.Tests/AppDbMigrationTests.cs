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
        // マイグレーション導入前の状態（履歴テーブル無し）
        await using (var legacy = new AppDbContext(_sqliteOptions))
        {
            await legacy.Database.EnsureCreatedAsync();
            legacy.Targets.Add(new MonitorTarget { Name = "既存", ConnectionString = "x" });
            await legacy.SaveChangesAsync();
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
}
