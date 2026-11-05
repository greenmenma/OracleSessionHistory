namespace OracleSessionHistory.Core.Data;

/// <summary>
/// アプリケーション DB の初期化（マイグレーション）が終わったことを、同じプロセス内の他の処理に知らせる。
/// 初期化前に新しい列を読むとエラーになるため、プロセス情報の取得などはこれを待ってから始める。
/// </summary>
public sealed class AppDatabaseReadiness
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void MarkReady() => _ready.TrySetResult();

    public Task WaitAsync(CancellationToken ct) => _ready.Task.WaitAsync(ct);
}
