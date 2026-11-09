using Microsoft.EntityFrameworkCore;
using OracleSessionHistory.Core.Entities;

namespace OracleSessionHistory.Core.Data;

/// <summary>
/// 収集した履歴を保存するアプリケーション DB（SQLite / SQL Server）。
/// SQL Server でも作成できるよう、インデックス対象の文字列には長さを指定し、
/// 削除時の連鎖経路が複数にならないようにする。
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<MonitorTarget> Targets => Set<MonitorTarget>();
    public DbSet<TrackedSession> Sessions => Set<TrackedSession>();
    public DbSet<LinkSource> LinkSources => Set<LinkSource>();
    public DbSet<DbLinkUsage> DbLinkUsages => Set<DbLinkUsage>();
    public DbSet<ProcessInspectionSettings> ProcessInspectionSettings => Set<ProcessInspectionSettings>();
    public DbSet<AuditLogonCount> AuditLogonCounts => Set<AuditLogonCount>();
    public DbSet<DirectoryUser> DirectoryUsers => Set<DirectoryUser>();
    public DbSet<AppAssignee> AppAssignees => Set<AppAssignee>();

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
            // インデックス対象（SQL Server の nvarchar(max) はインデックスにできない）。Oracle の列長より十分大きくする
            e.Property(x => x.Machine).HasMaxLength(256);
            e.Property(x => x.Program).HasMaxLength(256);
            e.Property(x => x.ProcessKind).HasMaxLength(30);
            // 接続アプリの集計（端末 × アプリ）と、プロセス情報の取得対象の検索に使う
            e.Property(x => x.AppName).HasMaxLength(256);
            e.Property(x => x.ProcInfoState).HasMaxLength(20);
            e.Property(x => x.IisAppPool).HasMaxLength(256);
            e.HasIndex(x => new { x.TargetId, x.Machine, x.AppName });
            e.HasIndex(x => new { x.IsConnected, x.ProcInfoState });
            e.Property(x => x.LinkOriginMachine).HasMaxLength(256);
            e.Property(x => x.LinkOriginApp).HasMaxLength(256);
            e.Property(x => x.LinkOriginSource).HasMaxLength(100);
            e.Property(x => x.LinkOriginUser).HasMaxLength(256);
            e.HasIndex(x => new { x.LinkOriginMachine, x.LinkOriginApp });
            e.HasOne(x => x.Target).WithMany().HasForeignKey(x => x.TargetId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AuditLogonCount>(e =>
        {
            e.HasIndex(x => new { x.TargetId, x.Day, x.Machine }).IsUnique();
            e.Property(x => x.Machine).HasMaxLength(256);
            e.HasOne(x => x.Target).WithMany().HasForeignKey(x => x.TargetId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<DirectoryUser>(e =>
        {
            e.HasKey(x => x.Account);
            e.Property(x => x.Account).HasMaxLength(256);
            e.Property(x => x.DisplayName).HasMaxLength(256);
            e.HasIndex(x => x.DisplayName);
        });

        b.Entity<AppAssignee>(e =>
        {
            // SQL Server の主キーの上限（900 バイト）に収まる長さ
            e.HasKey(x => x.NodeKey);
            e.Property(x => x.NodeKey).HasMaxLength(AppAssigneeLimits.NodeKeyLength);
            e.Property(x => x.Assignee).HasMaxLength(AppAssigneeLimits.AssigneeLength);
        });

        b.Entity<ProcessInspectionSettings>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.UserName).HasMaxLength(256);
            e.Property(x => x.DnsSuffix).HasMaxLength(256);
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
            e.Property(x => x.DbLinkName).HasMaxLength(256);
            e.Property(x => x.MatchType).HasMaxLength(30);
            e.Property(x => x.AppName).HasMaxLength(256);
            e.Property(x => x.ProcInfoState).HasMaxLength(20);
            e.Property(x => x.IisAppPool).HasMaxLength(256);
            e.HasIndex(x => new { x.ProcInfoState, x.LastSeenAt });
            e.HasOne(x => x.LinkSource).WithMany().HasForeignKey(x => x.LinkSourceId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.MatchedSession).WithMany(s => s.DbLinkOrigins).HasForeignKey(x => x.MatchedSessionId)
                // SQL Server は監視対象からの連鎖経路が 2 つになるため DB 側の SET NULL を使えない。
                // セッション削除前に AppDbMaintenance で参照を外す
                .OnDelete(DeleteBehavior.ClientSetNull);
        });
    }
}
