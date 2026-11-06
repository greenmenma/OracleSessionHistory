using Microsoft.EntityFrameworkCore;
using OracleSessionHistory.Core.Data;
using OracleSessionHistory.Core.Entities;
using OracleSessionHistory.Server.Support;

namespace OracleSessionHistory.Server.Api;

public sealed record TargetSummary(
    int Id, string Name, string Provider, bool Enabled, int Connections, int Apps, int Terminals, int History,
    int DbLinkSuspects, int Jobs, DateTime? LastPollAt, DateTime? LastSuccessAt, string? LastError, int ConsecutiveFailures);

public sealed record SummaryResult(
    int Connections, int Apps, int Terminals, int History, int TodayLogons, int DbLinkSuspects, int Jobs,
    IReadOnlyList<TargetSummary> Targets, IReadOnlyList<NameCount> TopApps, IReadOnlyList<NameCount> TopMachines);

public sealed record NameCount(string Name, int Count);

/// <summary>
/// 接続アプリ表の子の行（端末 × アプリ）。DB Link 経由で接続元を特定できたセッションは、接続元の端末・アプリの行にし、
/// 経由した DB サーバー（<paramref name="LinkVia"/>）の親の下に置く。
/// </summary>
/// <param name="LinkVia">DB Link 経由の行の場合、経由した DB サーバー（ORACLE.EXE の端末）。直接の接続は null</param>
public sealed record ConnectionCountRow(
    string? Machine, string AppName, string? AppDetail, string? Owner, int Connected, DateTime LastSeenAt,
    int Today, int D1, int D2, int D3, int D4, int D5, int D6, int Month1, int Month2, string? LinkVia = null);

/// <summary>
/// 接続アプリ表の親の行。端末（Kind=machine）または DB Link 経由（Kind=dblink、Machine は経由した DB サーバー）。
/// 端末の行は、このシステムの履歴がまだ無い期間を監査証跡（DBA_AUDIT_SESSION）のログオン件数で補う（<paramref name="AuditPeriods"/> の列）。
/// </summary>
/// <param name="AuditPeriods">監査証跡の件数を使った列（today・d1〜d6・month1・month2）</param>
public sealed record ConnectionCountGroup(
    string Kind, string? Machine, int Connected, DateTime? LastSeenAt,
    int Today, int D1, int D2, int D3, int D4, int D5, int D6, int Month1, int Month2,
    IReadOnlyList<string> AuditPeriods, IReadOnlyList<ConnectionCountRow> Children);

/// <summary>監視対象ごとの監査証跡の取得状況（接続アプリ表の注記用）。From〜LastDay（両端を含む）を監査の件数で補う</summary>
public sealed record AuditCoverage(string TargetName, DateTime? From, DateTime? LastDay, DateTime? FetchedAt, string? Error);

public static class ConnectionCountKinds
{
    public const string Machine = "machine";
    public const string DbLink = "dblink";
}

/// <summary>サマリー・接続数表の API</summary>
public static class ReportEndpoints
{
    public static void MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api");

        g.MapGet("/summary", async (int? targetId, AppDbContext db) =>
        {
            // Oracle 内部プロセス（ジョブ・パラレル実行等）は数えない
            var visible = db.Sessions.AsNoTracking().WhereVisible();
            var sessions = visible;
            if (targetId is { } tid) sessions = sessions.Where(s => s.TargetId == tid);
            var connected = sessions.Where(s => s.IsConnected);
            var today = DateTime.Today;

            var perTarget = await db.Targets.AsNoTracking()
                .Where(t => targetId == null || t.Id == targetId)
                .OrderBy(t => t.Id)
                .Select(t => new TargetSummary(
                    t.Id, t.Name, t.Provider, t.Enabled,
                    visible.Count(s => s.TargetId == t.Id && s.IsConnected),
                    visible.Where(s => s.TargetId == t.Id && s.IsConnected).Select(s => s.LinkOriginApp ?? s.AppName).Distinct().Count(),
                    visible.Where(s => s.TargetId == t.Id && s.IsConnected)
                        .Select(s => s.LinkOriginApp != null ? s.LinkOriginMachine : s.Machine).Distinct().Count(),
                    visible.Count(s => s.TargetId == t.Id && !s.IsConnected),
                    db.Sessions.Count(s => s.TargetId == t.Id && s.IsConnected && s.ProcessKind == ProcessKinds.DbLinkSuspect),
                    db.Sessions.Count(s => s.TargetId == t.Id && s.IsConnected && s.JobName != null),
                    t.LastPollAt, t.LastSuccessAt, t.LastError, t.ConsecutiveFailures))
                .ToListAsync();

            // DB Link 経由で接続元を特定できたセッションは、接続元の端末・アプリとして数える（接続アプリ表と同じ）
            var topApps = (await connected.GroupBy(s => s.LinkOriginApp ?? s.AppName)
                .Select(x => new { x.Key, Count = x.Count() })
                .OrderByDescending(x => x.Count).Take(10).ToListAsync())
                .Select(x => new NameCount(x.Key, x.Count)).ToList();
            var topMachines = (await connected.GroupBy(s => s.LinkOriginApp != null ? s.LinkOriginMachine : s.Machine)
                .Select(x => new { x.Key, Count = x.Count() })
                .OrderByDescending(x => x.Count).Take(10).ToListAsync())
                // 空の端末名も「(不明)」にまとめる（一覧へ遷移したとき完全一致で端末なしの行を開けるように）
                .GroupBy(x => string.IsNullOrEmpty(x.Key) ? SessionEndpoints.UnknownMachine : x.Key)
                .Select(x => new NameCount(x.Key, x.Sum(y => y.Count))).OrderByDescending(x => x.Count).ToList();

            return new SummaryResult(
                await connected.CountAsync(),
                await connected.Select(s => s.LinkOriginApp ?? s.AppName).Distinct().CountAsync(),
                await connected.Select(s => s.LinkOriginApp != null ? s.LinkOriginMachine : s.Machine).Distinct().CountAsync(),
                await sessions.CountAsync(s => !s.IsConnected),
                await sessions.CountAsync(s => s.LogonTime >= today),
                await connected.CountAsync(s => s.ProcessKind == ProcessKinds.DbLinkSuspect),
                await connected.CountAsync(s => s.JobName != null),
                perTarget, topApps, topMachines);
        });

        // 接続アプリ表：実行端末 × アプリごとの日単位の接続回数（ログオン時刻で集計）
        g.MapGet("/connection-counts", async (int? targetId, string? q, AppDbContext db, CancellationToken ct) =>
        {
            var b = Buckets.Create(DateTime.Today);
            var (groups, audit) = await QueryConnectionCountGroupsAsync(db, targetId, q, b.Today, ct);
            return new
            {
                periods = new
                {
                    today = b.Today, d1 = b.D1, d2 = b.D2, d3 = b.D3, d4 = b.D4, d5 = b.D5, d6 = b.D6,
                    month1Start = b.Month1Start, month2Start = b.Month2Start,
                },
                groups,
                audit,
            };
        });
    }

    /// <summary>
    /// 接続アプリ表（親：端末・DB Link 経由、子：アプリ）。Oracle 内部プロセスは除く。「本日〜1ヶ月」の多い順。
    /// 端末の行の件数は、監視対象ごとに監査証跡を使う期間（AuditFrom〜AuditTo）は監査の件数、それ以外は履歴の件数の合計。
    /// 監査の件数はアプリを区別できないため子の行には入れない（絞り込み時は端末名が一致する場合だけ補う）。
    /// </summary>
    internal static async Task<(List<ConnectionCountGroup> Groups, List<AuditCoverage> Audit)> QueryConnectionCountGroupsAsync(
        AppDbContext db, int? targetId, string? q, DateTime today, CancellationToken ct)
    {
        var b = Buckets.Create(today);
        var children = await ConnectionCountsQuery(db, targetId, q, today).ToListAsync(ct);
        var historyDays = await MachineDaysQuery(db, targetId, q, today).ToListAsync(ct);

        var targets = await db.Targets.AsNoTracking()
            .Where(t => targetId == null || t.Id == targetId)
            .Select(t => new { t.Id, t.Name, t.AuditFrom, t.AuditTo, t.AuditFetchedAt, t.AuditError })
            .ToListAsync(ct);
        var coverage = targets.Where(t => t.AuditFrom != null && t.AuditTo != null)
            .ToDictionary(t => t.Id, t => (From: t.AuditFrom!.Value, To: t.AuditTo!.Value));
        var coveredIds = coverage.Keys.ToList();
        var auditDays = await db.AuditLogonCounts.AsNoTracking()
            .Where(a => coveredIds.Contains(a.TargetId) && a.Day >= b.Month2Start)
            .Select(a => new { a.TargetId, a.Day, a.Machine, a.Count })
            .ToListAsync(ct);
        var term = q?.Trim();
        if (!string.IsNullOrEmpty(term))
            auditDays = auditDays.Where(a => a.Machine.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();

        bool Covered(int tid, DateTime day) => coverage.TryGetValue(tid, out var c) && day >= c.From && day < c.To;

        var groups = new List<ConnectionCountGroup>();
        // 端末（直接の接続）：端末名は大文字小文字を区別せずにまとめる（監査証跡の USERHOST と突き合わせるため）
        var direct = children.Where(c => c.LinkVia is null).ToList();
        var keys = direct.Select(c => MachineKey(c.Machine))
            .Concat(historyDays.Select(h => MachineKey(h.Machine)))
            .Concat(auditDays.Select(a => MachineKey(a.Machine)))
            .Distinct();
        foreach (var key in keys)
        {
            var rows = direct.Where(c => MachineKey(c.Machine) == key).ToList();
            var history = historyDays.Where(h => MachineKey(h.Machine) == key).ToList();
            var audits = auditDays.Where(a => MachineKey(a.Machine) == key).ToList();
            var days = history.Where(h => !Covered(h.TargetId, h.Day)).Select(h => (h.Day, h.Count))
                .Concat(audits.Where(a => Covered(a.TargetId, a.Day)).Select(a => (a.Day, a.Count)));
            var v = b.Sum(days);
            // 監査の件数を使った列：この端末が現れる監視対象のうち、監査を使う期間に重なる列
            var tids = history.Select(h => h.TargetId).Concat(audits.Select(a => a.TargetId)).Distinct()
                .Where(coverage.ContainsKey).ToList();
            var auditPeriods = b.Periods.Where(p => tids.Any(t => p.Start < coverage[t].To && coverage[t].From < p.End))
                .Select(p => p.Key).ToList();
            var machine = rows.Select(r => r.Machine).FirstOrDefault(m => !string.IsNullOrEmpty(m))
                          ?? history.Select(h => h.Machine).FirstOrDefault(m => !string.IsNullOrEmpty(m))
                          ?? audits.Select(a => a.Machine).FirstOrDefault(m => !string.IsNullOrEmpty(m));
            groups.Add(new ConnectionCountGroup(ConnectionCountKinds.Machine, machine,
                rows.Sum(r => r.Connected), rows.Count > 0 ? rows.Max(r => r.LastSeenAt) : null,
                v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], auditPeriods, Sort(rows)));
        }

        // DB Link 経由：経由した DB サーバーごと（子は接続元の端末・アプリ）
        foreach (var g in children.Where(c => c.LinkVia is not null).GroupBy(c => MachineKey(c.LinkVia)))
        {
            var rows = g.ToList();
            groups.Add(new ConnectionCountGroup(ConnectionCountKinds.DbLink, rows[0].LinkVia,
                rows.Sum(r => r.Connected), rows.Max(r => r.LastSeenAt),
                rows.Sum(r => r.Today), rows.Sum(r => r.D1), rows.Sum(r => r.D2), rows.Sum(r => r.D3), rows.Sum(r => r.D4),
                rows.Sum(r => r.D5), rows.Sum(r => r.D6), rows.Sum(r => r.Month1), rows.Sum(r => r.Month2), [], Sort(rows)));
        }

        var audit = targets.Where(t => t.AuditFetchedAt != null)
            .Select(t => new AuditCoverage(t.Name, t.AuditFrom, t.AuditTo?.AddDays(-1), t.AuditFetchedAt, t.AuditError)).ToList();
        return (groups.OrderByDescending(g => g.Month1).ThenByDescending(g => g.Month2)
            .ThenBy(g => g.Kind == ConnectionCountKinds.DbLink).ThenBy(g => g.Machine).ToList(), audit);

        static List<ConnectionCountRow> Sort(IEnumerable<ConnectionCountRow> rows) =>
            rows.OrderByDescending(r => r.Month1).ThenByDescending(r => r.Month2).ThenBy(r => r.Machine).ThenBy(r => r.AppName).ToList();
    }

    private static string MachineKey(string? machine) => (machine?.Trim() ?? "").ToUpperInvariant();

    private static IQueryable<TrackedSession> CountTarget(AppDbContext db, int? targetId, string? q, DateTime today)
    {
        var b = Buckets.Create(today);
        var sessions = db.Sessions.AsNoTracking().WhereVisible().Where(s => s.LogonTime >= b.Month2Start);
        if (targetId is { } tid) sessions = sessions.Where(s => s.TargetId == tid);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var p = QueryHelpers.Contains(q.Trim());
            sessions = sessions.Where(s => EF.Functions.Like(s.Machine, p, "\\") || EF.Functions.Like(s.AppName, p, "\\")
                                           || EF.Functions.Like(s.AppDetail, p, "\\") || EF.Functions.Like(s.Program, p, "\\")
                                           || EF.Functions.Like(s.LinkOriginMachine, p, "\\") || EF.Functions.Like(s.LinkOriginApp, p, "\\"));
        }
        return sessions;
    }

    /// <summary>
    /// 接続アプリ表の子の行の SQL。DB Link 経由（ORACLE.EXE）で接続元を特定できたセッションは、接続元の端末・アプリでまとめ、
    /// 経由した DB サーバーを LinkVia に入れる（一覧の完全一致の検索 <see cref="SessionEndpoints.Filter"/> と同じまとめ方）。
    /// </summary>
    internal static IQueryable<ConnectionCountRow> ConnectionCountsQuery(AppDbContext db, int? targetId, string? q, DateTime today)
    {
        var b = Buckets.Create(today);
        return CountTarget(db, targetId, q, today)
            .GroupBy(s => new
            {
                LinkVia = s.LinkOriginApp != null ? s.Machine : null,
                Machine = s.LinkOriginApp != null ? s.LinkOriginMachine : s.Machine,
                AppName = s.LinkOriginApp ?? s.AppName,
            })
            .Select(gr => new ConnectionCountRow(
                gr.Key.Machine, gr.Key.AppName,
                // 代表値（同じアプリでも補足・実行ユーザが複数ある場合は 1 つだけ表示する）。DB Link 経由の行は DB サーバー側の値のため使わない
                gr.Max(s => s.LinkOriginApp == null ? s.AppDetail : null), gr.Max(s => s.LinkOriginApp == null ? s.ProcOwner ?? s.OsUser : null),
                gr.Sum(s => s.IsConnected ? 1 : 0), gr.Max(s => s.LastSeenAt),
                gr.Sum(s => s.LogonTime >= b.Today ? 1 : 0),
                gr.Sum(s => s.LogonTime >= b.D1 && s.LogonTime < b.Today ? 1 : 0),
                gr.Sum(s => s.LogonTime >= b.D2 && s.LogonTime < b.D1 ? 1 : 0),
                gr.Sum(s => s.LogonTime >= b.D3 && s.LogonTime < b.D2 ? 1 : 0),
                gr.Sum(s => s.LogonTime >= b.D4 && s.LogonTime < b.D3 ? 1 : 0),
                gr.Sum(s => s.LogonTime >= b.D5 && s.LogonTime < b.D4 ? 1 : 0),
                gr.Sum(s => s.LogonTime >= b.D6 && s.LogonTime < b.D5 ? 1 : 0),
                gr.Sum(s => s.LogonTime >= b.Month1Start ? 1 : 0),
                gr.Sum(s => s.LogonTime >= b.Month2Start && s.LogonTime < b.Month1Start ? 1 : 0),
                gr.Key.LinkVia));
    }

    /// <summary>端末の行の件数（監査証跡と合わせるため、監視対象 × 端末 × 日の直接の接続のログオン数）</summary>
    internal static IQueryable<MachineDay> MachineDaysQuery(AppDbContext db, int? targetId, string? q, DateTime today) =>
        CountTarget(db, targetId, q, today)
            .Where(s => s.LinkOriginApp == null)
            .GroupBy(s => new { s.TargetId, s.Machine, Day = s.LogonTime.Date })
            .Select(g => new MachineDay(g.Key.TargetId, g.Key.Machine, g.Key.Day, g.Count()));

    internal sealed record MachineDay(int TargetId, string? Machine, DateTime Day, int Count);

    /// <summary>接続数表の期間区分</summary>
    internal sealed record Buckets(DateTime Today, DateTime D1, DateTime D2, DateTime D3, DateTime D4, DateTime D5, DateTime D6,
        DateTime Month1Start, DateTime Month2Start)
    {
        public static Buckets Create(DateTime today) => new(
            today, today.AddDays(-1), today.AddDays(-2), today.AddDays(-3), today.AddDays(-4), today.AddDays(-5), today.AddDays(-6),
            // 本日〜1ヶ月：1ヶ月前の同日以降（本日を含む）／1ヶ月〜2ヶ月：その前の1ヶ月
            today.AddMonths(-1), today.AddMonths(-2));

        /// <summary>列（画面の列のキー・期間）。終了は含まない</summary>
        public IReadOnlyList<(string Key, DateTime Start, DateTime End)> Periods =>
        [
            ("today", Today, Today.AddDays(1)), ("d1", D1, Today), ("d2", D2, D1), ("d3", D3, D2), ("d4", D4, D3),
            ("d5", D5, D4), ("d6", D6, D5), ("month1", Month1Start, Today.AddDays(1)), ("month2", Month2Start, Month1Start),
        ];

        /// <summary>日ごとの件数を列ごとに合計する（<see cref="Periods"/> の順）</summary>
        public int[] Sum(IEnumerable<(DateTime Day, int Count)> days)
        {
            var periods = Periods;
            var v = new int[periods.Count];
            foreach (var (day, count) in days)
                for (var i = 0; i < periods.Count; i++)
                    if (day >= periods[i].Start && day < periods[i].End) v[i] += count;
            return v;
        }
    }
}
