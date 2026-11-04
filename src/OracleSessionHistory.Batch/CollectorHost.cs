using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OracleSessionHistory.Core.Data;
using OracleSessionHistory.Core.Services;

namespace OracleSessionHistory.Batch;

/// <summary>
/// 監視対象ごとに収集ループを起動・停止する。
/// 監視対象は画面から追加・変更されるため、定期的に DB を確認して差分を反映する。
/// </summary>
public sealed class CollectorHost(
    IServiceProvider services,
    IDbContextFactory<AppDbContext> dbFactory,
    CollectionService collection,
    IOptions<CollectorOptions> options,
    ILogger<CollectorHost> logger) : BackgroundService
{
    private sealed record Runner(DateTime UpdatedAt, CancellationTokenSource Cts, Task Task);

    private readonly Dictionary<int, Runner> _runners = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await AppDatabase.InitializeAsync(services, logger, options.Value.SeedDemoTarget, stoppingToken);
        var lastCleanup = DateTime.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncRunnersAsync(stoppingToken);

                if (DateTime.Now - lastCleanup > TimeSpan.FromHours(6))
                {
                    var deleted = await collection.CleanupAsync(options.Value.RetentionDays, stoppingToken);
                    if (deleted > 0) logger.LogInformation("保持期間を過ぎた履歴を {Count} 件削除しました", deleted);
                    lastCleanup = DateTime.Now;
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "監視対象の同期に失敗しました");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.Value.ReloadSeconds)), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        foreach (var r in _runners.Values) r.Cts.Cancel();
        await Task.WhenAll(_runners.Values.Select(r => r.Task));
    }

    private async Task SyncRunnersAsync(CancellationToken stoppingToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
        var targets = await db.Targets.AsNoTracking().Where(t => t.Enabled)
            .Select(t => new { t.Id, t.Name, t.UpdatedAt })
            .ToListAsync(stoppingToken);

        // 削除・無効化・変更された対象のループを停止
        foreach (var (id, runner) in _runners.ToList())
        {
            var t = targets.FirstOrDefault(x => x.Id == id);
            if (t is not null && t.UpdatedAt == runner.UpdatedAt) continue;
            await runner.Cts.CancelAsync();
            await runner.Task;
            _runners.Remove(id);
        }

        // 新規・変更された対象のループを開始
        foreach (var t in targets.Where(t => !_runners.ContainsKey(t.Id)))
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var task = Task.Run(() => RunTargetAsync(t.Id, t.Name, cts.Token), CancellationToken.None);
            _runners[t.Id] = new Runner(t.UpdatedAt, cts, task);
            logger.LogInformation("[{Target}] 収集を開始しました", t.Name);
        }
    }

    /// <summary>1 つの監視対象の収集ループ</summary>
    private async Task RunTargetAsync(int targetId, string name, CancellationToken ct)
    {
        var lastLinkPoll = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            var interval = 5;
            try
            {
                await using (var db = await dbFactory.CreateDbContextAsync(ct))
                {
                    var t = await db.Targets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == targetId, ct);
                    if (t is null) return;
                    interval = Math.Max(1, t.IntervalSeconds);

                    var started = DateTime.Now;
                    var outcome = await collection.PollSessionsAsync(targetId, ct);

                    if (outcome.Success && DateTime.Now - lastLinkPoll >= TimeSpan.FromSeconds(Math.Max(5, t.LinkIntervalSeconds)))
                    {
                        await collection.PollLinkSourcesAsync(targetId, ct);
                        lastLinkPoll = DateTime.Now;
                    }

                    // 処理時間を差し引いて次回まで待つ
                    var wait = TimeSpan.FromSeconds(interval) - (DateTime.Now - started);
                    if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // アプリ DB の書き込み失敗など。少し待って継続する
                logger.LogError(ex, "[{Target}] 収集処理でエラーが発生しました", name);
                try { await Task.Delay(TimeSpan.FromSeconds(interval), ct); } catch (OperationCanceledException) { break; }
            }
        }
        logger.LogInformation("[{Target}] 収集を停止しました", name);
    }
}
