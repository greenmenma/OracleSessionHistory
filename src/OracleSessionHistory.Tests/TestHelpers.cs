using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace OracleSessionHistory.Tests;

/// <summary>
/// 次の SaveChanges の直前に 1 回だけ SQL を実行する（別プロセスが同じ行を同時に更新・削除した状況の再現用）。
/// </summary>
public sealed class BeforeNextSaveInterceptor : SaveChangesInterceptor
{
    private string? _sql;

    public void Arm(string sql) => _sql = sql;

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _sql, null) is { } sql && eventData.Context is { } ctx)
        {
            DbConnection conn = ctx.Database.GetDbConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        return result;
    }
}
