using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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
        return services;
    }

    /// <summary>プロバイダーに応じて DbContext を設定する</summary>
    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder options, string provider, string connectionString) =>
        provider switch
        {
            // 失敗時のリトライは行わない（Batch は次回の取得で再実行する。明示的なトランザクションとも両立させるため）
            AppDbProviders.SqlServer => options.UseSqlServer(connectionString),
            _ => options.UseSqlite(connectionString),
        };

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

    /// <summary>
    /// スキーマを作成する。SQLite の場合は WAL モードを有効にする（Batch と Server の同時アクセスのため）。
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
                await db.Database.EnsureCreatedAsync(ct);
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
                    logger.LogInformation("デモ用の監視対象を登録しました");
                }
                return;
            }
            catch (Exception ex) when (attempt < 10 && !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "アプリケーション DB の初期化に失敗しました。リトライします ({Attempt})", attempt);
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
    }
}
