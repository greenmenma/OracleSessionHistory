using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using OracleSessionHistory.Core.Data;
using OracleSessionHistory.Core.Entities;

namespace OracleSessionHistory.Tests;

/// <summary>履歴 DB のプロバイダー切替と、SQL Server でも作成できるスキーマかの検証</summary>
public sealed class AppDatabaseTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    private static AppDbContext SqlServerContext() =>
        // 接続はしない（モデルとスクリプト生成のみ）
        new(AppDatabase.Configure(new DbContextOptionsBuilder<AppDbContext>(), AppDbProviders.SqlServer,
            "Server=localhost;Database=OracleSessionHistory;Integrated Security=true;TrustServerCertificate=true").Options as DbContextOptions<AppDbContext>
            ?? throw new InvalidOperationException());

    private static IRelationalModel SqlServerModel(AppDbContext db) =>
        db.GetService<IDesignTimeModel>().Model.GetRelationalModel();

    [Fact]
    public void Provider未指定ならSqlite()
    {
        Assert.Equal(AppDbProviders.Sqlite, AppDatabase.ResolveProvider(Config()));
    }

    [Fact]
    public void Providerは大文字小文字を区別しない()
    {
        Assert.Equal(AppDbProviders.SqlServer, AppDatabase.ResolveProvider(Config(("AppDb:Provider", "sqlserver"))));
    }

    [Fact]
    public void 不正なProviderはエラー()
    {
        Assert.Throws<InvalidOperationException>(() => AppDatabase.ResolveProvider(Config(("AppDb:Provider", "Postgres"))));
    }

    [Fact]
    public void SqlServerは接続文字列が必須()
    {
        Assert.Throws<InvalidOperationException>(() => AppDatabase.ResolveConnectionString(Config(), AppDbProviders.SqlServer));
    }

    [Fact]
    public void SqlServerの接続文字列はそのまま使う()
    {
        const string cs = "Server=db01;Database=OracleSessionHistory;User Id=osh;Password=secret;";
        Assert.Equal(cs, AppDatabase.ResolveConnectionString(Config(("ConnectionStrings:AppDb", cs)), AppDbProviders.SqlServer));
    }

    [Fact]
    public void SqlServerでインデックス対象の列がmax型でない()
    {
        using var db = SqlServerContext();
        var bad = SqlServerModel(db).Tables
            .SelectMany(t => t.Indexes.SelectMany(i => i.Columns.Select(c => $"{t.Name}.{c.Name} ({c.StoreType})")))
            .Where(x => x.Contains("(max)", StringComparison.OrdinalIgnoreCase))
            .Distinct().ToList();
        Assert.Empty(bad);
    }

    [Fact]
    public void SqlServerで削除の連鎖経路が複数にならない()
    {
        using var db = SqlServerContext();
        // DB 側で動作する削除時アクション（CASCADE / SET NULL 等）を持つ外部キーだけを辿る
        var edges = SqlServerModel(db).Tables
            .SelectMany(t => t.ForeignKeyConstraints)
            .Where(fk => fk.OnDeleteAction != ReferentialAction.NoAction && fk.OnDeleteAction != ReferentialAction.Restrict)
            .Select(fk => (From: fk.PrincipalTable.Name, To: fk.Table.Name))
            .ToList();

        foreach (var root in edges.Select(e => e.From).Distinct())
        {
            // root から各テーブルへの経路数を数える（スキーマは循環しない前提）
            var paths = new Dictionary<string, int>();
            void Walk(string table)
            {
                foreach (var e in edges.Where(e => e.From == table))
                {
                    paths[e.To] = paths.GetValueOrDefault(e.To) + 1;
                    Walk(e.To);
                }
            }
            Walk(root);
            Assert.All(paths, p => Assert.True(p.Value == 1, $"{root} → {p.Key} の連鎖経路が {p.Value} 本あります"));
        }
    }

    [Fact]
    public void SqlServerの作成スクリプトを生成できる()
    {
        using var db = SqlServerContext();
        var script = db.Database.GenerateCreateScript();
        Assert.Contains("CREATE TABLE [Sessions]", script);
    }

    /// <summary>SQLite（外部キー有効）で、参照されているセッションを削除できることを確認する</summary>
    public sealed class Maintenance : IDisposable
    {
        private readonly SqliteConnection _conn = new("Data Source=:memory:");
        private readonly DbContextOptions<AppDbContext> _options;

        public Maintenance()
        {
            _conn.Open();
            _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options;
            using var db = new AppDbContext(_options);
            db.Database.EnsureCreated();
        }

        public void Dispose() => _conn.Dispose();

        /// <summary>監視対象 A のセッションを、監視対象 B 配下の DB Link 利用元が参照している状態を作る</summary>
        private (int TargetA, int TargetB, long SessionId, long UsageId) Seed(DateTime sessionLastSeen, DateTime usageLastSeen)
        {
            using var db = new AppDbContext(_options);
            var a = new MonitorTarget { Name = "A", ConnectionString = "x" };
            var b = new MonitorTarget { Name = "B", ConnectionString = "x" };
            db.Targets.AddRange(a, b);
            db.SaveChanges();
            var session = new TrackedSession
            {
                TargetId = a.Id, Sid = 1, Serial = 1, LogonTime = sessionLastSeen.AddHours(-1),
                FirstSeenAt = sessionLastSeen.AddHours(-1), LastSeenAt = sessionLastSeen, IsConnected = false,
            };
            var link = new LinkSource { TargetId = b.Id, Name = "元DB", ConnectionString = "x" };
            db.AddRange(session, link);
            db.SaveChanges();
            var usage = new DbLinkUsage
            {
                LinkSourceId = link.Id, TargetId = b.Id, DbLinkName = "TO_A", Sid = 9, Serial = 9,
                LogonTime = usageLastSeen.AddHours(-1), FirstSeenAt = usageLastSeen.AddHours(-1), LastSeenAt = usageLastSeen,
                MatchedSessionId = session.Id,
            };
            db.DbLinkUsages.Add(usage);
            db.SaveChanges();
            return (a.Id, b.Id, session.Id, usage.Id);
        }

        [Fact]
        public async Task 保持期間切れのセッションは参照されていても削除できる()
        {
            var now = DateTime.Now;
            var (_, _, sessionId, usageId) = Seed(sessionLastSeen: now.AddDays(-200), usageLastSeen: now);

            await using var db = new AppDbContext(_options);
            var deleted = await AppDbMaintenance.DeleteExpiredAsync(db, now.AddDays(-100), default);

            Assert.Equal(1, deleted);
            Assert.False(await db.Sessions.AnyAsync(s => s.Id == sessionId));
            var usage = await db.DbLinkUsages.SingleAsync(u => u.Id == usageId);
            Assert.Null(usage.MatchedSessionId);
        }

        [Fact]
        public async Task 監視対象を削除すると他の対象からの参照は外れて残る()
        {
            var now = DateTime.Now;
            var (targetA, _, _, usageId) = Seed(now, now);

            await using var db = new AppDbContext(_options);
            Assert.True(await AppDbMaintenance.DeleteTargetAsync(db, targetA, default));

            Assert.False(await db.Sessions.AnyAsync());
            var usage = await db.DbLinkUsages.SingleAsync(u => u.Id == usageId);
            Assert.Null(usage.MatchedSessionId);
        }

        [Fact]
        public async Task 監視対象を削除すると配下の接続元と利用元も消える()
        {
            var now = DateTime.Now;
            var (_, targetB, _, _) = Seed(now, now);

            await using var db = new AppDbContext(_options);
            Assert.True(await AppDbMaintenance.DeleteTargetAsync(db, targetB, default));

            Assert.False(await db.LinkSources.AnyAsync());
            Assert.False(await db.DbLinkUsages.AnyAsync());
            Assert.False(await AppDbMaintenance.DeleteTargetAsync(db, targetB, default));
        }
    }
}
