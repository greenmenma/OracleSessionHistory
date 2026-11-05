namespace OracleSessionHistory.Core.Processes;

/// <summary>
/// 一覧・集計のキーにする「アプリ名」を決める（純粋関数）。
/// プロセス情報が取れた場合はそれを優先し、取れない場合は gv$session の PROGRAM 等から決める。
/// </summary>
public static class AppIdentifier
{
    public const string Unknown = "(不明)";
    public const int MaxAppNameLength = 256;
    private const string IisPrefix = "IIS: ";
    private const string IisAppPoolUserPrefix = @"IIS APPPOOL\";

    /// <returns>AppName（集計キー・主表示）と AppDetail（補足表示）</returns>
    public static (string AppName, string? AppDetail) Identify(ClientProcessInfo? process, string? program, string? module, string? osUser)
    {
        if (process is not null && FromProcess(process) is { } fromProcess)
            return (Limit(fromProcess.Name), fromProcess.Detail);

        var prog = string.IsNullOrWhiteSpace(program) ? null : program.Trim();

        // プロセス情報が無くても、ApplicationPoolIdentity で動く w3wp.exe は OSUSER にプール名が入る
        if (prog is not null && IsW3wp(prog) && osUser?.Trim() is { } user
            && user.StartsWith(IisAppPoolUserPrefix, StringComparison.OrdinalIgnoreCase) && user.Length > IisAppPoolUserPrefix.Length)
        {
            return (Limit(IisPrefix + user[IisAppPoolUserPrefix.Length..]), null);
        }

        var detail = !string.IsNullOrWhiteSpace(module) && !string.Equals(module.Trim(), prog, StringComparison.OrdinalIgnoreCase)
            ? module.Trim()
            : null;
        return (Limit(prog ?? Unknown), detail);
    }

    private static (string Name, string? Detail)? FromProcess(ClientProcessInfo p)
    {
        var name = p.Name?.Trim();
        if (string.IsNullOrEmpty(name)) return null;
        var path = string.IsNullOrWhiteSpace(p.ExecutablePath) ? null : p.ExecutablePath.Trim();

        if (IsW3wp(name))
        {
            var pool = p.IisAppPool ?? ClientProcessRules.AppPoolOf(p.CommandLine);
            if (pool is not null) return (IisPrefix + pool, p.IisSite ?? path);
        }

        var args = SplitCommandLine(p.CommandLine);
        if (name.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            // 先頭（dotnet.exe 自身）以降で最初の .dll がアプリ本体
            var dll = args.Skip(1).FirstOrDefault(a => a.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
            if (dll is not null) return (ClientProcessRules.FileNameOf(dll), dll);
        }
        if (name.Equals("java.exe", StringComparison.OrdinalIgnoreCase) || name.Equals("javaw.exe", StringComparison.OrdinalIgnoreCase))
        {
            var i = args.FindIndex(a => a.Equals("-jar", StringComparison.OrdinalIgnoreCase));
            if (i >= 0 && i + 1 < args.Count) return (ClientProcessRules.FileNameOf(args[i + 1]), args[i + 1]);
        }

        return path is not null
            ? (ClientProcessRules.FileNameOf(path), ClientProcessRules.DirectoryOf(path))
            : (name, null);
    }

    private static bool IsW3wp(string name) => name.Equals("w3wp.exe", StringComparison.OrdinalIgnoreCase);

    private static string Limit(string s) => s.Length <= MaxAppNameLength ? s : s[..MaxAppNameLength];

    /// <summary>Windows のコマンドラインを引数に分割する（ダブルクォートで囲まれた空白は区切らない）</summary>
    internal static List<string> SplitCommandLine(string? commandLine)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine)) return result;
        var current = new System.Text.StringBuilder();
        var quoted = false;
        var hasToken = false;
        foreach (var c in commandLine)
        {
            if (c == '"')
            {
                quoted = !quoted;
                hasToken = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (hasToken) result.Add(current.ToString());
                current.Clear();
                hasToken = false;
            }
            else
            {
                current.Append(c);
                hasToken = true;
            }
        }
        if (hasToken) result.Add(current.ToString());
        return result;
    }
}
