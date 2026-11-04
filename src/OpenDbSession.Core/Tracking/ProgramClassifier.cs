using System.Text.RegularExpressions;
using OpenDbSession.Core.Entities;

namespace OpenDbSession.Core.Tracking;

/// <summary>
/// gv$session.program 等からプロセス種別を判定する。
/// Windows の Oracle は全プロセスが ORACLE.EXE のスレッドとして動くため、
/// "ORACLE.EXE (J000)" のように括弧内のプロセス名で種別を判断する。
/// UNIX 系では "oracle@host (J000)" の形式になる。
/// </summary>
public static partial class ProgramClassifier
{
    [GeneratedRegex(@"^(oracle\.exe|oracle@)", RegexOptions.IgnoreCase)]
    private static partial Regex OracleProgram();

    [GeneratedRegex(@"\(([A-Z0-9]{2,5})\)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ProcessTag();

    public static string Classify(string? program, string? sessionType)
    {
        if (string.Equals(sessionType, "BACKGROUND", StringComparison.OrdinalIgnoreCase))
            return ProcessKinds.Background;

        if (string.IsNullOrWhiteSpace(program) || !OracleProgram().IsMatch(program.Trim()))
            return ProcessKinds.Client;

        var m = ProcessTag().Match(program.Trim());
        if (!m.Success)
        {
            // "ORACLE.EXE" / "oracle@host" / "oracle@host (TNS V1-V3)" … 別 DB のサーバープロセスからの接続＝DB Link の可能性
            return ProcessKinds.DbLinkSuspect;
        }

        var tag = m.Groups[1].Value.ToUpperInvariant();
        return tag switch
        {
            "SHAD" => ProcessKinds.DbLinkSuspect, // 共有/専用サーバー表記
            _ when Regex.IsMatch(tag, @"^J\d{3}$") => ProcessKinds.JobSlave,
            _ when Regex.IsMatch(tag, @"^(P\d{3}|PZ\d{2})$") => ProcessKinds.ParallelSlave,
            _ when Regex.IsMatch(tag, @"^(Q\d{3}|Q\d[0-9A-Z]{2}|QM\d{2})$") => ProcessKinds.AqSlave,
            _ when Regex.IsMatch(tag, @"^(DM\d{2}|DW\d{2})$") => ProcessKinds.DataPump,
            _ => ProcessKinds.OracleInternal,
        };
    }

    /// <summary>DB Link 利用元を特定する際に使うプロセス ID 候補（Windows は "pid:tid" 形式のため分割）</summary>
    public static IEnumerable<string> ProcessIdCandidates(string? clientProcess)
    {
        if (string.IsNullOrWhiteSpace(clientProcess)) yield break;
        foreach (var part in clientProcess.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.All(char.IsDigit)) yield return part;
        }
    }
}
