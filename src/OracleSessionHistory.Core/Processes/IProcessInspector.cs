namespace OracleSessionHistory.Core.Processes;

/// <summary>プロセス情報の取得に使う接続設定</summary>
/// <param name="UserName">DOMAIN\user または user@domain。空なら実行アカウントで接続する</param>
/// <param name="Password">パスワード</param>
/// <param name="DnsSuffix">端末名で名前解決できない場合に付ける DNS サフィックス</param>
/// <param name="Timeout">1 サーバーあたりのタイムアウト</param>
public sealed record ProcessInspectorOptions(string? UserName, string? Password, string? DnsSuffix, TimeSpan Timeout)
{
    /// <summary>ログ・画面にパスワードを出さないよう ToString を上書きする</summary>
    public override string ToString() => $"UserName={UserName}, DnsSuffix={DnsSuffix}, Timeout={Timeout}";
}

/// <summary>
/// 接続元サーバーのプロセス情報を取得する。
/// </summary>
public interface IProcessInspector
{
    /// <summary>
    /// 指定ホストの指定 PID のプロセス情報を取得する。存在しない PID は結果に含めない。
    /// ホスト単位の失敗（名前解決・接続不可・アクセス拒否など）は <see cref="ProcessInspectionException"/> を投げる。
    /// </summary>
    Task<IReadOnlyList<ClientProcessInfo>> InspectAsync(string host, IReadOnlyCollection<int> pids, ProcessInspectorOptions options, CancellationToken ct);

    /// <summary>接続テスト。成功時は結果の説明（プロトコル・プロセス数）を返す。失敗時は例外。</summary>
    Task<string> TestAsync(string host, ProcessInspectorOptions options, CancellationToken ct);
}

/// <summary>ホスト単位のプロセス情報取得の失敗</summary>
public sealed class ProcessInspectionException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>監視対象の Provider に応じてプロセス情報の取得元を選ぶ（Demo の監視対象は疑似データ）</summary>
public sealed class ProcessInspectorResolver(IProcessInspector real, IProcessInspector demo)
{
    public IProcessInspector For(string provider) =>
        string.Equals(provider, Entities.SourceProviders.Demo, StringComparison.OrdinalIgnoreCase) ? demo : real;

    /// <summary>実際のサーバーへの取得元（設定画面の接続テスト用）</summary>
    public IProcessInspector Real => real;
}
