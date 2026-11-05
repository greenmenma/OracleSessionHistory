using System.Text.RegularExpressions;

namespace OracleSessionHistory.Core.Processes;

/// <summary>
/// gv$session の値から接続元プロセスを特定するための規則（DB・OS に依存しない純粋関数）。
/// </summary>
public static partial class ClientProcessRules
{
    /// <summary>プロセス開始がログオンよりこれ以上後なら、PID が再利用された別プロセスとみなす（クライアントと DB の時計差を許容）</summary>
    public static readonly TimeSpan StartTolerance = TimeSpan.FromMinutes(5);

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
    public static bool IsExcluded(string host, string? patterns)
    {
        if (string.IsNullOrWhiteSpace(patterns)) return false;
        foreach (var p in patterns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var regex = "^" + Regex.Escape(p).Replace(@"\*", ".*") + "$";
            if (Regex.IsMatch(host, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return true;
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
