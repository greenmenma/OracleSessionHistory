using System.Text.RegularExpressions;

namespace OracleSessionHistory.Core.Processes;

/// <summary>
/// 一覧・集計のキーにする「アプリ名」を決める（純粋関数）。
/// プロセス情報が取れた場合はそれを優先し、取れない場合は gv$session の PROGRAM 等から決める。
/// </summary>
public static partial class AppIdentifier
{
    /// <summary>
    /// 実行元（親プロセス）でアプリを区別するツール。バッチ・スクリプト・別のプログラムから起動されることが多く、
    /// 実行ファイル名だけではどの処理か分からないもの。必要に応じて追加する（docs/process-info.md）。
    /// </summary>
    public static readonly IReadOnlySet<string> LaunchedTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "sqlplus.exe" };

    /// <summary>スクリプトを実行するシェル（実行元をさらにさかのぼる対象）と、そのスクリプトの拡張子</summary>
    private static readonly Dictionary<string, string[]> ScriptHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cmd.exe"] = [".bat", ".cmd"],
        ["powershell.exe"] = [".ps1"],
        ["pwsh.exe"] = [".ps1"],
        ["cscript.exe"] = [".vbs", ".js", ".wsf"],
        ["wscript.exe"] = [".vbs", ".js", ".wsf"],
    };

    [GeneratedRegex(@"(?:^|\s)/[ck](?:\s|""|$)", RegexOptions.IgnoreCase)]
    private static partial Regex CmdCommandOption();

    [GeneratedRegex(@"(?:^|\s)-(?:file|f|command|c|encodedcommand|ec|e)(?:\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex PowerShellCommandOption();

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

        if (LaunchedTools.Contains(name) && p.Ancestors is { Count: > 0 } ancestors)
        {
            var (label, detail) = DescribeLauncher(ancestors);
            return ($"{name}: {label}", "実行元: " + detail);
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

    /// <summary>
    /// 実行元を、アプリ名に付ける短い名前（スクリプトのファイル名・起動したプログラム名）と補足（スクリプトのパスと親のつながり）にする。
    /// ・シェルがスクリプトを実行していれば、そのスクリプト
    /// ・コマンドを渡されただけのシェル（cmd /c sqlplus …）は、さらに親をさかのぼる
    /// ・対話のシェル（コマンドプロンプトで手で実行）はシェルの名前
    /// ・それ以外のプログラム（ジョブ管理・Java 等）はその名前
    /// </summary>
    internal static (string Label, string Detail) DescribeLauncher(IReadOnlyList<ParentProcessInfo> ancestors)
    {
        var chain = string.Join(" ← ", ancestors.Select(a => a.Name ?? "?"));
        foreach (var a in ancestors)
        {
            var name = a.Name?.Trim();
            if (string.IsNullOrEmpty(name)) break;
            if (!ScriptHosts.TryGetValue(name, out var extensions)) return (name, chain);
            if (ScriptOf(a.CommandLine, extensions) is { } script)
                return (ClientProcessRules.FileNameOf(script), $"{script}（{chain}）");
            // コマンドラインが取れない・コマンドを渡されていないシェルは、人が対話で実行したもの
            if (!RunsCommand(name, a.CommandLine)) return (name, chain);
        }
        return (ancestors[0].Name ?? Unknown, chain);
    }

    private static bool RunsCommand(string shell, string? commandLine) =>
        commandLine is not null && (shell.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase)
            ? CmdCommandOption().IsMatch(commandLine)
            : PowerShellCommandOption().IsMatch(commandLine) || !shell.StartsWith("p", StringComparison.OrdinalIgnoreCase));

    /// <summary>コマンドラインから実行しているスクリプトのパスを取り出す（空白を含むパスはダブルクォートで囲まれている）</summary>
    private static string? ScriptOf(string? commandLine, string[] extensions)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        var ext = string.Join("|", extensions.Select(e => Regex.Escape(e)));
        // 1. ダブルクォートで囲まれたパス 2. 空白を含まないパス
        var m = Regex.Match(commandLine, $@"""([^""]+?(?:{ext}))""", RegexOptions.IgnoreCase);
        if (!m.Success) m = Regex.Match(commandLine, $@"(?:^|[\s=])([^\s""=]+?(?:{ext}))(?=[\s""]|$)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : null;
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
