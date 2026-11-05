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
    public async Task 疑似的にアクセス拒否になる端末がある()
    {
        await Assert.ThrowsAsync<ProcessInspectionException>(() => new DemoProcessInspector().InspectAsync("DEV-PC07", [1], Options, default));
    }
}

public class CimProcessInspectorHelperTests
{
    [Fact]
    public void DMTF形式の日時をローカル時刻にする()
    {
        // UTC+9 の 10:00 は UTC 01:00
        var expected = new DateTime(2026, 11, 5, 1, 0, 0, DateTimeKind.Utc).ToLocalTime();
        Assert.Equal(expected, CimProcessInspector.ParseDmtf("20261105100000.000000+540"));
        Assert.Null(CimProcessInspector.ParseDmtf("bad"));
    }

    [Theory]
    [InlineData(@"CORP\svc_osh", "CORP", "svc_osh")]
    [InlineData("svc_osh@corp.local", null, "svc_osh@corp.local")]
    public void アカウントをドメインとユーザに分ける(string input, string? domain, string user)
    {
        Assert.Equal((domain, user), CimProcessInspector.SplitUser(input));
    }
}
