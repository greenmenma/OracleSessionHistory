namespace OracleSessionHistory.Core.Processes;

/// <summary>
/// 接続元サーバーから取得したクライアントプロセスの情報（Win32_Process 等）。
/// </summary>
/// <param name="ProcessId">プロセス ID</param>
/// <param name="Name">プロセス名（例：w3wp.exe）</param>
/// <param name="ExecutablePath">実行ファイルのフルパス</param>
/// <param name="CommandLine">コマンドライン</param>
/// <param name="Owner">実行ユーザ（DOMAIN\User）</param>
/// <param name="StartedAt">プロセス開始時刻（ローカル時刻）</param>
/// <param name="ParentName">親プロセス名</param>
/// <param name="IisAppPool">IIS アプリケーションプール名（w3wp.exe のみ）</param>
/// <param name="IisSite">IIS のサイト名・アプリのパス（例：Default Web Site/foo。複数はカンマ区切り）</param>
/// <param name="Ancestors">親プロセスから順にさかのぼった実行元（取得できた分だけ。sqlplus.exe 等の実行元の特定に使う）</param>
public sealed record ClientProcessInfo(
    int ProcessId,
    string? Name,
    string? ExecutablePath,
    string? CommandLine,
    string? Owner,
    DateTime? StartedAt,
    string? ParentName,
    string? IisAppPool,
    string? IisSite,
    IReadOnlyList<ParentProcessInfo>? Ancestors = null);

/// <summary>実行元（親・その親…）のプロセス</summary>
public sealed record ParentProcessInfo(int ProcessId, string? Name, string? CommandLine, DateTime? StartedAt);
