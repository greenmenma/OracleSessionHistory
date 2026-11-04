using OpenDbSession.Core.DbLinks;
using OpenDbSession.Server.Support;

namespace OpenDbSession.Tests;

public class DbLinkSqlMatcherTests
{
    [Theory]
    [InlineData("PUBLIC.KOUKAI_LINK", "KOUKAI_LINK")]
    [InlineData("SALES.KOUKAI_LINK.WORLD", "KOUKAI_LINK")]
    [InlineData("X.koukai_link", "KOUKAI_LINK")]
    public void ShortName(string full, string expected) => Assert.Equal(expected, DbLinkSqlMatcher.ShortName(full));

    [Theory]
    [InlineData("select * from item@koukai_link", true)]
    [InlineData("select * from item@KOUKAI_LINK.WORLD where 1=1", true)]
    [InlineData("select * from item @ \"KOUKAI_LINK\"", true)]
    [InlineData("select * from item@KOUKAI_LINK2", false)]
    [InlineData("select 'KOUKAI_LINK' from dual", false)]
    public void ReferencesLink(string sql, bool expected) => Assert.Equal(expected, DbLinkSqlMatcher.ReferencesLink(sql, "KOUKAI_LINK"));

    [Theory]
    [InlineData("select * from ITEM_SYN where x=1", true)]
    [InlineData("select * from ITEM_SYN2", false)]
    [InlineData("select * from MY_ITEM_SYN", false)]
    public void ReferencesWord(string sql, bool expected) => Assert.Equal(expected, DbLinkSqlMatcher.ReferencesWord(sql, "ITEM_SYN"));
}

public class ConnectionStringMaskerTests
{
    private const string Cs = "User Id=MONITOR;Password=secret;Data Source=KOUKAI";

    [Fact]
    public void パスワードをマスクする()
    {
        var masked = ConnectionStringMasker.MaskPassword(Cs);
        Assert.DoesNotContain("secret", masked);
        Assert.Contains(ConnectionStringMasker.Mask, masked);
    }

    [Fact]
    public void マスクのまま保存した場合は元のパスワードを使う()
    {
        var merged = ConnectionStringMasker.Merge(ConnectionStringMasker.MaskPassword(Cs), Cs);
        Assert.Contains("secret", merged);
    }

    [Fact]
    public void パスワードを変更した場合は新しい値を使う()
    {
        var merged = ConnectionStringMasker.Merge("User Id=MONITOR;Password=newpass;Data Source=KOUKAI", Cs);
        Assert.Contains("newpass", merged);
    }
}
