using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OracleSessionHistory.Core.Entities;

namespace OracleSessionHistory.Core.Data;

/// <summary>履歴 DB の種類（設定 AppDb:Provider の値）</summary>
public static class AppDbProviders
{
    public const string Sqlite = "Sqlite";
    public const string SqlServer = "SqlServer";

    public static readonly string[] All = [Sqlite, SqlServer];
}

/// <summary>アプリケーション DB の登録・初期化</summary>
public static class AppDatabase
{
    public const string ConnectionStringName = "AppDb";

    /// <summary>DB の種類を指定する設定キー（既定は Sqlite）</summary>
    public const string ProviderKey = "AppDb:Provider";

    /// <summary>
    /// AppDbContext を DI に登録する。
    /// AppDb:Provider で SQLite / SQL Server を切り替える。Batch と Server は同じ DB を参照する必要がある。
    /// </summary>
    public static IServiceCollection AddAppDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        var provider = ResolveProvider(configuration);
        var cs = ResolveConnectionString(configuration, provider);
        services.AddDbContextFactory<AppDbContext>(o => Configure(o, provider, cs));
        services.AddSingleton<AppDatabaseReadiness>();
        return services;
    }

    /// <summary>プロバイダーに応じて DbContext を設定する</summary>
    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder options, string provider, string connectionString) =>
        provider switch
        {
            // 失敗時のリトライは行わない（Batch は次回の取得で再実行する。明示的なトランザクションとも両立させるため）
            AppDbProviders.SqlServer => options.UseSqlServer(connectionString, o => o.MigrationsAssembly(SqlServerMigrationsAssembly)),
            _ => options.UseSqlite(connectionString, o => o.MigrationsAssembly(SqliteMigrationsAssembly)),
        };

    /// <summary>プロバイダーごとのマイグレーションを置くアセンブリ（Batch・Server から参照する）</summary>
    public const string SqliteMigrationsAssembly = "OracleSessionHistory.Migrations.Sqlite";

    public const string SqlServerMigrationsAssembly = "OracleSessionHistory.Migrations.SqlServer";

    /// <summary>
    /// マイグレーション導入前（EnsureCreated で作成）のスキーマに相当する最初のマイグレーション。
    /// 両プロバイダーで同じ名前にする
    /// </summary>
    public const string InitialMigrationName = "Initial";

    /// <summary>設定から DB の種類を決める。大文字小文字は区別しない</summary>
    public static string ResolveProvider(IConfiguration configuration)
    {
        var value = configuration[ProviderKey];
        if (string.IsNullOrWhiteSpace(value)) return AppDbProviders.Sqlite;
        return AppDbProviders.All.FirstOrDefault(p => string.Equals(p, value.Trim(), StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException(
                   $"{ProviderKey} の値 '{value}' は不正です。{string.Join(" / ", AppDbProviders.All)} のいずれかを指定してください。");
    }

    /// <summary>
    /// 接続文字列を決める。
    /// SQLite で接続文字列 "AppDb" が無い場合はローカルアプリケーションデータ配下
    /// （Windows: %LOCALAPPDATA%\OracleSessionHistory）のファイルを使う。SQL Server は指定必須。
    /// </summary>
    public static string ResolveConnectionString(IConfiguration configuration, string provider)
    {
        var cs = configuration.GetConnectionString(ConnectionStringName);
        if (provider == AppDbProviders.SqlServer)
        {
            if (string.IsNullOrWhiteSpace(cs))
                throw new InvalidOperationException($"{ProviderKey}={AppDbProviders.SqlServer} の場合は ConnectionStrings:{ConnectionStringName} を指定してください。");
            return cs;
        }

        if (string.IsNullOrWhiteSpace(cs))
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OracleSessionHistory");
            cs = $"Data Source={Path.Combine(dir, "oraclesessionhistory.db")}";
        }

        // フォルダが無ければ作成する
        var builder = new SqliteConnectionStringBuilder(cs);
        // 相対パスはカレントディレクトリ基準の絶対パスにする（ログで実際のファイルが分かるように）
        if (builder.DataSource != ":memory:") builder.DataSource = Path.GetFullPath(builder.DataSource);
        var dataDir = Path.GetDirectoryName(builder.DataSource);
        if (!string.IsNullOrEmpty(dataDir)) Directory.CreateDirectory(dataDir);
        builder.DefaultTimeout = Math.Max(builder.DefaultTimeout, 30);
        return builder.ToString();
    }

    /// <summary>マイグレーションの SQL 1 文あたりのタイムアウト</summary>
    private static readonly TimeSpan MigrationCommandTimeout = TimeSpan.FromMinutes(30);

    /// <summary>マイグレーション導入前から存在するテーブル</summary>
    private static readonly string[] LegacyTables = ["Targets", "Sessions", "LinkSources", "DbLinkUsages"];

    /// <summary>
    /// 未適用のマイグレーションを適用する。データベースが無ければ作成する。
    /// マイグレーション導入前に EnsureCreated で作成した DB は、最初のマイグレーションを適用済みとして引き継ぐ。
    /// </summary>
    public static async Task MigrateAsync(AppDbContext db, ILogger logger, CancellationToken ct = default)
    {
        await AdoptLegacySchemaAsync(db, logger, ct);

        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        if (pending.Count > 0)
            logger.LogInformation("アプリケーション DB のマイグレーションを適用します: {Migrations}", string.Join(", ", pending));
        // 履歴が多い DB では列追加・データ更新・インデックス作成が既定のコマンドタイムアウト（30 秒）を超えることがある
        var timeout = db.Database.GetCommandTimeout();
        db.Database.SetCommandTimeout(MigrationCommandTimeout);
        try
        {
            await db.Database.MigrateAsync(ct);
        }
        finally
        {
            db.Database.SetCommandTimeout(timeout);
        }
    }

    /// <summary>EnsureCreated で作成された（履歴テーブルが無く、テーブルはある）DB に、最初のマイグレーションの適用記録を追加する</summary>
    private static async Task AdoptLegacySchemaAsync(AppDbContext db, ILogger logger, CancellationToken ct)
    {
        var creator = db.GetService<IRelationalDatabaseCreator>();
        if (!await creator.ExistsAsync(ct)) return;
        var history = db.GetService<IHistoryRepository>();
        if (await history.ExistsAsync(ct)) return;

        var existing = new List<string>();
        foreach (var table in LegacyTables)
            if (await TableExistsAsync(db, table, ct)) existing.Add(table);
        if (existing.Count == 0) return;
        if (existing.Count != LegacyTables.Length)
            throw new InvalidOperationException(
                $"アプリケーション DB に一部のテーブル（{string.Join(", ", existing)}）だけが存在し、マイグレーション履歴がありません。専用のデータベースを指定してください。");

        var initial = db.Database.GetMigrations().First(m => m.EndsWith("_" + InitialMigrationName, StringComparison.Ordinal));
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync(history.GetCreateIfNotExistsScript(), ct);
        await db.Database.ExecuteSqlRawAsync(history.GetInsertScript(new HistoryRow(initial, ProductInfo.GetVersion())), ct);
        await tx.CommitAsync(ct);
        logger.LogInformation("マイグレーション導入前の DB を引き継ぎました（{Migration} を適用済みとして記録）", initial);
    }

    private static async Task<bool> TableExistsAsync(AppDbContext db, string table, CancellationToken ct)
    {
        var count = db.Database.IsSqlite()
            ? await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS \"Value\" FROM sqlite_master WHERE type = 'table' AND name = {table}").SingleAsync(ct)
            : await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = SCHEMA_NAME() AND TABLE_NAME = {table}").SingleAsync(ct);
        return count > 0;
    }

    /// <summary>
    /// マイグレーションでスキーマを作成・更新する。SQLite の場合は WAL モードを有効にする（Batch と Server の同時アクセスのため）。
    /// 両プロセスが同時に起動しても失敗しないようにリトライする。
    /// </summary>
    public static async Task InitializeAsync(IServiceProvider sp, ILogger logger, bool seedDemo, CancellationToken ct = default)
    {
        var factory = sp.GetRequiredService<IDbContextFactory<AppDbContext>>();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var db = await factory.CreateDbContextAsync(ct);
                await MigrateAsync(db, logger, ct);
                if (db.Database.IsSqlite())
                    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);

                if (seedDemo && !await db.Targets.AnyAsync(ct))
                {
                    var now = DateTime.Now;
                    var demo = new MonitorTarget
                    {
                        Name = "デモDB",
                        Provider = SourceProviders.Demo,
                        ConnectionString = "(demo)",
                        Description = "Oracle 無しで動作確認するための疑似データ",
                        CreatedAt = now,
                        UpdatedAt = now,
                    };
                    db.Targets.Add(demo);
                    await db.SaveChangesAsync(ct);
                    // 接続数表を確認できるよう過去 2 ヶ月分の履歴も作る
                    db.Sessions.AddRange(Collection.Demo.DemoHistory.Generate(demo.Id, now));
                    await db.SaveChangesAsync(ct);
                    // デモではプロセス情報の取得も有効にしておく（Demo の監視対象は疑似データを返す）
                    if (!await db.ProcessInspectionSettings.AnyAsync(ct))
                    {
                        db.ProcessInspectionSettings.Add(new ProcessInspectionSettings
                        {
                            Id = ProcessInspectionSettings.SingletonId, Enabled = true, ExcludeMachines = "GUEST-*", UpdatedAt = now,
                        });
                        await db.SaveChangesAsync(ct);
                    }
                    logger.LogInformation("デモ用の監視対象を登録しました");
                }
                sp.GetService<AppDatabaseReadiness>()?.MarkReady();
                return;
            }
            catch (Exception ex) when (attempt < 10 && !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "アプリケーション DB の初期化に失敗しました。リトライします ({Attempt})", attempt);
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // 起動できない原因をログファイルに残してから終了する
                logger.LogCritical(ex, "アプリケーション DB を初期化できないため終了します");
                throw;
            }
        }
    }
}
