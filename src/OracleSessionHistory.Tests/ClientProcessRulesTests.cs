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

    [Theory]
    [InlineData("1234", "JDBC Thin Client", null, true)]
    [InlineData("1234", "java.exe", "jdbcthin", true)]
    [InlineData("1234", "w3wp.exe", "ODP.NET Managed Driver", false)]
    [InlineData("4321", "JDBC Thin Client", null, false)]
    public void JDBC_Thinの既定のPROCESSは偽のPID(string process, string program, string? driver, bool expected)
    {
        Assert.Equal(expected, ClientProcessRules.IsDriverDefaultProcess(process, program, driver));
    }

    [Theory]
    [InlineData("sqlplus.exe scott/tiger@KOUKAI @D:\\sql\\x.sql", "sqlplus.exe scott/********@KOUKAI @D:\\sql\\x.sql")]
    [InlineData("sqlplus -s scott/tiger", "sqlplus -s scott/********")]
    [InlineData("sqlldr userid=scott/tiger@db control=x.ctl", "sqlldr userid=scott/********@db control=x.ctl")]
    [InlineData("cmd /c \"sqlplus scott/tiger@db @a.sql\"", "cmd /c \"sqlplus scott/********@db @a.sql\"")]
    [InlineData("C:\\app\\Foo.exe scott/tiger@db", "C:\\app\\Foo.exe scott/********@db")]
    [InlineData("sqlplus /nolog", "sqlplus /nolog")]
    [InlineData("sqlplus / as sysdba", "sqlplus / as sysdba")]
    [InlineData("dotnet bin/app.dll", "dotnet bin/app.dll")]
    [InlineData("java -jar http://host/x.jar", "java -jar http://host/x.jar")]
    [InlineData("sqlplus scott/\"p@ss w\"@db", "sqlplus scott/********@db")]
    [InlineData("sqlplus \"scott\"/tiger@db", "sqlplus \"scott\"/********@db")]
    [InlineData("java -Durl=jdbc:oracle:thin:scott/tiger@host:1521:orcl -jar a.jar", "java -Durl=jdbc:oracle:thin:scott/********@host:1521:orcl -jar a.jar")]
    [InlineData("app.exe \"User Id=scott;Password=tiger;Data Source=db\"", "app.exe \"User Id=scott;Password=********;Data Source=db\"")]
    [InlineData("app.exe /pwd=tiger", "app.exe /pwd=********")]
    [InlineData("app.exe --password tiger --user scott", "app.exe --password ******** --user scott")]
    [InlineData("app.exe -password=tiger", "app.exe -password=********")]
    // 隠しすぎない（ファイルのパス・exp という単語）
    [InlineData("sqlldr userid=scott/tiger control=ctl/x.ctl data=data/in.csv", "sqlldr userid=scott/******** control=ctl/x.ctl data=data/in.csv")]
    [InlineData("dotnet bin/app.dll --mode exp", "dotnet bin/app.dll --mode exp")]
    [InlineData(null, null)]
    public void MaskCredentialsはユーザとパスワードのパスワードを隠す(string? commandLine, string? expected)
    {
        Assert.Equal(expected, ClientProcessRules.MaskCredentials(commandLine));
    }

    [Fact]
    public void Oracleのツールを起動した親プロセスは接続先が無くてもパスワードを隠す()
    {
        // バッチに引数で渡す場合（cmd.exe /c x.bat scott/tiger）
        Assert.Equal(@"cmd.exe /c D:\x.bat scott/********", ClientProcessRules.MaskCredentials(@"cmd.exe /c D:\x.bat scott/tiger", oracleTool: true));
        Assert.Equal(@"cmd.exe /c D:\x.bat bin/x", ClientProcessRules.MaskCredentials(@"cmd.exe /c D:\x.bat bin/x"));
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
