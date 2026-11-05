using OracleSessionHistory.Core.Processes;

namespace OracleSessionHistory.Tests;

public class ClientProcessRulesTests
{
    [Theory]
    [InlineData(@"CORP\WEB01", "WEB01")]
    [InlineData("web01.corp.local", "web01.corp.local")]
    [InlineData(" WEB01 ", "WEB01")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData(@"CORP\", null)]
    public void HostOfはドメインを除いたホスト名(string? machine, string? expected)
    {
        Assert.Equal(expected, ClientProcessRules.HostOf(machine));
    }

    [Theory]
    [InlineData("1234:5678", 1234)]
    [InlineData("1234", 1234)]
    [InlineData(" 42 ", 42)]
    [InlineData("abc", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("0", null)]
    public void ProcessIdOfは先頭の数値(string? process, int? expected)
    {
        Assert.Equal(expected, ClientProcessRules.ProcessIdOf(process));
    }

    [Theory]
    [InlineData("c:\\windows\\system32\\inetsrv\\w3wp.exe -ap \"Foo Pool\" -v \"v4.0\" -l \"webengine4.dll\"", "Foo Pool")]
    [InlineData("w3wp.exe -ap Foo -v v4.0", "Foo")]
    [InlineData("C:\\Windows\\SysWOW64\\inetsrv\\w3wp.exe -debug -ap \"DefaultAppPool\"", "DefaultAppPool")]
    [InlineData("w3wp.exe -v v4.0", null)]
    [InlineData(null, null)]
    public void AppPoolOfはapオプションの値(string? commandLine, string? expected)
    {
        Assert.Equal(expected, ClientProcessRules.AppPoolOf(commandLine));
    }

    [Theory]
    [InlineData("PC-123", "PC-*,KIOSK*", true)]
    [InlineData("kiosk01", "PC-*, KIOSK*", true)]
    [InlineData("WEB01", "PC-*", false)]
    [InlineData("WEB01", "WEB01", true)]
    [InlineData("WEB011", "WEB01", false)]
    [InlineData("WEB01", null, false)]
    [InlineData("WEB01", " , ", false)]
    public void IsExcludedはワイルドカードで判定(string host, string? patterns, bool expected)
    {
        Assert.Equal(expected, ClientProcessRules.IsExcluded(host, patterns));
    }

    private static readonly DateTime Logon = new(2026, 11, 5, 9, 0, 0);

    private static ClientProcessInfo Proc(string name, DateTime? started) =>
        new(100, name, null, null, null, started, null, null, null);

    [Fact]
    public void プロセス開始がログオンより5分以上後なら不一致()
    {
        Assert.True(ClientProcessRules.IsMismatch(Proc("w3wp.exe", Logon.AddMinutes(6)), "w3wp.exe", Logon));
    }

    [Fact]
    public void 時計のずれ5分未満は一致扱い()
    {
        Assert.False(ClientProcessRules.IsMismatch(Proc("w3wp.exe", Logon.AddMinutes(4)), "w3wp.exe", Logon));
        Assert.False(ClientProcessRules.IsMismatch(Proc("w3wp.exe", Logon.AddDays(-3)), "w3wp.exe", Logon));
    }

    [Fact]
    public void exe形式のPROGRAMとプロセス名が違えば不一致()
    {
        Assert.True(ClientProcessRules.IsMismatch(Proc("java.exe", Logon.AddHours(-1)), "w3wp.exe", Logon));
        Assert.False(ClientProcessRules.IsMismatch(Proc("W3WP.EXE", Logon.AddHours(-1)), "w3wp.exe", Logon));
    }

    [Fact]
    public void exe形式でないPROGRAMは名前で判定しない()
    {
        Assert.False(ClientProcessRules.IsMismatch(Proc("java.exe", Logon.AddHours(-1)), "JDBC Thin Client", Logon));
        Assert.False(ClientProcessRules.IsMismatch(Proc("java.exe", null), null, Logon));
    }
}
