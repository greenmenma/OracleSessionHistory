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

    // cmd /c はコマンドを実行して終わる（/k は実行後も対話で残るため、手で使っているシェルとみなす）
    [GeneratedRegex(@"(?:^|\s)/c(?:\s|""|$)", RegexOptions.IgnoreCase)]
    private static partial Regex CmdCommandOption();

    // リダイレクト（> log.txt、2>> err.log、< in.txt）。出力先のファイルをスクリプトと誤らないよう除く
    [GeneratedRegex(@"\d?>>?\s*(?:""[^""]*""|\S+)|<\s*(?:""[^""]*""|\S+)")]
    private static partial Regex Redirection();

    // パスらしい書き出し（D:\、\\server、.\）
    [GeneratedRegex(@"^(?:[A-Za-z]:[\\/]|\\\\|\.{1,2}[\\/])")]
    private static partial Regex RootedPath();

    /// <summary>PowerShell の、コマンド・スクリプトを渡すオプション（前方一致の略記も受け付ける）</summary>
    private static readonly string[] PowerShellCommandOptions = ["command", "file", "encodedcommand", "ec"];

    /// <summary>PowerShell の、値を取るオプション（値を位置引数と誤らないよう読み飛ばす）</summary>
    private static readonly HashSet<string> PowerShellValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "executionpolicy", "ep", "ex", "windowstyle", "w", "win", "version", "v", "configurationname", "config",
        "inputformat", "if", "outputformat", "of", "o", "psconsolefile", "workingdirectory", "wd", "settingsfile",
    };

    public const string Unknown = "(不明)";
    public const int MaxAppNameLength = 256;
    private const string IisPrefix = "IIS: ";
    private const string IisAppPoolUserPrefix = @"IIS APPPOOL\";

    /// <returns>AppName（集計キー・主表示）と AppDetail（補足表示）</returns>
    /// <summary>
    /// 接続アプリ表の段に分ける：実行ファイル（w3wp.exe・sqlplus.exe 等）と、その下の区別（IIS のプール・sqlplus の実行元・dll 等）。
    /// 下の区別が無い場合は null。<paramref name="program"/> は PROGRAM（DB Link の接続元のように不明な場合は null）。
    /// </summary>
    public static (string Exe, string? Sub) SplitForTree(string? program, string appName)
    {
        var app = appName.Trim();
        if (app.StartsWith(IisPrefix, StringComparison.Ordinal)) return ("w3wp.exe", NullIfEmpty(app[IisPrefix.Length..]));
        // sqlplus.exe: 月次集計.bat（実行元でアプリを区別するツール）
        var colon = app.IndexOf(": ", StringComparison.Ordinal);
        if (colon > 0 && LaunchedTools.Contains(app[..colon])) return (app[..colon], NullIfEmpty(app[(colon + 2)..]));
        var prog = program?.Trim();
        // dotnet.exe で dll 名を確定した行など（取得対象を絞る前に記録したもの）
        if (!string.IsNullOrEmpty(prog) && !string.Equals(prog, app, StringComparison.OrdinalIgnoreCase)
            && prog.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return (prog, app);
        return (app.Length > 0 ? app : "(不明)", null);

        static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

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

    private static bool RunsCommand(string shell, string? commandLine)
    {
        if (commandLine is null) return false;
        if (shell.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase)) return CmdCommandOption().IsMatch(commandLine);
        if (!shell.StartsWith("p", StringComparison.OrdinalIgnoreCase)) return true; // cscript / wscript は常にスクリプトを実行する

        // PowerShell：-Command / -File / -EncodedCommand（略記を含む）か、オプション以外の引数（コマンドとして実行される）があれば実行
        var args = SplitCommandLine(commandLine);
        for (var i = 1; i < args.Count; i++)
        {
            var a = args[i];
            if (!a.StartsWith('-') && !a.StartsWith('/')) return true;
            var opt = a.TrimStart('-', '/');
            if (opt.Length == 0) continue;
            if (PowerShellValueOptions.Contains(opt)) { i++; continue; }
            if (PowerShellCommandOptions.Any(o => o.StartsWith(opt, StringComparison.OrdinalIgnoreCase))) return true;
        }
        return false;
    }

    /// <summary>コマンドラインから実行しているスクリプトのパスを取り出す（空白を含むパスはダブルクォートで囲まれている）</summary>
    private static string? ScriptOf(string? commandLine, string[] extensions)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        var text = Redirection().Replace(commandLine, " ");
        var ext = string.Join("|", extensions.Select(e => Regex.Escape(e)));
        // 1. クォートで囲まれたパス（空白を含むものは D:\ 等で始まるパスだけ。"call D:\x.bat" のようなコマンドは除く）
        foreach (Match q in Regex.Matches(text, $@"""([^""]+?(?:{ext}))""|'([^']+?(?:{ext}))'", RegexOptions.IgnoreCase))
        {
            var v = (q.Groups[1].Success ? q.Groups[1].Value : q.Groups[2].Value).Trim();
            if (!v.Any(char.IsWhiteSpace) || RootedPath().IsMatch(v)) return v;
        }
        // 2. 空白を含まないパス
        var m = Regex.Match(text, $@"(?:^|[\s=""'])([^\s""'=]+?(?:{ext}))(?=[\s""']|$)", RegexOptions.IgnoreCase);
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
