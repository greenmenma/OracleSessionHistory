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
public sealed record ClientProcessInfo(
    int ProcessId,
    string? Name,
    string? ExecutablePath,
    string? CommandLine,
    string? Owner,
    DateTime? StartedAt,
    string? ParentName,
    string? IisAppPool,
    string? IisSite);
