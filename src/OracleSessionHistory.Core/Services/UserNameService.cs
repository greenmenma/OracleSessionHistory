using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OracleSessionHistory.Core.Data;
using OracleSessionHistory.Core.Entities;
using OracleSessionHistory.Core.Users;

namespace OracleSessionHistory.Core.Services;

/// <summary>1 回分の表示名の取得結果</summary>
public sealed record UserNameOutcome(int Looked, int Found, string? Error);

/// <summary>
/// 実行ユーザ（OSUSER・プロセスの実行ユーザ）の表示名を AD から取得して <see cref="DirectoryUser"/> に保存する（Batch から呼ばれる）。
/// セッションの収集とは独立して動き、失敗してもセッションには触れない。
/// </summary>
public sealed class UserNameService(IDbContextFactory<AppDbContext> dbFactory, IUserDirectory directory, ILogger<UserNameService> logger)
{
    /// <summary>この期間に観測したセッション・DB Link 利用元の実行ユーザを調べる（接続アプリ表の最も古い列に合わせる）</summary>
    internal static readonly TimeSpan RecentWindow = TimeSpan.FromDays(62);
    /// <summary>取得済みの表示名を取り直す間隔（AD で名前が変わった場合に反映する）</summary>
    internal static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(1);
    /// <summary>1 回に問い合わせるアカウントの数</summary>
    internal int BatchSize { get; set; } = 500;

    private string? _lastError;

    /// <summary>期間内に観測したセッション・DB Link 利用元の実行ユーザ（OSUSER・プロセスの実行ユーザ）</summary>
    internal static IQueryable<string?> AccountsQuery(AppDbContext db, DateTime since) =>
        db.Sessions.Where(s => s.LastSeenAt >= since).Select(s => s.OsUser)
            .Union(db.Sessions.Where(s => s.LastSeenAt >= since).Select(s => s.ProcOwner))
            .Union(db.DbLinkUsages.Where(u => u.LastSeenAt >= since).Select(u => u.OsUser))
            .Union(db.DbLinkUsages.Where(u => u.LastSeenAt >= since).Select(u => u.ProcOwner))
            .Where(a => a != null && a != "");

    public async Task<UserNameOutcome> RunOnceAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = DateTime.Now;
        var since = now - RecentWindow;

        var raw = await AccountsQuery(db, since).ToListAsync(ct);
        var accounts = raw.Where(UserAccountRules.IsDirectoryAccount).Select(a => UserAccountRules.Normalize(a)!)
            .Where(a => a.Length <= 256).Distinct().ToList();
        if (accounts.Count == 0) return new UserNameOutcome(0, 0, null);

        var stale = now - RefreshAfter;
        var fresh = (await db.DirectoryUsers.Where(d => d.ResolvedAt >= stale).Select(d => d.Account).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targets = accounts.Where(a => !fresh.Contains(a)).Take(BatchSize).ToList();
        if (targets.Count == 0) return new UserNameOutcome(0, 0, null);

        IReadOnlyDictionary<string, string?> names;
        try
        {
            names = await directory.LookupAsync(targets, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // 同じ理由で失敗し続ける場合は、最初の 1 回だけ警告する（取得済みの表示名はそのまま使う）
            if (ex.Message != _lastError)
                logger.LogWarning(ex, "実行ユーザの表示名を AD から取得できませんでした（{Count} 件）", targets.Count);
            _lastError = ex.Message;
            return new UserNameOutcome(targets.Count, 0, ex.Message);
        }
        if (_lastError is not null) logger.LogInformation("実行ユーザの表示名を AD から取得できるようになりました");
        _lastError = null;

        var existing = await db.DirectoryUsers.Where(d => targets.Contains(d.Account)).ToDictionaryAsync(d => d.Account, StringComparer.OrdinalIgnoreCase, ct);
        var found = 0;
        foreach (var account in targets)
        {
            var hit = names.TryGetValue(account, out var name);
            if (!existing.TryGetValue(account, out var row))
            {
                row = new DirectoryUser { Account = account };
                db.DirectoryUsers.Add(row);
            }
            row.Found = hit && name is not null;
            row.DisplayName = name is { Length: > 256 } ? name[..256] : name;
            row.ResolvedAt = now;
            if (row.Found) found++;
        }
        await db.SaveChangesAsync(ct);
        return new UserNameOutcome(targets.Count, found, null);
    }
}
