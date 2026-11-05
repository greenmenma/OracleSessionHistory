namespace OracleSessionHistory.Core.Processes;

/// <summary>
/// 接続できないホストへの問い合わせ間隔を空ける（1 分 → 5 分 → 15 分 → 60 分）。スレッドセーフ。
/// 応答しないサーバーへ数秒おきに問い合わせてタイムアウト待ちで詰まる・ログが埋まるのを防ぐ。
/// </summary>
public sealed class HostBackoff
{
    private static readonly TimeSpan[] Steps =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(60)];

    private sealed record State(int Failures, DateTime RetryAt);

    private readonly Lock _lock = new();
    private readonly Dictionary<string, State> _states = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>失敗を記録する。連続失敗の最初（ログに出すべき）なら true</summary>
    public bool RecordFailure(string host, DateTime now)
    {
        lock (_lock)
        {
            var failures = _states.TryGetValue(host, out var s) ? s.Failures + 1 : 1;
            _states[host] = new State(failures, now + Steps[Math.Min(failures, Steps.Length) - 1]);
            return failures == 1;
        }
    }

    /// <summary>成功を記録する。失敗が続いていた状態から回復した（ログに出すべき）なら true</summary>
    public bool RecordSuccess(string host)
    {
        lock (_lock) return _states.Remove(host);
    }

    /// <summary>再試行待ちか</summary>
    public bool IsWaiting(string host, DateTime now)
    {
        lock (_lock) return _states.TryGetValue(host, out var s) && now < s.RetryAt;
    }

    /// <summary>次に再試行する時刻（待ちでなければ null）</summary>
    public DateTime? RetryAt(string host)
    {
        lock (_lock) return _states.TryGetValue(host, out var s) ? s.RetryAt : null;
    }
}
