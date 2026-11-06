using Microsoft.Extensions.Options;
using OracleSessionHistory.Core.Data;
using OracleSessionHistory.Core.Services;

namespace OracleSessionHistory.Batch;

/// <summary>
/// 実行ユーザの表示名（AD の displayName）を定期的に取得する。
/// セッション収集とは独立したループで動かし、AD に接続できなくても収集に影響しないようにする。
/// </summary>
public sealed class UserNameCollector(
    UserNameService service,
    AppDatabaseReadiness readiness,
    IOptions<CollectorOptions> options,
    ILogger<UserNameCollector> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.UserNameMinutes <= 0) return;
        // DB の初期化（マイグレーション）は CollectorHost が行うため、終わるまで待つ
        try { await readiness.WaitAsync(stoppingToken); } catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var r = await service.RunOnceAsync(stoppingToken);
                if (r.Looked > 0 && r.Error is null)
                    logger.LogInformation("実行ユーザの表示名：問い合わせ {Looked} 件 / 見つかった {Found} 件", r.Looked, r.Found);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // 履歴 DB の読み書き失敗など。次の周期で再試行する
                logger.LogError(ex, "実行ユーザの表示名の取得処理でエラーが発生しました");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(options.Value.UserNameMinutes), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
