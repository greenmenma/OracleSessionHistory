using Microsoft.EntityFrameworkCore;
using OracleSessionHistory.Core.Collection;
using OracleSessionHistory.Core.Data;
using OracleSessionHistory.Core.Entities;
using OracleSessionHistory.Server.Support;

namespace OracleSessionHistory.Server.Api;

public sealed record TargetDto(
    int Id, string Name, string Provider, string ConnectionString, int IntervalSeconds, int LinkIntervalSeconds,
    bool IncludeBackground, bool CollectSqlText, bool Enabled, string? Description,
    DateTime? LastPollAt, DateTime? LastSuccessAt, string? LastError, int ConsecutiveFailures, int? LastSessionCount)
{
    public static TargetDto From(MonitorTarget t) => new(
        t.Id, t.Name, t.Provider, ConnectionStringMasker.MaskPassword(t.ConnectionString), t.IntervalSeconds, t.LinkIntervalSeconds,
        t.IncludeBackground, t.CollectSqlText, t.Enabled, t.Description,
        t.LastPollAt, t.LastSuccessAt, t.LastError, t.ConsecutiveFailures, t.LastSessionCount);
}

public sealed record TargetInput(
    string Name, string Provider, string ConnectionString, int IntervalSeconds, int LinkIntervalSeconds,
    bool IncludeBackground, bool CollectSqlText, bool Enabled, string? Description);

public sealed record TestConnectionInput(string Provider, string ConnectionString, int? TargetId, int? LinkSourceId);

/// <summary>監視対象の管理 API</summary>
public static class TargetEndpoints
{
    public static void MapTargetEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api");

        g.MapGet("/providers", (SessionSourceFactory f) => f.Providers.Order());

        g.MapGet("/targets", async (AppDbContext db) =>
            (await db.Targets.AsNoTracking().OrderBy(t => t.Id).ToListAsync()).Select(TargetDto.From));

        g.MapPost("/targets", async (TargetInput input, AppDbContext db) =>
        {
            if (Validate(input) is { } err) return Results.ValidationProblem(err);
            if (await db.Targets.AnyAsync(t => t.Name == input.Name))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["同じ名前の監視対象があります"] });
            var now = DateTime.Now;
            var t = new MonitorTarget { CreatedAt = now };
            Apply(input, t, now);
            db.Targets.Add(t);
            await db.SaveChangesAsync();
            return Results.Ok(TargetDto.From(t));
        });

        g.MapPut("/targets/{id:int}", async (int id, TargetInput input, AppDbContext db) =>
        {
            if (Validate(input) is { } err) return Results.ValidationProblem(err);
            var t = await db.Targets.FindAsync(id);
            if (t is null) return Results.NotFound();
            if (await db.Targets.AnyAsync(x => x.Name == input.Name && x.Id != id))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["同じ名前の監視対象があります"] });
            Apply(input, t, DateTime.Now);
            await db.SaveChangesAsync();
            return Results.Ok(TargetDto.From(t));
        });

        g.MapDelete("/targets/{id:int}", async (int id, AppDbContext db) =>
        {
            var deleted = await db.Targets.Where(t => t.Id == id).ExecuteDeleteAsync();
            return deleted > 0 ? Results.NoContent() : Results.NotFound();
        });

        g.MapPost("/targets/test", async (TestConnectionInput input, AppDbContext db, SessionSourceFactory f, CancellationToken ct) =>
        {
            var cs = input.ConnectionString;
            if (input.TargetId is { } tid && await db.Targets.FindAsync([tid], ct) is { } stored)
                cs = ConnectionStringMasker.Merge(cs, stored.ConnectionString);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(30));
                var msg = await f.Get(input.Provider).TestConnectionAsync(cs, cts.Token);
                return Results.Ok(new { success = true, message = msg });
            }
            catch (Exception ex)
            {
                return Results.Ok(new { success = false, message = ex.Message });
            }
        });
    }

    private static void Apply(TargetInput input, MonitorTarget t, DateTime now)
    {
        t.Name = input.Name.Trim();
        t.Provider = input.Provider;
        t.ConnectionString = ConnectionStringMasker.Merge(input.ConnectionString, t.ConnectionString);
        t.IntervalSeconds = input.IntervalSeconds;
        t.LinkIntervalSeconds = input.LinkIntervalSeconds;
        t.IncludeBackground = input.IncludeBackground;
        t.CollectSqlText = input.CollectSqlText;
        t.Enabled = input.Enabled;
        t.Description = input.Description;
        // Batch はこの値の変化で設定変更を検知する
        t.UpdatedAt = now;
    }

    private static Dictionary<string, string[]>? Validate(TargetInput i)
    {
        var e = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(i.Name)) e["name"] = ["名前は必須です"];
        if (!SourceProviders.All.Contains(i.Provider)) e["provider"] = ["Provider が不正です"];
        if (string.IsNullOrWhiteSpace(i.ConnectionString)) e["connectionString"] = ["接続文字列は必須です"];
        if (i.IntervalSeconds is < 1 or > 3600) e["intervalSeconds"] = ["取得間隔は 1〜3600 秒で指定してください"];
        if (i.LinkIntervalSeconds is < 5 or > 3600) e["linkIntervalSeconds"] = ["DB Link 取得間隔は 5〜3600 秒で指定してください"];
        return e.Count > 0 ? e : null;
    }
}
