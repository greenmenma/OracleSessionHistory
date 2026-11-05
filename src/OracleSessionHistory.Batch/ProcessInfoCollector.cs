using Microsoft.Extensions.Options;
using OracleSessionHistory.Core.Services;

namespace OracleSessionHistory.Batch;

/// <summary>
/// 接続元サーバーのプロセス情報を定期的に取得する。
/// セッション収集（<see cref="CollectorHost"/>）とは独立したループで動かし、取得の遅延・失敗が収集に影響しないようにする。
/// </summary>
public sealed class ProcessInfoCollector(
    ProcessInfoService service,
    IOptions<CollectorOptions> options,
    ILogger<ProcessInfoCollector> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // DB の初期化（マイグレーション）は CollectorHost が行うため、少し待ってから始める
        try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); } catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var r = await service.RunOnceAsync(stoppingToken);
                if (r.Resolved > 0 || r.Failed > 0)
                    logger.LogInformation("プロセス情報：取得 {Resolved} 件 / 失敗 {Failed} 件 / 対象外 {Skipped} 件", r.Resolved, r.Failed, r.Skipped);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // 履歴 DB の読み書き失敗など。次の周期で再試行する
                logger.LogError(ex, "プロセス情報の取得処理でエラーが発生しました");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.Value.ProcessInfoSeconds)), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
