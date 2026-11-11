using Microsoft.EntityFrameworkCore;
using OracleSessionHistory.Core.Data;
using OracleSessionHistory.Core.Entities;
using OracleSessionHistory.Core.Processes;
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
/// 接続アプリ表の 1 行（木構造）。端末 → 実行ファイル → IIS のプール・sqlplus の実行元、
/// ORACLE.EXE → DB Link（接続元 DB）→ 接続元の端末 → 接続元のアプリ（実行ファイル → プール・実行元）。
/// </summary>
/// <param name="Kind">machine（端末。DB Link の下は接続元の端末）・exe（実行ファイル）・app（プール・実行元等）・dblink（DB Link 接続元）・other（区別できないもの）</param>
/// <param name="Detail">補足（IIS のサイト・フォルダ・DB Link の接続元の端末等）</param>
/// <param name="AuditPeriods">監査証跡の件数を使った列（端末の行のみ。today・d1〜d6・month1・month2）</param>
/// <param name="Open">クリックで開くセッション履歴の検索条件（null は開かない）</param>
/// <param name="Owners">実行ユーザ（ログオンの多い順。DB Link 経由は接続元の実行ユーザ）</param>
/// <param name="Key">行のキー（対応担当者の保存に使う。<see cref="ConnectionTreeKeys"/>）</param>
/// <param name="Note">画面から設定した日本語名・メモ・対応担当者（未設定は null）</param>
public sealed record ConnectionCountNode(
    string Kind, string Label, string? Detail, IReadOnlyList<string> Owners, int Connected, DateTime? LastSeenAt,
    int Today, int D1, int D2, int D3, int D4, int D5, int D6, int Month1, int Month2,
    IReadOnlyList<string> AuditPeriods, IReadOnlyDictionary<string, string>? Open, IReadOnlyList<ConnectionCountNode> Children,
    string Key = "", NodeNoteDto? Note = null);

/// <summary>監視対象ごとの監査証跡の取得状況（接続アプリ表の注記用）。From〜LastDay（両端を含む）の端末の件数は監査の件数</summary>
public sealed record AuditCoverage(string TargetName, DateTime? From, DateTime? LastDay, DateTime? FetchedAt, string? Error);

public static class ConnectionCountKinds
{
    public const string Machine = "machine";
    public const string Exe = "exe";
    public const string App = "app";
    public const string DbLink = "dblink";
    public const string Other = "other";
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
        // assignee：担当者（AD のアカウント）で絞り込む（ConnectionTreeKeys.Unassigned は担当者が決まっていない行）
        g.MapGet("/connection-counts", async (int? targetId, string? q, string? assignee, AppDbContext db, CancellationToken ct) =>
        {
            var b = Buckets.Create(DateTime.Today);
            var (tree, audit) = await QueryConnectionTreeAsync(db, targetId, q, b.Today, ct);
            var notes = (await db.AppNodeNotes.AsNoTracking().Include(n => n.Assignees).ToListAsync(ct))
                .ToDictionary(n => n.NodeKey, NodeNoteDto.From);
            var nodes = ConnectionTreeKeys.FilterByAssignee(ConnectionTreeKeys.Apply(tree, notes), assignee);
            return new
            {
                periods = new
                {
                    today = b.Today, d1 = b.D1, d2 = b.D2, d3 = b.D3, d4 = b.D4, d5 = b.D5, d6 = b.D6,
                    month1Start = b.Month1Start, month2Start = b.Month2Start,
                },
                nodes,
                audit,
            };
        });
    }

    /// <summary>集計の最小単位（端末 × PROGRAM × アプリ × DB Link の接続元）</summary>
    internal sealed record ConnectionCountRaw(
        string? Machine, string? Program, string AppName, string? LinkSource, string? OriginMachine, string? OriginApp,
        string? AppDetail, string? Owner, int Connected, DateTime LastSeenAt,
        int Today, int D1, int D2, int D3, int D4, int D5, int D6, int Month1, int Month2)
    {
        public bool Linked => OriginApp is not null;
        public int[] Counts => [Today, D1, D2, D3, D4, D5, D6, Month1, Month2];
    }

    /// <summary>端末の行の件数（監査証跡と合わせるため、監視対象 × 端末 × 日のログオン数）</summary>
    internal sealed record MachineDay(int TargetId, string? Machine, DateTime Day, int Count);

    /// <summary>
    /// 接続アプリ表（木構造）。Oracle 内部プロセスは除く。各段は「本日〜1ヶ月」の多い順。
    /// DB Link 経由（ORACLE.EXE）で接続元を特定できたセッションは、DB サーバーの端末の ORACLE.EXE の下に、
    /// DB Link 接続元 → 接続元のアプリの段で置く。
    /// 端末の行の件数は監査証跡（DBA_AUDIT_SESSION）のログオン件数。監査の件数を取得できていない監視対象・期間
    /// （AuditFrom〜AuditTo の外）だけ履歴の件数を使う。監査の件数はアプリを区別できないため下の段は履歴の件数
    /// （絞り込み時は端末名が一致する場合だけ監査の件数を使い、それ以外は絞り込んだ履歴の件数）。
    /// </summary>
    internal static async Task<(List<ConnectionCountNode> Nodes, List<AuditCoverage> Audit)> QueryConnectionTreeAsync(
        AppDbContext db, int? targetId, string? q, DateTime today, CancellationToken ct)
    {
        var b = Buckets.Create(today);
        var raws = await ConnectionCountsQuery(db, targetId, q, today).ToListAsync(ct);
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

        var nodes = new List<ConnectionCountNode>();
        // 端末名は大文字小文字を区別せずにまとめる（監査証跡の USERHOST と突き合わせるため）
        var keys = raws.Select(r => MachineKey(r.Machine))
            .Concat(historyDays.Select(h => MachineKey(h.Machine)))
            .Concat(auditDays.Select(a => MachineKey(a.Machine)))
            .Distinct();
        foreach (var key in keys)
        {
            var rows = raws.Where(r => MachineKey(r.Machine) == key).ToList();
            var history = historyDays.Where(h => MachineKey(h.Machine) == key).ToList();
            var audits = auditDays.Where(a => MachineKey(a.Machine) == key).ToList();
            var machine = rows.Select(r => r.Machine).FirstOrDefault(m => !string.IsNullOrEmpty(m))
                          ?? history.Select(h => h.Machine).FirstOrDefault(m => !string.IsNullOrEmpty(m))
                          ?? audits.Select(a => a.Machine).FirstOrDefault(m => !string.IsNullOrEmpty(m));
            // 端末名で絞り込んでいない（アプリ名等で一致した）端末は、端末全体の監査の件数ではなく絞り込んだ履歴の件数
            var useAudit = string.IsNullOrEmpty(term) || (machine ?? "").Contains(term, StringComparison.OrdinalIgnoreCase);
            var days = history.Where(h => !useAudit || !Covered(h.TargetId, h.Day)).Select(h => (h.Day, h.Count))
                .Concat(audits.Where(a => useAudit && Covered(a.TargetId, a.Day)).Select(a => (a.Day, a.Count)));
            var v = b.Sum(days);
            // 監査の件数を使った列：この端末が現れる監視対象のうち、監査の件数がある期間に重なる列
            var tids = useAudit
                ? history.Select(h => h.TargetId).Concat(audits.Select(a => a.TargetId)).Distinct().Where(coverage.ContainsKey).ToList()
                : [];
            var auditPeriods = b.Periods.Where(p => tids.Any(t => p.Start < coverage[t].To && coverage[t].From < p.End))
                .Select(p => p.Key).ToList();
            var host = string.IsNullOrEmpty(machine) ? SessionEndpoints.UnknownMachine : machine;
            nodes.Add(new ConnectionCountNode(ConnectionCountKinds.Machine, machine ?? SessionEndpoints.UnknownMachine, null, OwnersOf(rows),
                rows.Sum(r => r.Connected), rows.Count > 0 ? rows.Max(r => r.LastSeenAt) : null,
                v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], auditPeriods,
                new Dictionary<string, string> { ["host"] = host }, ExeNodes(rows, host)));
        }

        var audit = targets.Where(t => t.AuditFetchedAt != null)
            .Select(t => new AuditCoverage(t.Name, t.AuditFrom, t.AuditTo?.AddDays(-1), t.AuditFetchedAt, t.AuditError)).ToList();
        return (Sort(nodes), audit);
    }

    /// <summary>端末の下：実行ファイルごと（下にプール・実行元、ORACLE.EXE は DB Link 接続元）</summary>
    private static List<ConnectionCountNode> ExeNodes(List<ConnectionCountRaw> rows, string host)
    {
        var list = new List<ConnectionCountNode>();
        foreach (var g in rows.GroupBy(r => AppIdentifier.SplitForTree(r.Program, r.AppName).Exe, StringComparer.OrdinalIgnoreCase))
        {
            var exe = g.Key;
            var all = g.ToList();
            var direct = all.Where(r => !r.Linked).ToList();
            var children = new List<ConnectionCountNode>();
            // プール・実行元・dll 等
            foreach (var s in direct.Select(r => (Row: r, Sub: AppIdentifier.SplitForTree(r.Program, r.AppName).Sub))
                         .Where(x => x.Sub is not null).GroupBy(x => x.Sub!, StringComparer.OrdinalIgnoreCase))
            {
                var sub = s.Select(x => x.Row).ToList();
                children.Add(Leaf(ConnectionCountKinds.App, s.Key, sub,
                    Open(("host", host), ("via", SessionEndpoints.DirectVia), ("app", sub[0].AppName), ("exact", "true"))));
            }
            // DB Link 経由：DB Link 接続元ごと
            foreach (var l in all.Where(r => r.Linked).GroupBy(r => r.LinkSource ?? SessionEndpoints.UnknownMachine))
            {
                var linked = l.ToList();
                var open = Open(("host", host), ("via", host), ("linkSource", l.Key));
                children.Add(Node(ConnectionCountKinds.DbLink, l.Key, null, linked, open, OriginMachineNodes(linked, open)));
            }
            // 区別できないもの（プール・実行元・接続元を特定できていない）。他に下の段がある場合だけ別の行にする
            var rest = direct.Where(r => AppIdentifier.SplitForTree(r.Program, r.AppName).Sub is null).ToList();
            if (children.Count > 0 && rest.Count > 0)
                children.Add(Leaf(ConnectionCountKinds.Other, RestLabel(exe), rest,
                    Open(("host", host), ("via", SessionEndpoints.DirectVia), ("app", rest[0].AppName), ("exact", "true"))));

            var exeOpen = all.Any(r => r.Linked) ? Open(("host", host), ("exe", exe))
                : Open(("host", host), ("via", SessionEndpoints.DirectVia), ("exe", exe));
            list.Add(children.Count == 0
                ? Leaf(ConnectionCountKinds.Exe, exe, all, exeOpen)
                : Node(ConnectionCountKinds.Exe, exe, null, all, exeOpen, Sort(children)));
        }
        return Sort(list);
    }

    /// <summary>DB Link 接続元の下：接続元の端末ごと（その下は端末の下と同じく実行ファイル → プール・実行元）</summary>
    private static List<ConnectionCountNode> OriginMachineNodes(List<ConnectionCountRaw> rows, IReadOnlyDictionary<string, string> linkOpen)
    {
        var list = new List<ConnectionCountNode>();
        foreach (var g in rows.GroupBy(r => MachineKey(r.OriginMachine)))
        {
            var all = g.ToList();
            var machine = all.Select(r => r.OriginMachine).FirstOrDefault(m => !string.IsNullOrEmpty(m));
            var origin = string.IsNullOrEmpty(machine) ? SessionEndpoints.UnknownMachine : machine;
            var open = With(linkOpen, ("origin", origin));
            list.Add(Node(ConnectionCountKinds.Machine, origin, null, all, open, OriginExeNodes(all, open)));
        }
        return Sort(list);
    }

    /// <summary>接続元の端末の下：接続元のアプリを、端末の下と同じく実行ファイル → プール・実行元に分ける</summary>
    private static List<ConnectionCountNode> OriginExeNodes(List<ConnectionCountRaw> rows, IReadOnlyDictionary<string, string> originOpen)
    {
        var list = new List<ConnectionCountNode>();
        foreach (var g in rows.GroupBy(r => AppIdentifier.SplitForTree(null, r.OriginApp!).Exe, StringComparer.OrdinalIgnoreCase))
        {
            var all = g.ToList();
            var children = new List<ConnectionCountNode>();
            foreach (var s in all.Select(r => (Row: r, Sub: AppIdentifier.SplitForTree(null, r.OriginApp!).Sub))
                         .Where(x => x.Sub is not null).GroupBy(x => x.Sub!, StringComparer.OrdinalIgnoreCase))
            {
                var sub = s.Select(x => x.Row).ToList();
                children.Add(Node(ConnectionCountKinds.App, s.Key, null, sub, With(originOpen, ("app", sub[0].OriginApp!), ("exact", "true")), []));
            }
            var rest = all.Where(r => AppIdentifier.SplitForTree(null, r.OriginApp!).Sub is null).ToList();
            if (children.Count > 0 && rest.Count > 0)
                children.Add(Node(ConnectionCountKinds.Other, RestLabel(g.Key), null, rest,
                    With(originOpen, ("app", rest[0].OriginApp!), ("exact", "true")), []));
            // 下の段がある実行ファイルは、w3wp.exe なら「IIS: 」、それ以外は実行ファイル名の部分一致で接続元のアプリを探す
            var open = children.Count == 0 ? With(originOpen, ("app", all[0].OriginApp!), ("exact", "true"))
                : With(originOpen, ("app", g.Key.Equals("w3wp.exe", StringComparison.OrdinalIgnoreCase) ? "IIS: " : g.Key));
            list.Add(Node(ConnectionCountKinds.Exe, g.Key, null, all, open, Sort(children)));
        }
        return Sort(list);
    }

    private static IReadOnlyDictionary<string, string> With(IReadOnlyDictionary<string, string> open, params (string Key, string Value)[] extra) =>
        new Dictionary<string, string>(open.Concat(extra.Select(e => KeyValuePair.Create(e.Key, e.Value))));

    private static string RestLabel(string exe) => exe.ToLowerInvariant() switch
    {
        "w3wp.exe" => "(プール不明)",
        "oracle.exe" => "(接続元不明)",
        _ when AppIdentifier.LaunchedTools.Contains(exe) => "(実行元不明)",
        _ => "(区別なし)",
    };

    /// <summary>下の段の無い行（補足は代表値）</summary>
    private static ConnectionCountNode Leaf(string kind, string label, List<ConnectionCountRaw> rows, IReadOnlyDictionary<string, string>? open) =>
        Node(kind, label, rows.Select(r => r.AppDetail).FirstOrDefault(d => d is not null), rows, open, []);

    /// <summary>実行ユーザ（大文字小文字の違いはまとめる）。ログオンの多い順</summary>
    private static List<string> OwnersOf(IEnumerable<ConnectionCountRaw> rows) =>
        rows.Where(r => !string.IsNullOrWhiteSpace(r.Owner))
            .GroupBy(r => r.Owner!.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Sum(r => r.Month1 + r.Month2)).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Key).ToList();

    private static ConnectionCountNode Node(string kind, string label, string? detail, List<ConnectionCountRaw> rows,
        IReadOnlyDictionary<string, string>? open, IReadOnlyList<ConnectionCountNode> children)
    {
        var v = new int[9];
        foreach (var r in rows)
        {
            var c = r.Counts;
            for (var i = 0; i < v.Length; i++) v[i] += c[i];
        }
        return new ConnectionCountNode(kind, label, detail, OwnersOf(rows), rows.Sum(r => r.Connected), rows.Max(r => r.LastSeenAt),
            v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], [], open, children);
    }

    private static IReadOnlyDictionary<string, string> Open(params (string Key, string Value)[] items) =>
        items.ToDictionary(i => i.Key, i => i.Value);

    /// <summary>「本日〜1ヶ月」の多い順。区別できないものは最後</summary>
    private static List<ConnectionCountNode> Sort(IEnumerable<ConnectionCountNode> nodes) =>
        nodes.OrderBy(n => n.Kind == ConnectionCountKinds.Other).ThenByDescending(n => n.Month1).ThenByDescending(n => n.Month2)
            .ThenBy(n => n.Label, StringComparer.OrdinalIgnoreCase).ToList();

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
                                           || EF.Functions.Like(s.LinkOriginMachine, p, "\\") || EF.Functions.Like(s.LinkOriginApp, p, "\\")
                                           || EF.Functions.Like(s.LinkOriginSource, p, "\\"));
        }
        return sessions;
    }

    /// <summary>接続アプリ表の集計の SQL（端末 × PROGRAM × アプリ × DB Link の接続元）。木構造はメモリ上で組み立てる</summary>
    internal static IQueryable<ConnectionCountRaw> ConnectionCountsQuery(AppDbContext db, int? targetId, string? q, DateTime today)
    {
        var b = Buckets.Create(today);
        return CountTarget(db, targetId, q, today)
            .GroupBy(s => new
            {
                s.Machine,
                s.Program,
                s.AppName,
                LinkSource = s.LinkOriginApp != null ? s.LinkOriginSource : null,
                OriginMachine = s.LinkOriginApp != null ? s.LinkOriginMachine : null,
                s.LinkOriginApp,
                // 実行ユーザ。DB Link 経由は接続元の実行ユーザ（DB サーバー側の OSUSER ではない）
                Owner = s.LinkOriginApp != null ? s.LinkOriginUser : s.ProcOwner ?? s.OsUser,
            })
            .Select(gr => new ConnectionCountRaw(
                gr.Key.Machine, gr.Key.Program, gr.Key.AppName, gr.Key.LinkSource, gr.Key.OriginMachine, gr.Key.LinkOriginApp,
                // 補足は代表値（同じアプリでも補足が複数ある場合は 1 つだけ表示する）
                gr.Max(s => s.AppDetail), gr.Key.Owner,
                gr.Sum(s => s.IsConnected ? 1 : 0), gr.Max(s => s.LastSeenAt),
                gr.Sum(s => s.LogonTime >= b.Today ? 1 : 0),
                gr.Sum(s => s.LogonTime >= b.D1 && s.LogonTime < b.Today ? 1 : 0),
                gr.Sum(s => s.LogonTime >= b.D2 && s.LogonTime < b.D1 ? 1 : 0),
                gr.Sum(s => s.LogonTime >= b.D3 && s.LogonTime < b.D2 ? 1 : 0),
                gr.Sum(s => s.LogonTime >= b.D4 && s.LogonTime < b.D3 ? 1 : 0),
                gr.Sum(s => s.LogonTime >= b.D5 && s.LogonTime < b.D4 ? 1 : 0),
                gr.Sum(s => s.LogonTime >= b.D6 && s.LogonTime < b.D5 ? 1 : 0),
                gr.Sum(s => s.LogonTime >= b.Month1Start ? 1 : 0),
                gr.Sum(s => s.LogonTime >= b.Month2Start && s.LogonTime < b.Month1Start ? 1 : 0)));
    }

    /// <summary>端末の行の件数（監視対象 × 端末 × 日。DB Link 経由も DB サーバーの端末として数える）</summary>
    internal static IQueryable<MachineDay> MachineDaysQuery(AppDbContext db, int? targetId, string? q, DateTime today) =>
        CountTarget(db, targetId, q, today)
            .GroupBy(s => new { s.TargetId, s.Machine, Day = s.LogonTime.Date })
            .Select(g => new MachineDay(g.Key.TargetId, g.Key.Machine, g.Key.Day, g.Count()));

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
