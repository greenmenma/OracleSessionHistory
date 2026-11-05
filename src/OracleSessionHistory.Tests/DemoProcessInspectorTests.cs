using OracleSessionHistory.Core.Processes;

namespace OracleSessionHistory.Tests;

public class DemoProcessInspectorTests
{
    private static readonly ProcessInspectorOptions Options = new(null, null, null, TimeSpan.FromSeconds(5));

    [Fact]
    public async Task IISサーバーはプール名付きのw3wpを返す()
    {
        var r = await new DemoProcessInspector().InspectAsync("APSV01", [1001, 1002], Options, default);
        Assert.Equal(2, r.Count);
        Assert.All(r, p => Assert.Equal("w3wp.exe", p.Name));
        Assert.All(r, p => Assert.NotNull(p.IisAppPool));
        Assert.Equal([1001, 1002], r.Select(p => p.ProcessId).Order());
    }

    [Fact]
    public async Task バッチサーバーのsqlplusは実行元付き()
    {
        var p = (await new DemoProcessInspector().InspectAsync("BATCHSV02", [3001], Options, default)).Single();
        Assert.Equal("sqlplus.exe", p.Name);
        Assert.Equal(["cmd.exe", "jp1ajs2.exe"], p.Ancestors!.Select(a => a.Name));
        Assert.Equal("sqlplus.exe: 月次集計.bat", AppIdentifier.Identify(p, "sqlplus.exe", null, null).AppName);
    }

    [Fact]
    public async Task 疑似的にアクセス拒否になる端末がある()
    {
        await Assert.ThrowsAsync<ProcessInspectionException>(() => new DemoProcessInspector().InspectAsync("DEV-PC07", [1], Options, default));
    }
}

public class CimProcessInspectorHelperTests
{
    private static readonly DateTime T = new(2026, 11, 5, 9, 0, 0);

    [Fact]
    public void 実行元は親から順にたどる()
    {
        var child = new ClientProcessInfo(30, "sqlplus.exe", null, null, null, T, null, null, null);
        var parents = new Dictionary<int, int> { [30] = 20 };
        var known = new Dictionary<int, (ParentProcessInfo, int?)>
        {
            [20] = (new(20, "cmd.exe", "cmd /c x.bat", T.AddMinutes(-1)), 10),
            [10] = (new(10, "jp1ajs2.exe", null, T.AddDays(-1)), 4),
            [4] = (new(4, "services.exe", null, T.AddDays(-2)), 0),
        };

        Assert.Equal(["cmd.exe", "jp1ajs2.exe", "services.exe"], CimProcessInspector.ChainOf(child, parents, known).Select(a => a.Name));
    }

    [Fact]
    public void 親の方が後から開始していればPIDの再利用としてそこで止める()
    {
        var child = new ClientProcessInfo(30, "sqlplus.exe", null, null, null, T, null, null, null);
        var parents = new Dictionary<int, int> { [30] = 20 };
        var known = new Dictionary<int, (ParentProcessInfo, int?)>
        {
            [20] = (new(20, "cmd.exe", null, T.AddMinutes(-1)), 10),
            [10] = (new(10, "notepad.exe", null, T.AddMinutes(5)), 30), // 親が終了した後に別のプロセスが同じ PID を使った
        };

        Assert.Equal(["cmd.exe"], CimProcessInspector.ChainOf(child, parents, known).Select(a => a.Name));
    }

    [Fact]
    public void DMTF形式の日時をローカル時刻にする()
    {
        // UTC+9 の 10:00 は UTC 01:00
        var expected = new DateTime(2026, 11, 5, 1, 0, 0, DateTimeKind.Utc).ToLocalTime();
        Assert.Equal(expected, CimProcessInspector.ParseDmtf("20261105100000.000000+540"));
        Assert.Null(CimProcessInspector.ParseDmtf("bad"));
    }

    [Fact]
    public async Task 不正な端末名は名前解決できないとして扱う()
    {
        Assert.False(await CimProcessInspector.CanResolveAsync(new string('a', 300), default));
    }

    [Theory]
    [InlineData(@"CORP\svc_osh", "CORP", "svc_osh")]
    [InlineData("svc_osh@corp.local", null, "svc_osh@corp.local")]
    public void アカウントをドメインとユーザに分ける(string input, string? domain, string user)
    {
        Assert.Equal((domain, user), CimProcessInspector.SplitUser(input));
    }
}
