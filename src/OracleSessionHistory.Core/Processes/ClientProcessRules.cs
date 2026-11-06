using System.Text.RegularExpressions;

namespace OracleSessionHistory.Core.Processes;

/// <summary>
/// gv$session の値から接続元プロセスを特定するための規則（DB・OS に依存しない純粋関数）。
/// </summary>
public static partial class ClientProcessRules
{
    /// <summary>プロセス開始がログオンよりこれ以上後なら、PID が再利用された別プロセスとみなす（クライアントと DB の時計差を許容）</summary>
    public static readonly TimeSpan StartTolerance = TimeSpan.FromMinutes(5);

    /// <summary>
    /// プロセス情報を取得するプログラム（gv$session.program）。IIS（プールの区別）と sqlplus.exe（実行元の区別）だけを調べる。
    /// それ以外は PROGRAM でアプリを表示し、接続元サーバーに問い合わせない。
    /// </summary>
    public static readonly IReadOnlySet<string> InspectedPrograms =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "w3wp.exe", "sqlplus.exe" };

    /// <summary>プロセス情報を取得する対象のプログラムか</summary>
    public static bool IsInspectedProgram(string? program) =>
        !string.IsNullOrWhiteSpace(program) && InspectedPrograms.Contains(program.Trim());

    [GeneratedRegex(@"(?:^|\s)-ap\s+(?:""([^""]*)""|(\S+))", RegexOptions.IgnoreCase)]
    private static partial Regex AppPoolOption();

    /// <summary>gv$session.machine から接続先ホスト名を取り出す（DOMAIN\HOST は HOST）。取れなければ null。</summary>
    public static string? HostOf(string? machine)
    {
        if (string.IsNullOrWhiteSpace(machine)) return null;
        var m = machine.Trim();
        var i = m.LastIndexOf('\\');
        if (i >= 0) m = m[(i + 1)..].Trim();
        return m.Length > 0 ? m : null;
    }

    /// <summary>gv$session.process からクライアントの PID を取り出す（Windows は "pid:tid" の先頭）。数値でなければ null。</summary>
    public static int? ProcessIdOf(string? clientProcess)
    {
        if (string.IsNullOrWhiteSpace(clientProcess)) return null;
        var head = clientProcess.Split(':')[0].Trim();
        return int.TryParse(head, out var pid) && pid > 0 ? pid : null;
    }

    /// <summary>
    /// ドライバが実際の PID の代わりに固定値を入れているか。JDBC Thin ドライバは既定で PROCESS に "1234" を設定する
    /// （v$session.process を設定しない場合）。存在しないか別のプロセスの PID になるため問い合わせない。
    /// </summary>
    public static bool IsDriverDefaultProcess(string? clientProcess, string? program, string? clientDriver) =>
        clientProcess?.Trim() == "1234"
        && ((program?.Contains("JDBC", StringComparison.OrdinalIgnoreCase) ?? false)
            || (clientDriver?.Contains("jdbc", StringComparison.OrdinalIgnoreCase) ?? false));

    /// <summary>w3wp.exe のコマンドラインからアプリケーションプール名（-ap の値）を取り出す</summary>
    public static string? AppPoolOf(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        var m = AppPoolOption().Match(commandLine);
        if (!m.Success) return null;
        var v = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    /// <summary>ホスト名が対象外端末パターン（カンマ区切り、* はワイルドカード、大文字小文字無視）に一致するか</summary>
    public static bool IsExcluded(string host, string? patterns) => MatchesAny(host, patterns);

    // ユーザ/パスワード（ユーザ・パスワードはダブルクォートで囲まれていてもよい）。jdbc:oracle:thin:scott/tiger@… の「:」の後も対象
    [GeneratedRegex(@"(?<=^|[\s""'=:])([A-Za-z0-9_$#.\-]+|""[^""\s]+"")/(""[^""]*""|[^\s""'/@]+)", RegexOptions.CultureInvariant)]
    private static partial Regex UserPassword();

    // Password=xxx / PWD=xxx（接続文字列）、/pwd=xxx、--password xxx、-password=xxx
    [GeneratedRegex(@"(?<=^|[\s""';/\-])(password|pwd|passwd)(\s*=\s*|\s+)(""[^""]*""|[^;\s""]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PasswordOption();

    // Oracle のツールの実行ファイル（exp / imp は一般的な単語と区別するため .exe 付きのみ）
    [GeneratedRegex(@"(?:^|[\s""\\/])(?:(?:sqlplus|sqlldr|expdp|impdp|rman|dgmgrl|tkprof)(?:\.exe)?|(?:exp|imp)\.exe)(?=[\s""]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OracleTool();

    // ファイルのパスらしい値（ctl/x.ctl、data/in.csv）。ツールの引数でも、接続先（@）が続かなければパスワードとみなさない
    [GeneratedRegex(@"\.[A-Za-z0-9]{1,5}$", RegexOptions.CultureInvariant)]
    private static partial Regex FileExtension();

    public const string PasswordMask = "********";

    /// <summary>Oracle のツール（sqlplus 等）の実行ファイル名・コマンドラインか</summary>
    public static bool IsOracleTool(string? value) => !string.IsNullOrEmpty(value) && OracleTool().IsMatch(" " + value);

    /// <summary>
    /// コマンドラインのパスワードを隠す（保存・表示する前に必ず通す）。次の形を隠す。
    /// ・ユーザ/パスワード：接続先（@）が続くもの（scott/tiger@db、jdbc:oracle:thin:scott/tiger@host）、
    ///   または Oracle のツール（sqlplus 等）とその実行元のコマンドライン中のもの（ファイルのパスらしい値は除く）
    /// ・Password=xxx / PWD=xxx / --password xxx 等のオプション
    /// ユーザとパスワードを別々の引数で渡す形（run.bat scott tiger）は判別できないため隠さない。
    /// </summary>
    /// <param name="oracleTool">Oracle のツールを起動した親プロセスのコマンドライン（バッチの引数に渡す場合など）</param>
    public static string? MaskCredentials(string? commandLine, bool oracleTool = false)
    {
        if (string.IsNullOrEmpty(commandLine)) return commandLine;
        var tool = oracleTool || IsOracleTool(commandLine);
        var masked = UserPassword().Replace(commandLine, m =>
        {
            var end = m.Index + m.Length;
            var hasConnect = end < commandLine.Length && commandLine[end] == '@';
            var tokenEnds = end >= commandLine.Length || hasConnect || char.IsWhiteSpace(commandLine[end]) || commandLine[end] is '"' or '\'';
            var looksLikePath = FileExtension().IsMatch(m.Groups[2].Value);
            return tokenEnds && (hasConnect || (tool && !looksLikePath)) ? m.Groups[1].Value + "/" + PasswordMask : m.Value;
        });
        return PasswordOption().Replace(masked, m => m.Groups[1].Value + m.Groups[2].Value + PasswordMask);
    }

    private static bool MatchesAny(string value, string? patterns)
    {
        if (string.IsNullOrWhiteSpace(patterns)) return false;
        foreach (var p in patterns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var regex = "^" + Regex.Escape(p).Replace(@"\*", ".*") + "$";
            if (Regex.IsMatch(value, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return true;
        }
        return false;
    }

    /// <summary>
    /// 取得したプロセスがセッションのプロセスではない（PID の再利用・クライアントの申告値が偽）と判断できるか。
    /// ・プロセス開始がログオンより <see cref="StartTolerance"/> 以上後
    /// ・PROGRAM が "*.exe" の形で、プロセス名と異なる
    /// </summary>
    public static bool IsMismatch(ClientProcessInfo process, string? program, DateTime logonTime)
    {
        if (process.StartedAt is { } started && started - logonTime >= StartTolerance) return true;

        var prog = program?.Trim();
        if (!string.IsNullOrEmpty(prog) && prog.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(process.Name))
        {
            return !string.Equals(FileNameOf(prog), process.Name.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    /// <summary>Windows パスのファイル名部分（実行環境の OS に依存させないため自前で分割する）</summary>
    internal static string FileNameOf(string path)
    {
        var i = path.LastIndexOfAny(['\\', '/']);
        return i >= 0 ? path[(i + 1)..] : path;
    }

    /// <summary>Windows パスのフォルダ部分。フォルダがなければ null。</summary>
    internal static string? DirectoryOf(string path)
    {
        var i = path.LastIndexOfAny(['\\', '/']);
        return i > 0 ? path[..i] : null;
    }
}
