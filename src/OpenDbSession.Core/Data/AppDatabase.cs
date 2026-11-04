using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenDbSession.Core.Entities;

namespace OpenDbSession.Core.Data;

/// <summary>アプリケーション DB の登録・初期化</summary>
public static class AppDatabase
{
    public const string ConnectionStringName = "AppDb";

    /// <summary>
    /// AppDbContext を DI に登録する。接続文字列 "AppDb" が無い場合は
    /// ローカルアプリケーションデータ配下（Windows: %LOCALAPPDATA%\OpenDbSession）の SQLite を使う。
    /// Batch と Server は同じ DB ファイルを参照する必要がある。
    /// </summary>
    public static IServiceCollection AddAppDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        var cs = ResolveConnectionString(configuration);
        services.AddDbContextFactory<AppDbContext>(o => o.UseSqlite(cs));
        return services;
    }

    public static string ResolveConnectionString(IConfiguration configuration)
    {
        var cs = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(cs))
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenDbSession");
            cs = $"Data Source={Path.Combine(dir, "opendbsession.db")}";
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
    /// スキーマを作成し、WAL モードを有効にする（Batch と Server の同時アクセスのため）。
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
