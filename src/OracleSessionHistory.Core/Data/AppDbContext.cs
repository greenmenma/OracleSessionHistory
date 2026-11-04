using Microsoft.EntityFrameworkCore;
using OracleSessionHistory.Core.Entities;

namespace OracleSessionHistory.Core.Data;

/// <summary>収集した履歴を保存するアプリケーション DB（既定は SQLite）</summary>
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<MonitorTarget> Targets => Set<MonitorTarget>();
    public DbSet<TrackedSession> Sessions => Set<TrackedSession>();
    public DbSet<LinkSource> LinkSources => Set<LinkSource>();
    public DbSet<DbLinkUsage> DbLinkUsages => Set<DbLinkUsage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<MonitorTarget>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Provider).HasMaxLength(30);
        });

        b.Entity<TrackedSession>(e =>
        {
            // 同じ Oracle セッションは 1 行だけ
            e.HasIndex(x => new { x.TargetId, x.InstId, x.Sid, x.Serial, x.LogonTime }).IsUnique();
            e.HasIndex(x => new { x.TargetId, x.IsConnected });
            e.HasIndex(x => x.LogonTime);
            e.HasIndex(x => x.LastSeenAt);
            e.HasIndex(x => x.Machine);
            e.HasIndex(x => x.Program);
            e.HasOne(x => x.Target).WithMany().HasForeignKey(x => x.TargetId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<LinkSource>(e =>
        {
            e.HasOne(x => x.Target).WithMany(t => t.LinkSources).HasForeignKey(x => x.TargetId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<DbLinkUsage>(e =>
        {
            e.HasIndex(x => new { x.LinkSourceId, x.DbLinkName, x.InstId, x.Sid, x.Serial, x.LogonTime }).IsUnique();
            e.HasIndex(x => new { x.TargetId, x.LastSeenAt });
            e.HasIndex(x => x.MatchedSessionId);
            e.HasOne(x => x.LinkSource).WithMany().HasForeignKey(x => x.LinkSourceId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.MatchedSession).WithMany(s => s.DbLinkOrigins).HasForeignKey(x => x.MatchedSessionId).OnDelete(DeleteBehavior.SetNull);
        });
    }
}
