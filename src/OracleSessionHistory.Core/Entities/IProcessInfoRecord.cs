namespace OracleSessionHistory.Core.Entities;

/// <summary>
/// 接続元プロセス情報を取得・保存する行（監視対象のセッション <see cref="TrackedSession"/> と、
/// DB Link 接続元 DB 上のセッション <see cref="DbLinkUsage"/>）。<c>ProcessInfoService</c> が同じ規則で扱う。
/// </summary>
public interface IProcessInfoRecord
{
    // ---- gv$session の値（取得の手がかり） ----
    string? Machine { get; }
    string? ClientProcess { get; }
    string? Program { get; }
    string? Module { get; }
    string? OsUser { get; }
    string? ClientDriver { get; }
    DateTime LogonTime { get; }

    // ---- 取得結果 ----
    string AppName { get; set; }
    string? AppDetail { get; set; }
    string ProcInfoState { get; set; }
    string? ProcInfoError { get; set; }
    DateTime? ProcInfoAt { get; set; }
    string? ProcName { get; set; }
    string? ProcPath { get; set; }
    string? ProcCommandLine { get; set; }
    string? ProcOwner { get; set; }
    DateTime? ProcStartedAt { get; set; }
    string? ProcParentName { get; set; }
    string? IisAppPool { get; set; }
    string? IisSite { get; set; }
}
