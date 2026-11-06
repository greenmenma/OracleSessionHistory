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
    string? LinkOriginApp = null, string? LinkOriginMachine = null, string? LinkOriginUser = null);

public sealed record SessionSearch(
    int? TargetId, string? State, string? Q, string? Machine, string? Program, string? Username, string? OsUser,
    string? Kind, DateTime? From, DateTime? To, int Page = 1, int PageSize = 50,
    string? App = null, bool IncludeInternal = false, bool Exact = false, string? Via = null,
    string? Host = null, string? LinkSource = null, string? Origin = null, string? Source = null);

/// <summary>セッション履歴の 1 行。監視対象のセッション（session）か、DB Link 接続元 DB 上の DB Link 利用元（dblink）</summary>
public sealed record SessionRow(string Type, string TargetName, DateTime LogonTime, SessionListItem? Session, DbLinkUsageDto? Usage);

/// <summary>セッション履歴の検索 API</summary>
public static class SessionEndpoints
{
    /// <summary>完全一致の検索で「端末なし（MACHINE が空）」を表す値。接続アプリ表の「(不明)」から遷移した場合に使う</summary>
    public const string UnknownMachine = "(不明)";

    /// <summary>経由の条件（<see cref="SessionSearch.Via"/>）で、DB Link 経由ではない（直接の）接続を表す値。それ以外の値は経由した DB サーバー</summary>
    public const string DirectVia = "direct";

    /// <summary>一覧に出すもの（<see cref="SessionSearch.Source"/>）：DB Link 利用元のみ</summary>
    public const string DbLinkSource = "dblink";

    /// <summary>一覧に出すもの（<see cref="SessionSearch.Source"/>）：セッションと DB Link 利用元の両方</summary>
    public const string AllSource = "all";

    /// <summary>一覧の 1 行（DB Link の可能性がある行は、特定できた接続元のアプリも含める）</summary>
    internal static IQueryable<SessionListItem> Project(IQueryable<TrackedSession> q) =>
        q.Select(x => new SessionListItem(
            x.Id, x.TargetId, x.Target!.Name, x.InstId, x.Sid, x.Serial, x.LogonTime,
            x.Username, x.SchemaName, x.OsUser, x.Machine, x.Program, x.Module,
            x.Status, x.ProcessKind, x.JobName, x.IsConnected, x.FirstSeenAt, x.LastSeenAt,
            x.DisconnectedAt, x.DisconnectUncertain, x.DbLinkOrigins.Count,
            x.AppName, x.AppDetail, x.ProcOwner, x.ProcInfoState, x.ProcInfoError,
            // DB Link の可能性があるセッションは、特定できた接続元（最後に観測したもの）のアプリを一覧に出す
            x.LinkOriginApp, x.LinkOriginMachine, x.LinkOriginUser));

    public static void MapSessionEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api");

        // セッション履歴。Source により監視対象のセッションと DB Link 利用元を同じ一覧（ログオンが新しい順）で返す
        g.MapGet("/sessions", async ([AsParameters] SessionSearch s, AppDbContext db) =>
        {
            var pageSize = Math.Clamp(s.PageSize, 1, 500);
            var page = Math.Max(1, s.Page);
            var skip = (page - 1) * pageSize;
            var withSessions = s.Source != DbLinkSource;
            var withUsages = s.Source == DbLinkSource || (s.Source == AllSource && !HasSessionOnlyCondition(s));

            var sq = Filter(db.Sessions.AsNoTracking(), s, db.DirectoryUsers);
            var uq = FilterUsages(db.DbLinkUsages.AsNoTracking(), s, db.DirectoryUsers);
            var sessionTotal = withSessions ? await sq.CountAsync() : 0;
            var usageTotal = withUsages ? await uq.CountAsync() : 0;

            // 両方を出す場合は、それぞれの先頭から skip + pageSize 件を取り、ログオン順に並べ直してページを切り出す
            var take = withSessions && withUsages ? skip + pageSize : pageSize;
            var from = withSessions && withUsages ? 0 : skip;
            var rows = new List<SessionRow>();
            if (withSessions)
            {
                var items = await Project(sq.OrderByDescending(x => x.LogonTime) // 最新のログオンを上位に
                        .ThenByDescending(x => x.Id).Skip(from).Take(take))
                    .ToListAsync();
                rows.AddRange(items.Select(x => new SessionRow("session", x.TargetName, x.LogonTime, x, null)));
            }
            if (withUsages)
            {
                var targetNames = await db.Targets.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.Name);
                var items = await uq.OrderByDescending(u => u.LogonTime).ThenByDescending(u => u.Id).Skip(from).Take(take)
                    .Select(u => new { u.TargetId, Dto = DbLinkUsageDto.From(u, u.LinkSource!.Name) })
                    .ToListAsync();
                rows.AddRange(items.Select(x => new SessionRow("dblink", targetNames.GetValueOrDefault(x.TargetId, ""), x.Dto.LogonTime, null, x.Dto)));
            }
            if (withSessions && withUsages)
            {
                rows = rows.OrderByDescending(r => r.LogonTime).ThenBy(r => r.Type)
                    .ThenByDescending(r => r.Session?.Id ?? r.Usage!.Id)
                    .Skip(skip).Take(pageSize).ToList();
            }
            return new PagedResult<SessionRow>(rows, sessionTotal + usageTotal, page, pageSize);
        });

        // 実行ユーザの表示名（AD の displayName）。画面はアカウントの代わりに表示名を出す（キーはアカウントを大文字にした値）
        g.MapGet("/user-names", async (AppDbContext db, CancellationToken ct) =>
            await db.DirectoryUsers.AsNoTracking().Where(d => d.Found && d.DisplayName != null)
                .ToDictionaryAsync(d => d.Account, d => d.DisplayName!, ct));

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

    /// <summary>
    /// 監視対象のセッションにしか無い条件（状態・種別・Oracle 内部プロセス・接続アプリ表の行の条件）があるか。
    /// 「すべて」を表示する場合、これらの条件があれば DB Link 利用元は出さない
    /// </summary>
    internal static bool HasSessionOnlyCondition(SessionSearch s) =>
        !string.IsNullOrWhiteSpace(s.State) || !string.IsNullOrWhiteSpace(s.Kind) || s.IncludeInternal
        || !string.IsNullOrWhiteSpace(s.Via) || !string.IsNullOrWhiteSpace(s.Host)
        || !string.IsNullOrWhiteSpace(s.LinkSource) || !string.IsNullOrWhiteSpace(s.Origin);

    /// <summary>DB Link 利用元（接続元 DB 上で DB Link を使っていたセッション）の検索。セッションと共通の条件だけを使う</summary>
    /// <param name="users">実行ユーザの表示名（AD）。指定するとキーワード・実行ユーザの条件で表示名も探す</param>
    internal static IQueryable<DbLinkUsage> FilterUsages(IQueryable<DbLinkUsage> q, SessionSearch s, IQueryable<DirectoryUser>? users = null)
    {
        var none = q.Where(x => false).Select(x => "");
        IQueryable<string> NamedAccounts(string pattern) =>
            users?.Where(d => d.Found && EF.Functions.Like(d.DisplayName, pattern, "\\")).Select(d => d.Account) ?? none;
        if (s.TargetId is { } tid) q = q.Where(x => x.TargetId == tid);
        if (!string.IsNullOrWhiteSpace(s.Q))
        {
            var p = QueryHelpers.Contains(s.Q.Trim());
            var named = NamedAccounts(p);
            q = q.Where(x =>
                EF.Functions.Like(x.Machine, p, "\\") || EF.Functions.Like(x.Program, p, "\\") ||
                EF.Functions.Like(x.Username, p, "\\") || EF.Functions.Like(x.SchemaName, p, "\\") ||
                EF.Functions.Like(x.OsUser, p, "\\") || EF.Functions.Like(x.Module, p, "\\") ||
                EF.Functions.Like(x.Action, p, "\\") || EF.Functions.Like(x.Terminal, p, "\\") ||
                EF.Functions.Like(x.DbLinkName, p, "\\") || EF.Functions.Like(x.SqlText, p, "\\") ||
                EF.Functions.Like(x.AppName, p, "\\") || EF.Functions.Like(x.AppDetail, p, "\\") ||
                EF.Functions.Like(x.ProcPath, p, "\\") || EF.Functions.Like(x.ProcCommandLine, p, "\\") ||
                EF.Functions.Like(x.ProcOwner, p, "\\") || EF.Functions.Like(x.ProcAncestry, p, "\\") ||
                EF.Functions.Like(x.LinkSource!.Name, p, "\\") ||
                named.Contains(x.OsUser!.Trim().ToUpper()) || named.Contains(x.ProcOwner!.Trim().ToUpper()));
        }
        if (!string.IsNullOrWhiteSpace(s.Machine))
        {
            var m = s.Machine.Trim();
            q = !s.Exact ? q.Where(x => EF.Functions.Like(x.Machine, QueryHelpers.Contains(m), "\\"))
                : m == UnknownMachine ? q.Where(x => x.Machine == null || x.Machine == "")
                : q.Where(x => x.Machine == m);
        }
        if (!string.IsNullOrWhiteSpace(s.Program)) q = q.Where(x => EF.Functions.Like(x.Program, QueryHelpers.Contains(s.Program.Trim()), "\\"));
        if (!string.IsNullOrWhiteSpace(s.Username)) q = q.Where(x => EF.Functions.Like(x.Username, QueryHelpers.Contains(s.Username.Trim()), "\\"));
        if (!string.IsNullOrWhiteSpace(s.OsUser))
        {
            var p = QueryHelpers.Contains(s.OsUser.Trim());
            var named = NamedAccounts(p);
            q = q.Where(x => EF.Functions.Like(x.OsUser, p, "\\") || EF.Functions.Like(x.ProcOwner, p, "\\")
                             || named.Contains(x.OsUser!.Trim().ToUpper()) || named.Contains(x.ProcOwner!.Trim().ToUpper()));
        }
        if (!string.IsNullOrWhiteSpace(s.App))
        {
            var app = s.App.Trim();
            q = s.Exact ? q.Where(x => x.AppName == app) : q.Where(x => EF.Functions.Like(x.AppName, QueryHelpers.Contains(app), "\\"));
        }
        // 期間：指定期間に使われていた利用元
        if (s.From is { } from) q = q.Where(x => x.LastSeenAt >= from);
        if (s.To is { } to) q = q.Where(x => x.LogonTime <= to);
        return q;
    }

    /// <param name="users">実行ユーザの表示名（AD）。指定するとキーワード・実行ユーザの条件で表示名も探す</param>
    internal static IQueryable<TrackedSession> Filter(IQueryable<TrackedSession> q, SessionSearch s, IQueryable<DirectoryUser>? users = null)
    {
        // 表示名が一致するアカウント（アカウントは大文字で保存している）。表示名を使わない場合は空の副問い合わせ
        var none = q.Where(x => false).Select(x => "");
        IQueryable<string> NamedAccounts(string pattern) =>
            users?.Where(d => d.Found && EF.Functions.Like(d.DisplayName, pattern, "\\")).Select(d => d.Account) ?? none;
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
            var named = NamedAccounts(p);
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
                EF.Functions.Like(x.LinkOriginMachine, p, "\\") || EF.Functions.Like(x.LinkOriginApp, p, "\\") ||
                named.Contains(x.OsUser!.Trim().ToUpper()) || named.Contains(x.ProcOwner!.Trim().ToUpper()));
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
        // DB Link 経由で特定した接続元の端末（接続アプリ表の DB Link の下の端末）
        if (!string.IsNullOrWhiteSpace(s.Origin))
        {
            var origin = s.Origin.Trim();
            q = origin == UnknownMachine ? q.Where(x => x.LinkOriginApp != null && (x.LinkOriginMachine == null || x.LinkOriginMachine == ""))
                : q.Where(x => x.LinkOriginApp != null && x.LinkOriginMachine == origin);
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
            var named = NamedAccounts(p);
            q = q.Where(x => EF.Functions.Like(x.OsUser, p, "\\") || EF.Functions.Like(x.ProcOwner, p, "\\")
                             || named.Contains(x.OsUser!.Trim().ToUpper()) || named.Contains(x.ProcOwner!.Trim().ToUpper()));
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
