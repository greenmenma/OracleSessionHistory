namespace OracleSessionHistory.Core.Processes;

/// <summary>
/// Demo 監視対象用の疑似プロセス情報（DemoWorld の端末名に合わせて返す）。
/// </summary>
public sealed class DemoProcessInspector : IProcessInspector
{
    public Task<IReadOnlyList<ClientProcessInfo>> InspectAsync(string host, IReadOnlyCollection<int> pids, ProcessInspectorOptions options, CancellationToken ct)
    {
        // 画面で失敗表示を確認できるよう、開発用端末はアクセス拒否にする
        if (host.StartsWith("DEV-PC07", StringComparison.OrdinalIgnoreCase))
            throw new ProcessInspectionException("アクセスが拒否されました（デモ用の疑似エラー）");

        var started = DateTime.Now.AddDays(-1);
        var list = pids.Distinct().Select(pid => Profile(host, pid, started)).OfType<ClientProcessInfo>().ToList();
        return Task.FromResult<IReadOnlyList<ClientProcessInfo>>(list);
    }

    public Task<string> TestAsync(string host, ProcessInspectorOptions options, CancellationToken ct) =>
        Task.FromResult($"{host}（デモ）に接続しました");

    internal static ClientProcessInfo? Profile(string host, int pid, DateTime started)
    {
        var h = host.ToUpperInvariant();
        if (h.StartsWith("APSV"))
        {
            // 同じサーバーで 2 つのプールが動いている想定
            var (pool, site) = pid % 2 == 0 ? ("KoukaiWeb", "Default Web Site/koukai") : ("KoukaiApi", "Default Web Site/koukai-api");
            return new(pid, "w3wp.exe", @"c:\windows\system32\inetsrv\w3wp.exe",
                $"c:\\windows\\system32\\inetsrv\\w3wp.exe -ap \"{pool}\" -v \"v4.0\" -l \"webengine4.dll\"",
                $"IIS APPPOOL\\{pool}", started, "svchost.exe", pool, site);
        }
        if (h.StartsWith("PC-SALES"))
            return new(pid, "販売管理.exe", @"C:\Program Files\Sales\販売管理.exe", @"""C:\Program Files\Sales\販売管理.exe""",
                @"CORP\" + h.ToLowerInvariant(), started, "explorer.exe", null, null);
        if (h.StartsWith("PC-ACC"))
            return new(pid, "会計システム.exe", @"D:\Apps\Account\会計システム.exe", @"D:\Apps\Account\会計システム.exe",
                @"CORP\sato", started, "explorer.exe", null, null);
        if (h == "BATCHSV")
            return new(pid, "JP1Batch.exe", @"D:\Batch\Nightly\JP1Batch.exe", @"D:\Batch\Nightly\JP1Batch.exe /job:集計",
                @"CORP\jp1admin", started, "jp1ajs2.exe", null, null);
        if (h == "DEV-PC08")
            return new(pid, "java.exe", @"C:\sqldeveloper\jdk\bin\java.exe", @"java.exe -Xmx1g -jar ""C:\sqldeveloper\sqldeveloper.jar""",
                @"CORP\dev08", started, "sqldeveloper64W.exe", null, null);
        // DB Link 接続元 DB を使っている端末（DemoWorld.LinkOrigins）
        if (h == "KIKAN-AP01")
            return new(pid, "w3wp.exe", @"c:\windows\system32\inetsrv\w3wp.exe",
                @"c:\windows\system32\inetsrv\w3wp.exe -ap ""JuchuWeb"" -v ""v4.0""",
                @"IIS APPPOOL\JuchuWeb", started, "svchost.exe", "JuchuWeb", "Default Web Site/juchu");
        if (h == "KIKAN-AP02")
            return new(pid, "在庫照会.exe", @"D:\Kikan\Zaiko\在庫照会.exe", @"D:\Kikan\Zaiko\在庫照会.exe",
                @"CORP\kikan02", started, "explorer.exe", null, null);
        if (h == "KIKAN-BAT")
            return new(pid, "日次連携.exe", @"D:\Batch\Renkei\日次連携.exe", @"D:\Batch\Renkei\日次連携.exe /daily",
                @"CORP\batch", started, "jp1ajs2.exe", null, null);
        // その他の端末は、プロセスが終了済みとして扱う
        return null;
    }
}
