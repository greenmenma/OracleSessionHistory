using Microsoft.EntityFrameworkCore;
using OracleSessionHistory.Core.Data;
using OracleSessionHistory.Core.Entities;
using OracleSessionHistory.Server.Support;

namespace OracleSessionHistory.Server.Api;

public sealed record SessionListItem(
    long Id, int TargetId, string TargetName, int InstId, int Sid, int Serial, DateTime LogonTime,
    string? Username, string? SchemaName, string? OsUser, string? Machine, string? Program, string? Module,
    string? Status, string ProcessKind, string? JobName, bool IsConnected, DateTime FirstSeenAt, DateTime LastSeenAt,
    DateTime? DisconnectedAt, bool DisconnectUncertain, int DbLinkOriginCount,
    string AppName, string? AppDetail, string? ProcOwner, string ProcInfoState, string? ProcInfoError,
    string? LinkOriginApp = null, string? LinkOriginMachine = null);

public sealed record SessionSearch(
    int? TargetId, string? State, string? Q, string? Machine, string? Program, string? Username, string? OsUser,
    string? Kind, DateTime? From, DateTime? To, int Page = 1, int PageSize = 50,
    string? App = null, bool IncludeInternal = false, bool Exact = false, string? Via = null,
    string? Host = null, string? LinkSource = null);

/// <summary>セッション履歴の検索 API</summary>
public static class SessionEndpoints
{
    /// <summary>完全一致の検索で「端末なし（MACHINE が空）」を表す値。接続アプリ表の「(不明)」から遷移した場合に使う</summary>
    public const string UnknownMachine = "(不明)";

    /// <summary>経由の条件（<see cref="SessionSearch.Via"/>）で、DB Link 経由ではない（直接の）接続を表す値。それ以外の値は経由した DB サーバー</summary>
    public const string DirectVia = "direct";

    /// <summary>一覧の 1 行（DB Link の可能性がある行は、特定できた接続元のアプリも含める）</summary>
    internal static IQueryable<SessionListItem> Project(IQueryable<TrackedSession> q) =>
        q.Select(x => new SessionListItem(
            x.Id, x.TargetId, x.Target!.Name, x.InstId, x.Sid, x.Serial, x.LogonTime,
            x.Username, x.SchemaName, x.OsUser, x.Machine, x.Program, x.Module,
            x.Status, x.ProcessKind, x.JobName, x.IsConnected, x.FirstSeenAt, x.LastSeenAt,
            x.DisconnectedAt, x.DisconnectUncertain, x.DbLinkOrigins.Count,
            x.AppName, x.AppDetail, x.ProcOwner, x.ProcInfoState, x.ProcInfoError,
            // DB Link の可能性があるセッションは、特定できた接続元（最後に観測したもの）のアプリを一覧に出す
            x.LinkOriginApp, x.LinkOriginMachine));

    public static void MapSessionEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api");

        g.MapGet("/sessions", async ([AsParameters] SessionSearch s, AppDbContext db) =>
        {
            var q = Filter(db.Sessions.AsNoTracking(), s);
            var pageSize = Math.Clamp(s.PageSize, 1, 500);
            var page = Math.Max(1, s.Page);
            var total = await q.CountAsync();
            var items = await Project(q
                    .OrderByDescending(x => x.LogonTime) // 最新のログオンを上位に
                    .ThenByDescending(x => x.Id)
                    .Skip((page - 1) * pageSize).Take(pageSize))
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
                EF.Functions.Like(x.LastSqlText, p, "\\") || EF.Functions.Like(x.AppName, p, "\\") ||
                EF.Functions.Like(x.AppDetail, p, "\\") || EF.Functions.Like(x.ProcPath, p, "\\") ||
                EF.Functions.Like(x.ProcCommandLine, p, "\\") || EF.Functions.Like(x.ProcOwner, p, "\\") ||
                EF.Functions.Like(x.ProcAncestry, p, "\\") ||
                EF.Functions.Like(x.LinkOriginMachine, p, "\\") || EF.Functions.Like(x.LinkOriginApp, p, "\\"));
        }
        // 端末・アプリは、DB Link 経由で接続元を特定できた行（LinkOriginApp あり）は接続元の端末・アプリで探す（接続アプリ表と同じまとめ方）
        if (!string.IsNullOrWhiteSpace(s.Machine))
        {
            var m = s.Machine.Trim();
            // 完全一致：接続アプリ表から遷移した場合（部分一致だと WEB01 で WEB011 も出てしまう）。
            // 端末名は保存時に前後の空白を除いているため、インデックスを使えるようそのまま比べる
            if (!s.Exact)
            {
                var p = QueryHelpers.Contains(m);
                q = q.Where(x => EF.Functions.Like(x.Machine, p, "\\") || EF.Functions.Like(x.LinkOriginMachine, p, "\\"));
            }
            else if (m == UnknownMachine)
            {
                q = q.Where(x => (x.LinkOriginApp == null && (x.Machine == null || x.Machine == ""))
                                 || (x.LinkOriginApp != null && (x.LinkOriginMachine == null || x.LinkOriginMachine == "")));
            }
            else
            {
                q = q.Where(x => (x.LinkOriginApp == null && x.Machine == m) || (x.LinkOriginApp != null && x.LinkOriginMachine == m));
            }
        }
        // 接続アプリ表の行から遷移した場合：MACHINE の完全一致（DB Link 経由も接続元ではなく DB サーバーの端末で探す）と DB Link 接続元
        if (!string.IsNullOrWhiteSpace(s.Host))
        {
            var host = s.Host.Trim();
            q = host == UnknownMachine ? q.Where(x => x.Machine == null || x.Machine == "") : q.Where(x => x.Machine == host);
        }
        if (!string.IsNullOrWhiteSpace(s.LinkSource))
        {
            var source = s.LinkSource.Trim();
            q = source == UnknownMachine ? q.Where(x => x.LinkOriginApp != null && x.LinkOriginSource == null)
                : q.Where(x => x.LinkOriginApp != null && x.LinkOriginSource == source);
        }
        // 経由：接続アプリ表の親子の行から遷移した場合。直接の接続だけ、または特定の DB サーバーを経由した DB Link 経由の接続だけにする
        if (!string.IsNullOrWhiteSpace(s.Via))
        {
            var via = s.Via.Trim();
            q = via == DirectVia ? q.Where(x => x.LinkOriginApp == null)
                : via == UnknownMachine ? q.Where(x => x.LinkOriginApp != null && (x.Machine == null || x.Machine == ""))
                : q.Where(x => x.LinkOriginApp != null && x.Machine == via);
        }
        if (!string.IsNullOrWhiteSpace(s.Program)) q = q.Where(x => EF.Functions.Like(x.Program, QueryHelpers.Contains(s.Program.Trim()), "\\"));
        if (!string.IsNullOrWhiteSpace(s.Username)) q = q.Where(x => EF.Functions.Like(x.Username, QueryHelpers.Contains(s.Username.Trim()), "\\"));
        if (!string.IsNullOrWhiteSpace(s.OsUser))
        {
            var p = QueryHelpers.Contains(s.OsUser.Trim());
            q = q.Where(x => EF.Functions.Like(x.OsUser, p, "\\") || EF.Functions.Like(x.ProcOwner, p, "\\"));
        }
        if (!string.IsNullOrWhiteSpace(s.App))
        {
            var app = s.App.Trim();
            var p = QueryHelpers.Contains(app);
            q = s.Exact ? q.Where(x => (x.LinkOriginApp == null && x.AppName == app) || x.LinkOriginApp == app)
                : q.Where(x => EF.Functions.Like(x.AppName, p, "\\") || EF.Functions.Like(x.LinkOriginApp, p, "\\"));
        }
        // Oracle 内部プロセス（ジョブ・パラレル実行等）は既定で表示しない。種別を明示した場合はそれに従う
        if (!string.IsNullOrWhiteSpace(s.Kind)) q = q.Where(x => x.ProcessKind == s.Kind);
        else if (!s.IncludeInternal) q = q.WhereVisible();
        // 期間：指定期間に接続していたセッション
        if (s.From is { } from) q = q.Where(x => x.LastSeenAt >= from);
        if (s.To is { } to) q = q.Where(x => x.LogonTime <= to);
        return q;
    }
}
