using Microsoft.EntityFrameworkCore;
using OpenDbSession.Core.Data;
using OpenDbSession.Core.Entities;
using OpenDbSession.Server.Support;

namespace OpenDbSession.Server.Api;

public sealed record SessionListItem(
    long Id, int TargetId, string TargetName, int InstId, int Sid, int Serial, DateTime LogonTime,
    string? Username, string? SchemaName, string? OsUser, string? Machine, string? Program, string? Module,
    string? Status, string ProcessKind, string? JobName, bool IsConnected, DateTime FirstSeenAt, DateTime LastSeenAt,
    DateTime? DisconnectedAt, bool DisconnectUncertain, int DbLinkOriginCount);

public sealed record SessionSearch(
    int? TargetId, string? State, string? Q, string? Machine, string? Program, string? Username, string? OsUser,
    string? Kind, DateTime? From, DateTime? To, int Page = 1, int PageSize = 50);

/// <summary>セッション履歴の検索 API</summary>
public static class SessionEndpoints
{
    public static void MapSessionEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api");

        g.MapGet("/sessions", async ([AsParameters] SessionSearch s, AppDbContext db) =>
        {
            var q = Filter(db.Sessions.AsNoTracking(), s);
            var pageSize = Math.Clamp(s.PageSize, 1, 500);
            var page = Math.Max(1, s.Page);
            var total = await q.CountAsync();
            var items = await q
                .OrderByDescending(x => x.LogonTime) // 最新のログオンを上位に
                .ThenByDescending(x => x.Id)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(x => new SessionListItem(
                    x.Id, x.TargetId, x.Target!.Name, x.InstId, x.Sid, x.Serial, x.LogonTime,
                    x.Username, x.SchemaName, x.OsUser, x.Machine, x.Program, x.Module,
                    x.Status, x.ProcessKind, x.JobName, x.IsConnected, x.FirstSeenAt, x.LastSeenAt,
                    x.DisconnectedAt, x.DisconnectUncertain, x.DbLinkOrigins.Count))
                .ToListAsync();
            return new PagedResult<SessionListItem>(items, total, page, pageSize);
        });

        g.MapGet("/sessions/{id:long}", async (long id, AppDbContext db) =>
        {
            // 接続文字列を返さないよう Target は Include しない
            var s = await db.Sessions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
            if (s is null) return Results.NotFound();
            var targetName = await db.Targets.Where(t => t.Id == s.TargetId).Select(t => t.Name).SingleAsync();

            // プロセス ID で確定した接続元
            var origins = await db.DbLinkUsages.AsNoTracking()
                .Where(u => u.MatchedSessionId == id)
                .OrderByDescending(u => u.LastSeenAt)
                .Select(u => DbLinkUsageDto.From(u, u.LinkSource!.Name))
                .ToListAsync();

            // 確定できない場合は、同じ時間帯に DB Link を使っていた接続元セッションを候補として出す
            var candidates = new List<DbLinkUsageDto>();
            if (s.ProcessKind == ProcessKinds.DbLinkSuspect && origins.Count == 0)
            {
                candidates = await db.DbLinkUsages.AsNoTracking()
                    .Where(u => u.TargetId == s.TargetId && u.MatchedSessionId == null
                                && u.LastSeenAt >= s.FirstSeenAt && u.FirstSeenAt <= s.LastSeenAt)
                    .OrderByDescending(u => u.LastSeenAt).Take(50)
                    .Select(u => DbLinkUsageDto.From(u, u.LinkSource!.Name))
                    .ToListAsync();
            }

            var hasLinkSource = await db.LinkSources.AnyAsync(l => l.TargetId == s.TargetId);
            return Results.Ok(new { session = s, targetName, origins, candidates, hasLinkSource });
        });
    }

    internal static IQueryable<TrackedSession> Filter(IQueryable<TrackedSession> q, SessionSearch s)
    {
        if (s.TargetId is { } tid) q = q.Where(x => x.TargetId == tid);
        q = s.State switch
        {
            "connected" => q.Where(x => x.IsConnected),
            "disconnected" => q.Where(x => !x.IsConnected),
            _ => q,
        };
        if (!string.IsNullOrWhiteSpace(s.Q))
        {
            var p = QueryHelpers.Contains(s.Q.Trim());
            q = q.Where(x =>
                EF.Functions.Like(x.Machine, p, "\\") || EF.Functions.Like(x.Program, p, "\\") ||
                EF.Functions.Like(x.Username, p, "\\") || EF.Functions.Like(x.SchemaName, p, "\\") ||
                EF.Functions.Like(x.OsUser, p, "\\") || EF.Functions.Like(x.Module, p, "\\") ||
                EF.Functions.Like(x.Action, p, "\\") || EF.Functions.Like(x.Terminal, p, "\\") ||
                EF.Functions.Like(x.ClientInfo, p, "\\") || EF.Functions.Like(x.JobName, p, "\\") ||
                EF.Functions.Like(x.LastSqlText, p, "\\"));
        }
        if (!string.IsNullOrWhiteSpace(s.Machine)) q = q.Where(x => EF.Functions.Like(x.Machine, QueryHelpers.Contains(s.Machine.Trim()), "\\"));
        if (!string.IsNullOrWhiteSpace(s.Program)) q = q.Where(x => EF.Functions.Like(x.Program, QueryHelpers.Contains(s.Program.Trim()), "\\"));
        if (!string.IsNullOrWhiteSpace(s.Username)) q = q.Where(x => EF.Functions.Like(x.Username, QueryHelpers.Contains(s.Username.Trim()), "\\"));
        if (!string.IsNullOrWhiteSpace(s.OsUser)) q = q.Where(x => EF.Functions.Like(x.OsUser, QueryHelpers.Contains(s.OsUser.Trim()), "\\"));
        if (!string.IsNullOrWhiteSpace(s.Kind)) q = q.Where(x => x.ProcessKind == s.Kind);
        // 期間：指定期間に接続していたセッション
        if (s.From is { } from) q = q.Where(x => x.LastSeenAt >= from);
        if (s.To is { } to) q = q.Where(x => x.LogonTime <= to);
        return q;
    }
}
