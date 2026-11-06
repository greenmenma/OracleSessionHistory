namespace OracleSessionHistory.Core.Collection;

/// <summary>1 回のポーリングで取得したセッション一覧</summary>
public sealed class SessionSnapshot
{
    /// <summary>取得時刻（アプリ側のローカル時刻）</summary>
    public DateTime CapturedAt { get; init; }

    /// <summary>
    /// 応答したインスタンス番号。RAC で一部インスタンスの情報が取れなかった場合に、
    /// そのインスタンスのセッションを誤って切断扱いにしないために使う。
    /// </summary>
    public required IReadOnlySet<int> RespondedInstances { get; init; }

    public required IReadOnlyList<ObservedSession> Sessions { get; init; }

    /// <summary>付加情報（ジョブ等）の取得で発生した警告。セッション取得自体は成功している。</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>取得したセッション 1 件</summary>
public sealed record ObservedSession
{
    public int InstId { get; init; }
    public int Sid { get; init; }
    public int Serial { get; init; }
    public DateTime LogonTime { get; init; }
    public long? AuditSid { get; init; }
    public string? Username { get; init; }
    public string? SchemaName { get; init; }
    public string? OsUser { get; init; }
    public string? Machine { get; init; }
    public string? Terminal { get; init; }
    public string? Program { get; init; }
    public string? Module { get; init; }
    public string? Action { get; init; }
    public string? ClientInfo { get; init; }
    public string? ClientIdentifier { get; init; }
    public string? ServiceName { get; init; }
    public string? ClientProcess { get; init; }
    public string? ServerProcess { get; init; }
    public int? Port { get; init; }
    public string? ClientDriver { get; init; }
    public string? ClientVersion { get; init; }
    public string? SessionType { get; init; }
    public string? Status { get; init; }
    public string? SqlId { get; init; }
    public string? SqlText { get; init; }
    public string? Event { get; init; }

    // ---- 付加情報 ----
    public string? JobType { get; init; }
    public string? JobOwner { get; init; }
    public string? JobName { get; init; }
    public string? JobAction { get; init; }
    public string? ParentInfo { get; init; }
}

/// <summary>
/// セッション情報の取得元。Oracle 以外の DB を監視したい場合はこのインターフェースを実装し、
/// <see cref="SessionSourceFactory"/> に登録する。
/// 取得に失敗した場合は例外を投げること（例外時は切断判定を行わない）。
/// </summary>
public interface ISessionSource
{
    string Provider { get; }
    Task<SessionSnapshot> CaptureAsync(Entities.MonitorTarget target, CancellationToken ct);
    /// <summary>接続テスト。成功時はサーバー情報（バージョン等）を返す。</summary>
    Task<string> TestConnectionAsync(string connectionString, CancellationToken ct);

    /// <summary>
    /// 監査証跡（DBA_AUDIT_SESSION 等）から、期間内の成功したログオン件数を日・端末ごとに取得する（このシステム自身の接続は除く）。
    /// 失敗時は例外。
    /// </summary>
    Task<IReadOnlyList<Entities.AuditLogonDay>> CaptureAuditLogonsAsync(Entities.MonitorTarget target, DateTime from, DateTime to, CancellationToken ct);
}

/// <summary>Provider 名から取得元を解決する</summary>
public sealed class SessionSourceFactory(IEnumerable<ISessionSource> sources)
{
    private readonly Dictionary<string, ISessionSource> _map =
        sources.ToDictionary(s => s.Provider, StringComparer.OrdinalIgnoreCase);

    public ISessionSource Get(string provider) =>
        _map.TryGetValue(provider, out var s) ? s : throw new NotSupportedException($"未対応の Provider です: {provider}");

    public IEnumerable<string> Providers => _map.Keys;
}
