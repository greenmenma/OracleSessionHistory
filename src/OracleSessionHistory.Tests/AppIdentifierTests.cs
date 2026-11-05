using OracleSessionHistory.Core.Processes;

namespace OracleSessionHistory.Tests;

public class AppIdentifierTests
{
    private static ClientProcessInfo Proc(string name, string? path, string? cmd, string? pool = null, string? site = null) =>
        new(100, name, path, cmd, null, null, null, pool, site);

    [Fact]
    public void w3wpはプール名とサイト()
    {
        var p = Proc("w3wp.exe", @"c:\windows\system32\inetsrv\w3wp.exe", "w3wp.exe -ap \"Foo\"", "Foo", "Default Web Site/foo");
        Assert.Equal(("IIS: Foo", "Default Web Site/foo"), AppIdentifier.Identify(p, "w3wp.exe", null, null));
    }

    [Fact]
    public void w3wpでサイト不明なら実行ファイルパス()
    {
        var p = Proc("w3wp.exe", @"c:\windows\system32\inetsrv\w3wp.exe", "w3wp.exe -ap \"Foo\"", "Foo");
        Assert.Equal(("IIS: Foo", @"c:\windows\system32\inetsrv\w3wp.exe"), AppIdentifier.Identify(p, "w3wp.exe", null, null));
    }

    [Fact]
    public void w3wpでプール名がなければコマンドラインから取る()
    {
        var p = Proc("w3wp.exe", null, "w3wp.exe -ap \"Bar\"");
        Assert.Equal("IIS: Bar", AppIdentifier.Identify(p, "w3wp.exe", null, null).AppName);
    }

    [Fact]
    public void dotnetは起動しているdll()
    {
        var p = Proc("dotnet.exe", @"C:\Program Files\dotnet\dotnet.exe", @"""C:\Program Files\dotnet\dotnet.exe"" D:\a\Foo.Api.dll --urls x");
        Assert.Equal(("Foo.Api.dll", @"D:\a\Foo.Api.dll"), AppIdentifier.Identify(p, "dotnet.exe", null, null));
    }

    [Fact]
    public void javaはjar()
    {
        var p = Proc("java.exe", @"C:\java\bin\java.exe", @"java.exe -Xmx1g -jar ""D:\b\x.jar"" run");
        Assert.Equal(("x.jar", @"D:\b\x.jar"), AppIdentifier.Identify(p, "JDBC Thin Client", null, null));
    }

    [Fact]
    public void その他は実行ファイル名とフォルダ()
    {
        var p = Proc("Foo.exe", @"D:\Apps\Foo\Foo.exe", @"D:\Apps\Foo\Foo.exe /batch");
        Assert.Equal(("Foo.exe", @"D:\Apps\Foo"), AppIdentifier.Identify(p, "Foo.exe", null, null));
    }

    private static ClientProcessInfo Sqlplus(params ParentProcessInfo[] ancestors) =>
        new(200, "sqlplus.exe", @"C:\oracle\bin\sqlplus.exe", "sqlplus.exe scott/********@DB @x.sql", null, null,
            ancestors.FirstOrDefault()?.Name, null, null, ancestors);

    private static ParentProcessInfo Parent(string name, string? cmd) => new(1, name, cmd, null);

    [Fact]
    public void sqlplusはcmdで実行したバッチ()
    {
        var p = Sqlplus(Parent("cmd.exe", @"C:\Windows\system32\cmd.exe /c D:\Batch\月次集計.bat"), Parent("jp1ajs2.exe", "jp1ajs2.exe"));
        Assert.Equal(("sqlplus.exe: 月次集計.bat", @"実行元: D:\Batch\月次集計.bat（cmd.exe ← jp1ajs2.exe）"),
            AppIdentifier.Identify(p, "sqlplus.exe", "SQL*Plus", null));
    }

    [Fact]
    public void sqlplusは空白を含むパスのバッチ()
    {
        var p = Sqlplus(Parent("cmd.exe", @"C:\Windows\system32\cmd.exe /c """"D:\My Batch\夜間.cmd"" arg"""));
        Assert.Equal(("sqlplus.exe: 夜間.cmd", @"実行元: D:\My Batch\夜間.cmd（cmd.exe）"), AppIdentifier.Identify(p, "sqlplus.exe", null, null));
    }

    [Fact]
    public void sqlplusを対話のcmdから実行した場合はcmd()
    {
        var p = Sqlplus(Parent("cmd.exe", @"""C:\Windows\system32\cmd.exe"" "), Parent("explorer.exe", @"C:\Windows\explorer.exe"));
        Assert.Equal(("sqlplus.exe: cmd.exe", "実行元: cmd.exe ← explorer.exe"), AppIdentifier.Identify(p, "sqlplus.exe", null, null));
    }

    [Fact]
    public void sqlplusをcmd経由で起動したプログラム()
    {
        var p = Sqlplus(Parent("cmd.exe", "cmd /c sqlplus scott/********@db @a.sql"), Parent("java.exe", "java -jar batch.jar"));
        Assert.Equal(("sqlplus.exe: java.exe", "実行元: cmd.exe ← java.exe"), AppIdentifier.Identify(p, "sqlplus.exe", null, null));
    }

    [Fact]
    public void sqlplusはPowerShellのスクリプト()
    {
        var p = Sqlplus(Parent("powershell.exe", @"powershell.exe -NoProfile -File D:\ps\run.ps1"), Parent("svchost.exe", null));
        Assert.Equal(("sqlplus.exe: run.ps1", @"実行元: D:\ps\run.ps1（powershell.exe ← svchost.exe）"), AppIdentifier.Identify(p, "sqlplus.exe", null, null));
    }

    [Fact]
    public void sqlplusでも実行元が取れなければ実行ファイル名とフォルダ()
    {
        Assert.Equal(("sqlplus.exe", @"C:\oracle\bin"), AppIdentifier.Identify(Sqlplus(), "sqlplus.exe", null, null));
    }

    [Fact]
    public void 実行元はsqlplus以外では使わない()
    {
        var p = new ClientProcessInfo(1, "Foo.exe", @"D:\Apps\Foo\Foo.exe", null, null, null, "cmd.exe", null, null,
            [Parent("cmd.exe", @"cmd /c D:\x.bat")]);
        Assert.Equal(("Foo.exe", @"D:\Apps\Foo"), AppIdentifier.Identify(p, "Foo.exe", null, null));
    }

    [Fact]
    public void パスが取れなければプロセス名()
    {
        var p = Proc("Foo.exe", null, null);
        Assert.Equal(("Foo.exe", null), AppIdentifier.Identify(p, "Foo.exe", null, null));
    }

    [Fact]
    public void プロセス情報なしはPROGRAM()
    {
        Assert.Equal(("sqlplus.exe", null), AppIdentifier.Identify(null, "sqlplus.exe", null, null));
        Assert.Equal(("(不明)", null), AppIdentifier.Identify(null, null, null, null));
        Assert.Equal(("(不明)", null), AppIdentifier.Identify(null, "  ", null, null));
    }

    [Fact]
    public void プロセス情報なしでもIIS_APPPOOLのOSユーザならプール名()
    {
        Assert.Equal(("IIS: Bar", null), AppIdentifier.Identify(null, "w3wp.exe", null, @"IIS APPPOOL\Bar"));
    }

    [Fact]
    public void モジュールがPROGRAMと異なれば補足に出す()
    {
        Assert.Equal(("販売.exe", "受注入力"), AppIdentifier.Identify(null, "販売.exe", "受注入力", null));
        Assert.Equal(("販売.exe", null), AppIdentifier.Identify(null, "販売.exe", "販売.exe", null));
    }
}
