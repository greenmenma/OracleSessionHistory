using OracleSessionHistory.Core.Processes;

namespace OracleSessionHistory.Tests;

public class HostBackoffTests
{
    private static readonly DateTime T0 = new(2026, 11, 5, 9, 0, 0);

    [Fact]
    public void 失敗するたびに待ち時間が1分_5分_15分_60分と伸びる()
    {
        var b = new HostBackoff();
        var now = T0;
        foreach (var minutes in new[] { 1, 5, 15, 60, 60 })
        {
            b.RecordFailure("WEB01", now);
            Assert.True(b.IsWaiting("WEB01", now.AddMinutes(minutes).AddSeconds(-1)));
            Assert.False(b.IsWaiting("WEB01", now.AddMinutes(minutes)));
            now = now.AddMinutes(minutes);
        }
    }

    [Fact]
    public void 最初の失敗だけをtrueで返す()
    {
        var b = new HostBackoff();
        Assert.True(b.RecordFailure("WEB01", T0));
        Assert.False(b.RecordFailure("web01", T0.AddMinutes(1)));
    }

    [Fact]
    public void 成功で回復しリセットされる()
    {
        var b = new HostBackoff();
        Assert.False(b.RecordSuccess("WEB01"));
        b.RecordFailure("WEB01", T0);
        Assert.True(b.RecordSuccess("WEB01"));
        Assert.False(b.IsWaiting("WEB01", T0));
        Assert.True(b.RecordFailure("WEB01", T0));
    }

    [Fact]
    public void 他のホストには影響しない()
    {
        var b = new HostBackoff();
        b.RecordFailure("WEB01", T0);
        Assert.False(b.IsWaiting("WEB02", T0));
    }
}
