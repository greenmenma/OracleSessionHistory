using Microsoft.EntityFrameworkCore;
using OpenDbSession.Core.Data;
using OpenDbSession.Core.Entities;
using OpenDbSession.Server.Support;

namespace OpenDbSession.Server.Api;

public sealed record TargetSummary(
    int Id, string Name, string Provider, bool Enabled, int Connections, int Apps, int Terminals, int History,
    int DbLinkSuspects, int Jobs, DateTime? LastPollAt, DateTime? LastSuccessAt, string? LastError, int ConsecutiveFailures);

public sealed record SummaryResult(
    int Connections, int Apps, int Terminals, int History, int TodayLogons, int DbLinkSuspects, int Jobs,
    IReadOnlyList<TargetSummary> Targets, IReadOnlyList<NameCount> TopPrograms, IReadOnlyList<NameCount> TopMachines);

public sealed record NameCount(string Name, int Count);

public sealed record ConnectionCountRow(
    string? Machine, string? Program, int Today, int D1, int D2, int D3, int D4, int D5, int D6, int Month1, int Month2);

/// <summary>サマリー・接続数表の API</summary>
public static class ReportEndpoints
{
    public static void MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api");

        g.MapGet("/summary", async (int? targetId, AppDbContext db) =>
        {
            var sessions = db.Sessions.AsNoTracking();
            if (targetId is { } tid) sessions = sessions.Where(s => s.TargetId == tid);
            var connected = sessions.Where(s => s.IsConnected);
            var today = DateTime.Today;

            var perTarget = await db.Targets.AsNoTracking()
                .Where(t => targetId == null || t.Id == targetId)
                .OrderBy(t => t.Id)
                .Select(t => new TargetSummary(
                    t.Id, t.Name, t.Provider, t.Enabled,
                    db.Sessions.Count(s => s.TargetId == t.Id && s.IsConnected),
                    db.Sessions.Where(s => s.TargetId == t.Id && s.IsConnected).Select(s => s.Program).Distinct().Count(),
                    db.Sessions.Where(s => s.TargetId == t.Id && s.IsConnected).Select(s => s.Machine).Distinct().Count(),
                    db.Sessions.Count(s => s.TargetId == t.Id && !s.IsConnected),
                    db.Sessions.Count(s => s.TargetId == t.Id && s.IsConnected && s.ProcessKind == ProcessKinds.DbLinkSuspect),
                    db.Sessions.Count(s => s.TargetId == t.Id && s.IsConnected && s.JobName != null),
                    t.LastPollAt, t.LastSuccessAt, t.LastError, t.ConsecutiveFailures))
                .ToListAsync();

            var topPrograms = (await connected.GroupBy(s => s.Program)
                .Select(x => new { x.Key, Count = x.Count() })
                .OrderByDescending(x => x.Count).Take(10).ToListAsync())
                .Select(x => new NameCount(x.Key ?? "(不明)", x.Count)).ToList();
            var topMachines = (await connected.GroupBy(s => s.Machine)
                .Select(x => new { x.Key, Count = x.Count() })
                .OrderByDescending(x => x.Count).Take(10).ToListAsync())
                .Select(x => new NameCount(x.Key ?? "(不明)", x.Count)).ToList();

            return new SummaryResult(
                await connected.CountAsync(),
                await connected.Select(s => s.Program).Distinct().CountAsync(),
                await connected.Select(s => s.Machine).Distinct().CountAsync(),
                await sessions.CountAsync(s => !s.IsConnected),
                await sessions.CountAsync(s => s.LogonTime >= today),
                await connected.CountAsync(s => s.ProcessKind == ProcessKinds.DbLinkSuspect),
                await connected.CountAsync(s => s.JobName != null),
                perTarget, topPrograms, topMachines);
        });

        // 接続数表：実行端末 × 実行プログラムごとの日単位の接続回数（ログオン時刻で集計）
        g.MapGet("/connection-counts", async (int? targetId, string? q, AppDbContext db) =>
        {
            var b = Buckets.Create(DateTime.Today);
            var sessions = db.Sessions.AsNoTracking().Where(s => s.LogonTime >= b.Month2Start);
            if (targetId is { } tid) sessions = sessions.Where(s => s.TargetId == tid);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var p = QueryHelpers.Contains(q.Trim());
                sessions = sessions.Where(s => EF.Functions.Like(s.Machine, p, "\\") || EF.Functions.Like(s.Program, p, "\\"));
            }

            var rows = await sessions
                .GroupBy(s => new { s.Machine, s.Program })
                .Select(gr => new ConnectionCountRow(
                    gr.Key.Machine, gr.Key.Program,
                    gr.Sum(s => s.LogonTime >= b.Today ? 1 : 0),
                    gr.Sum(s => s.LogonTime >= b.D1 && s.LogonTime < b.Today ? 1 : 0),
                    gr.Sum(s => s.LogonTime >= b.D2 && s.LogonTime < b.D1 ? 1 : 0),
                    gr.Sum(s => s.LogonTime >= b.D3 && s.LogonTime < b.D2 ? 1 : 0),
                    gr.Sum(s => s.LogonTime >= b.D4 && s.LogonTime < b.D3 ? 1 : 0),
                    gr.Sum(s => s.LogonTime >= b.D5 && s.LogonTime < b.D4 ? 1 : 0),
                    gr.Sum(s => s.LogonTime >= b.D6 && s.LogonTime < b.D5 ? 1 : 0),
                    gr.Sum(s => s.LogonTime >= b.Month1Start ? 1 : 0),
                    gr.Sum(s => s.LogonTime >= b.Month2Start && s.LogonTime < b.Month1Start ? 1 : 0)))
                .ToListAsync();

            return new
            {
                periods = new
                {
                    today = b.Today, d1 = b.D1, d2 = b.D2, d3 = b.D3, d4 = b.D4, d5 = b.D5, d6 = b.D6,
                    month1Start = b.Month1Start, month2Start = b.Month2Start,
                },
                rows = rows.OrderByDescending(r => r.Month1).ThenByDescending(r => r.Month2)
                    .ThenBy(r => r.Machine).ThenBy(r => r.Program).ToList(),
            };
        });
    }

    /// <summary>接続数表の期間区分</summary>
    internal sealed record Buckets(DateTime Today, DateTime D1, DateTime D2, DateTime D3, DateTime D4, DateTime D5, DateTime D6,
        DateTime Month1Start, DateTime Month2Start)
    {
        public static Buckets Create(DateTime today) => new(
            today, today.AddDays(-1), today.AddDays(-2), today.AddDays(-3), today.AddDays(-4), today.AddDays(-5), today.AddDays(-6),
            // 本日〜1ヶ月：1ヶ月前の同日以降（本日を含む）／1ヶ月〜2ヶ月：その前の1ヶ月
            today.AddMonths(-1), today.AddMonths(-2));
    }
}
