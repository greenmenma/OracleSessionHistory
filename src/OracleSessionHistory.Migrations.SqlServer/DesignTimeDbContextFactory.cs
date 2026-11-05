using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using OracleSessionHistory.Core.Data;

namespace OracleSessionHistory.Migrations.SqlServer;

/// <summary>dotnet ef 用。マイグレーションの作成には DB 接続は不要</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) =>
        new((DbContextOptions<AppDbContext>)AppDatabase.Configure(
            new DbContextOptionsBuilder<AppDbContext>(), AppDbProviders.SqlServer,
            "Server=localhost;Database=OracleSessionHistory;Integrated Security=true;TrustServerCertificate=true").Options);
}
