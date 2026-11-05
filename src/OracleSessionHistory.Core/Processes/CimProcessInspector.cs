using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Management.Infrastructure;
using Microsoft.Management.Infrastructure.Options;

namespace OracleSessionHistory.Core.Processes;

/// <summary>
/// WMI/CIM（Win32_Process）で接続元サーバーのプロセス情報を取得する（Windows のみ）。
/// WinRM（WS-Man）で接続し、失敗した場合は DCOM を試す。成功したプロトコルはホストごとに覚えておく。
/// </summary>
public sealed class CimProcessInspector(ILogger<CimProcessInspector> logger) : IProcessInspector
{
    private const string CimV2 = @"root\cimv2";
    private const string WebAdministration = @"root\WebAdministration";
    /// <summary>1 回の WQL に含める PID の数</summary>
    private const int PidChunk = 50;
    // GetOwner はインスタンスのキー（Handle）が必要なため SELECT に含める
    private static readonly TimeSpan IisCacheDuration = TimeSpan.FromHours(1);

    private enum Protocol { WsMan, Dcom }

    /// <summary>ホスト → 接続できたプロトコル</summary>
    private readonly ConcurrentDictionary<string, Protocol> _protocols = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>(ホスト, プール) → IIS のサイト／パス（null は取得できなかった）</summary>
    private readonly ConcurrentDictionary<(string Host, string Pool), (string? Site, DateTime At)> _iisSites = new();

    public Task<IReadOnlyList<ClientProcessInfo>> InspectAsync(string host, IReadOnlyCollection<int> pids, ProcessInspectorOptions options, CancellationToken ct)
    {
        if (pids.Count == 0) return Task.FromResult<IReadOnlyList<ClientProcessInfo>>([]);
        // CIM の API は同期処理のため、スレッドプールで実行する
        return Task.Run(() => OperatingSystem.IsWindows() ? InspectCoreAsync(host, pids, options, ct) : throw NotSupported(), ct);
    }

    public Task<string> TestAsync(string host, ProcessInspectorOptions options, CancellationToken ct)
    {
        return Task.Run(async () =>
        {
            if (!OperatingSystem.IsWindows()) throw NotSupported();
            var target = await ResolveHostAsync(host, options.DnsSuffix, ct);
            var (count, protocol) = WithSession(target, options, ct, (session, op) =>
                session.QueryInstances(CimV2, "WQL", "SELECT ProcessId FROM Win32_Process", op).Count());
            return $"{target} に {(protocol == Protocol.WsMan ? "WinRM" : "DCOM")} で接続し、{count} 件のプロセスを取得しました";
        }, ct);
    }

    private static ProcessInspectionException NotSupported() => new("この OS ではプロセス情報を取得できません（Windows のみ）");

    [SupportedOSPlatform("windows")]
    private async Task<IReadOnlyList<ClientProcessInfo>> InspectCoreAsync(string host, IReadOnlyCollection<int> pids, ProcessInspectorOptions options, CancellationToken ct)
    {
        var target = await ResolveHostAsync(host, options.DnsSuffix, ct);
        var (result, _) = WithSession(target, options, ct, (session, op) =>
        {
            var rows = new List<ClientProcessInfo>();
            var parents = new Dictionary<int, int>();
            foreach (var chunk in pids.Distinct().Chunk(PidChunk))
            {
                var wql = "SELECT Handle, ProcessId, ParentProcessId, Name, ExecutablePath, CommandLine, CreationDate FROM Win32_Process WHERE "
                          + string.Join(" OR ", chunk.Select(p => "ProcessId = " + p.ToString(CultureInfo.InvariantCulture)));
                foreach (var inst in session.QueryInstances(CimV2, "WQL", wql, op))
                {
                    using (inst)
                    {
                        var pid = ToInt(inst, "ProcessId");
                        if (pid is null) continue;
                        if (ToInt(inst, "ParentProcessId") is { } ppid) parents[pid.Value] = ppid;
                        var name = Str(inst, "Name");
                        var cmd = Str(inst, "CommandLine");
                        rows.Add(new ClientProcessInfo(pid.Value, name, Str(inst, "ExecutablePath"), cmd,
                            GetOwner(session, inst, op), ToLocalTime(inst.CimInstanceProperties["CreationDate"]?.Value),
                            null, IsW3wp(name) ? ClientProcessRules.AppPoolOf(cmd) : null, null));
                    }
                }
            }

            var parentNames = QueryNames(session, op, parents.Values.Distinct().ToList());
            return rows.Select(r => r with
            {
                ParentName = parents.TryGetValue(r.ProcessId, out var pp) && parentNames.TryGetValue(pp, out var pn) ? pn : null,
                IisSite = r.IisAppPool is { } pool ? GetIisSite(session, op, target, pool) : null,
            }).ToList();
        });
        return result;
    }

    /// <summary>
    /// CIM セッションを作って処理を実行する。前回成功したプロトコル（初回は WinRM）で失敗した場合はもう一方を試す。
    /// どちらも失敗した場合は <see cref="ProcessInspectionException"/>。
    /// </summary>
    [SupportedOSPlatform("windows")]
    private (T Result, Protocol Protocol) WithSession<T>(string host, ProcessInspectorOptions options, CancellationToken ct,
        Func<CimSession, CimOperationOptions, T> action)
    {
        var first = _protocols.TryGetValue(host, out var known) ? known : Protocol.WsMan;
        Exception? firstError = null;
        foreach (var protocol in new[] { first, first == Protocol.WsMan ? Protocol.Dcom : Protocol.WsMan })
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var sessionOptions = CreateSessionOptions(protocol, options);
                using var session = CimSession.Create(host, sessionOptions);
                using var op = new CimOperationOptions { Timeout = options.Timeout, CancellationToken = ct };
                var result = action(session, op);
                _protocols[host] = protocol;
                return (result, protocol);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is CimException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                logger.LogDebug(ex, "{Host} への {Protocol} 接続に失敗しました", host, protocol);
                firstError ??= ex;
            }
        }
        throw new ProcessInspectionException(Describe(firstError!), firstError);
    }

    [SupportedOSPlatform("windows")]
    private static CimSessionOptions CreateSessionOptions(Protocol protocol, ProcessInspectorOptions options)
    {
        CimSessionOptions so = protocol == Protocol.WsMan
            ? new WSManSessionOptions()
            // IIS の WMI（root\WebAdministration）は暗号化（PacketPrivacy）が必須
            : new DComSessionOptions { PacketPrivacy = true, PacketIntegrity = true };
        so.Timeout = options.Timeout;
        if (!string.IsNullOrWhiteSpace(options.UserName))
        {
            var (domain, user) = SplitUser(options.UserName.Trim());
            var secure = new SecureString();
            foreach (var c in options.Password ?? "") secure.AppendChar(c);
            secure.MakeReadOnly();
            so.AddDestinationCredentials(new CimCredential(PasswordAuthenticationMechanism.Default, domain, user, secure));
        }
        return so;
    }

    /// <summary>"DOMAIN\user" はドメインとユーザに分ける。"user@domain" はそのままユーザ名として渡す</summary>
    internal static (string? Domain, string User) SplitUser(string userName)
    {
        var i = userName.IndexOf('\\');
        return i > 0 ? (userName[..i], userName[(i + 1)..]) : (null, userName);
    }

    /// <summary>名前解決できない場合は DNS サフィックスを付けた名前を試す</summary>
    private static async Task<string> ResolveHostAsync(string host, string? dnsSuffix, CancellationToken ct)
    {
        if (await CanResolveAsync(host, ct)) return host;
        if (!string.IsNullOrWhiteSpace(dnsSuffix) && !host.Contains('.'))
        {
            var fqdn = host + "." + dnsSuffix.Trim().TrimStart('.');
            if (await CanResolveAsync(fqdn, ct)) return fqdn;
            throw new ProcessInspectionException($"端末名 {host}（{fqdn}）を名前解決できません");
        }
        throw new ProcessInspectionException($"端末名 {host} を名前解決できません");
    }

    internal static async Task<bool> CanResolveAsync(string host, CancellationToken ct)
    {
        try
        {
            return (await Dns.GetHostAddressesAsync(host, ct)).Length > 0;
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            // ArgumentException：空白を含む・長すぎるなど、ホスト名として不正な MACHINE
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private string? GetOwner(CimSession session, CimInstance process, CimOperationOptions op)
    {
        try
        {
            using var result = session.InvokeMethod(CimV2, process, "GetOwner", new CimMethodParametersCollection(), op);
            if (Convert.ToInt32(result.ReturnValue?.Value ?? -1, CultureInfo.InvariantCulture) != 0) return null;
            var user = result.OutParameters["User"]?.Value as string;
            var domain = result.OutParameters["Domain"]?.Value as string;
            if (string.IsNullOrEmpty(user)) return null;
            return string.IsNullOrEmpty(domain) ? user : domain + "\\" + user;
        }
        catch (CimException ex)
        {
            // 権限不足等。オーナー以外の情報は保存する
            logger.LogDebug(ex, "プロセスのオーナーを取得できませんでした");
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static Dictionary<int, string> QueryNames(CimSession session, CimOperationOptions op, IReadOnlyCollection<int> pids)
    {
        var map = new Dictionary<int, string>();
        foreach (var chunk in pids.Chunk(PidChunk))
        {
            var wql = "SELECT ProcessId, Name FROM Win32_Process WHERE "
                      + string.Join(" OR ", chunk.Select(p => "ProcessId = " + p.ToString(CultureInfo.InvariantCulture)));
            foreach (var inst in session.QueryInstances(CimV2, "WQL", wql, op))
                using (inst)
                    if (ToInt(inst, "ProcessId") is { } pid && Str(inst, "Name") is { } name) map[pid] = name;
        }
        return map;
    }

    /// <summary>IIS の WMI プロバイダーから、プールで動いているサイト／パスを取得する（無ければ null）</summary>
    [SupportedOSPlatform("windows")]
    private string? GetIisSite(CimSession session, CimOperationOptions op, string host, string pool)
    {
        var key = (host.ToUpperInvariant(), pool);
        if (_iisSites.TryGetValue(key, out var cached) && DateTime.Now - cached.At < IisCacheDuration) return cached.Site;
        string? site = null;
        try
        {
            var wql = $"SELECT SiteName, Path FROM Application WHERE ApplicationPool = '{pool.Replace("\\", "\\\\").Replace("'", "\\'")}'";
            var apps = session.QueryInstances(WebAdministration, "WQL", wql, op)
                .Select(i =>
                {
                    using (i) return $"{Str(i, "SiteName")}{Str(i, "Path")}";
                })
                .Where(s => s.Length > 0)
                .Distinct()
                .ToList();
            site = apps.Count > 0 ? string.Join(", ", apps) : null;
        }
        catch (CimException ex)
        {
            // IIS 管理スクリプトとツール（WMI プロバイダー）が無い場合など
            logger.LogDebug(ex, "{Host} の IIS サイト情報を取得できませんでした", host);
        }
        _iisSites[key] = (site, DateTime.Now);
        return site;
    }

    private static bool IsW3wp(string? name) => string.Equals(name, "w3wp.exe", StringComparison.OrdinalIgnoreCase);

    [SupportedOSPlatform("windows")]
    private static string? Str(CimInstance inst, string name) => inst.CimInstanceProperties[name]?.Value as string;

    [SupportedOSPlatform("windows")]
    private static int? ToInt(CimInstance inst, string name) =>
        inst.CimInstanceProperties[name]?.Value is { } v ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : null;

    /// <summary>CIM の日時（DateTime または DMTF 形式の文字列）をローカル時刻にする</summary>
    internal static DateTime? ToLocalTime(object? value) => value switch
    {
        DateTime dt => dt.Kind == DateTimeKind.Utc ? dt.ToLocalTime() : dt,
        string s => ParseDmtf(s),
        _ => null,
    };

    /// <summary>DMTF 形式（yyyyMMddHHmmss.ffffff+UUU、UUU は UTC からの分）をローカル時刻にする</summary>
    internal static DateTime? ParseDmtf(string s)
    {
        if (s.Length < 25) return null;
        if (!DateTime.TryParseExact(s[..21], "yyyyMMddHHmmss.ffffff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) return null;
        if (!int.TryParse(s[21..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var offsetMinutes)) return null;
        return new DateTimeOffset(local, TimeSpan.FromMinutes(offsetMinutes)).LocalDateTime;
    }

    /// <summary>よくある失敗を分かりやすいメッセージにする</summary>
    private static string Describe(Exception ex) => ex switch
    {
        CimException { NativeErrorCode: NativeErrorCode.AccessDenied } => "アクセスが拒否されました（アカウントの権限を確認してください）",
        UnauthorizedAccessException => "アクセスが拒否されました（アカウントの権限を確認してください）",
        CimException cim => $"WinRM/DCOM で接続できません: {cim.Message}",
        _ => ex.Message,
    };
}
