using Microsoft.EntityFrameworkCore;
using OracleSessionHistory.Core.Data;
using OracleSessionHistory.Core.DbLinks;
using OracleSessionHistory.Core.Entities;
using OracleSessionHistory.Server.Support;

namespace OracleSessionHistory.Server.Api;

public sealed record LinkSourceDto(
    int Id, int TargetId, string Name, string Provider, string ConnectionString, bool Enabled, List<string> SelectedLinks,
    DateTime? LastPollAt, DateTime? LastSuccessAt, string? LastError)
{
    public static LinkSourceDto From(LinkSource l) => new(
        l.Id, l.TargetId, l.Name, l.Provider, ConnectionStringMasker.MaskPassword(l.ConnectionString), l.Enabled, l.SelectedLinks,
        l.LastPollAt, l.LastSuccessAt, l.LastError);
}

public sealed record LinkSourceInput(string Name, string Provider, string ConnectionString, bool Enabled, List<string>? SelectedLinks);

public sealed record ListLinksInput(string Provider, string ConnectionString, int? LinkSourceId);

public sealed record DbLinkUsageDto(
    long Id, int LinkSourceId, string LinkSourceName, string DbLinkName, string MatchType, int InstId, int Sid, int Serial,
    DateTime LogonTime, string? Username, string? SchemaName, string? OsUser, string? Machine, string? Terminal, string? Program,
    string? Module, string? Action, string? ServerProcess, string? SqlId, string? SqlText, long? MatchedSessionId,
    DateTime FirstSeenAt, DateTime LastSeenAt, int HitCount,
    string? ClientProcess, string AppName, string? AppDetail, string ProcInfoState, string? ProcInfoError, DateTime? ProcInfoAt,
    string? ProcName, string? ProcPath, string? ProcCommandLine, string? ProcOwner, DateTime? ProcStartedAt, string? ProcParentName,
    string? IisAppPool, string? IisSite)
{
    public static DbLinkUsageDto From(DbLinkUsage u, string linkSourceName) => new(
        u.Id, u.LinkSourceId, linkSourceName, u.DbLinkName, u.MatchType, u.InstId, u.Sid, u.Serial,
        u.LogonTime, u.Username, u.SchemaName, u.OsUser, u.Machine, u.Terminal, u.Program,
        u.Module, u.Action, u.ServerProcess, u.SqlId, u.SqlText, u.MatchedSessionId,
        u.FirstSeenAt, u.LastSeenAt, u.HitCount,
        u.ClientProcess, u.AppName, u.AppDetail, u.ProcInfoState, u.ProcInfoError, u.ProcInfoAt,
        u.ProcName, u.ProcPath, u.ProcCommandLine, u.ProcOwner, u.ProcStartedAt, u.ProcParentName,
        u.IisAppPool, u.IisSite);
}

/// <summary>DB Link 接続元の設定・利用状況の API</summary>
public static class LinkEndpoints
{
    public static void MapLinkEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api");

        g.MapGet("/targets/{targetId:int}/link-sources", async (int targetId, AppDbContext db) =>
            (await db.LinkSources.AsNoTracking().Where(l => l.TargetId == targetId).OrderBy(l => l.Id).ToListAsync())
            .Select(LinkSourceDto.From));

        g.MapPost("/targets/{targetId:int}/link-sources", async (int targetId, LinkSourceInput input, AppDbContext db) =>
        {
            if (!await db.Targets.AnyAsync(t => t.Id == targetId)) return Results.NotFound();
            if (Validate(input) is { } err) return Results.ValidationProblem(err);
            var now = DateTime.Now;
            var l = new LinkSource { TargetId = targetId, CreatedAt = now };
            Apply(input, l, now);
            db.LinkSources.Add(l);
            await db.SaveChangesAsync();
            return Results.Ok(LinkSourceDto.From(l));
        });

        g.MapPut("/link-sources/{id:int}", async (int id, LinkSourceInput input, AppDbContext db) =>
        {
            if (Validate(input) is { } err) return Results.ValidationProblem(err);
            var l = await db.LinkSources.FindAsync(id);
            if (l is null) return Results.NotFound();
            Apply(input, l, DateTime.Now);
            await db.SaveChangesAsync();
            return Results.Ok(LinkSourceDto.From(l));
        });

        g.MapDelete("/link-sources/{id:int}", async (int id, AppDbContext db) =>
        {
            var n = await db.LinkSources.Where(l => l.Id == id).ExecuteDeleteAsync();
            return n > 0 ? Results.NoContent() : Results.NotFound();
        });

        // 接続文字列から DB Link の一覧を取得する
        g.MapPost("/db-links", async (ListLinksInput input, AppDbContext db, DbLinkInspectorFactory f, CancellationToken ct) =>
        {
            var cs = input.ConnectionString;
            if (input.LinkSourceId is { } id && await db.LinkSources.FindAsync([id], ct) is { } stored)
                cs = ConnectionStringMasker.Merge(cs, stored.ConnectionString);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(30));
                var links = await f.Get(input.Provider).ListLinksAsync(cs, cts.Token);
                return Results.Ok(new { success = true, links, message = (string?)null });
            }
            catch (Exception ex)
            {
                return Results.Ok(new { success = false, links = Array.Empty<DbLinkInfo>(), message = ex.Message });
            }
        });

        // DB Link の利用状況（接続元のセッション）
        g.MapGet("/db-link-usages", async (int? targetId, int? linkSourceId, string? q, int? page, int? pageSize, AppDbContext db) =>
        {
            var query = db.DbLinkUsages.AsNoTracking();
            if (targetId is { } tid) query = query.Where(u => u.TargetId == tid);
            if (linkSourceId is { } lid) query = query.Where(u => u.LinkSourceId == lid);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var p = QueryHelpers.Contains(q.Trim());
                query = query.Where(u => EF.Functions.Like(u.Machine, p, "\\") || EF.Functions.Like(u.Program, p, "\\")
                                         || EF.Functions.Like(u.Username, p, "\\") || EF.Functions.Like(u.OsUser, p, "\\")
                                         || EF.Functions.Like(u.DbLinkName, p, "\\") || EF.Functions.Like(u.SqlText, p, "\\")
                                         || EF.Functions.Like(u.AppName, p, "\\") || EF.Functions.Like(u.ProcPath, p, "\\")
                                         || EF.Functions.Like(u.ProcOwner, p, "\\"));
            }
            var ps = Math.Clamp(pageSize ?? 50, 1, 500);
            var pg = Math.Max(1, page ?? 1);
            var total = await query.CountAsync();
            var items = await query.OrderByDescending(u => u.LastSeenAt).ThenByDescending(u => u.Id)
                .Skip((pg - 1) * ps).Take(ps)
                .Select(u => DbLinkUsageDto.From(u, u.LinkSource!.Name))
                .ToListAsync();
            return new PagedResult<DbLinkUsageDto>(items, total, pg, ps);
        });
    }

    private static void Apply(LinkSourceInput input, LinkSource l, DateTime now)
    {
        l.Name = input.Name.Trim();
        l.Provider = input.Provider;
        l.ConnectionString = ConnectionStringMasker.Merge(input.ConnectionString, l.ConnectionString);
        l.Enabled = input.Enabled;
        l.SelectedLinks = input.SelectedLinks?.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList() ?? [];
        l.UpdatedAt = now;
    }

    private static Dictionary<string, string[]>? Validate(LinkSourceInput i)
    {
        var e = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(i.Name)) e["name"] = ["名前は必須です"];
        if (!SourceProviders.All.Contains(i.Provider)) e["provider"] = ["Provider が不正です"];
        if (string.IsNullOrWhiteSpace(i.ConnectionString)) e["connectionString"] = ["接続文字列は必須です"];
        return e.Count > 0 ? e : null;
    }
}
