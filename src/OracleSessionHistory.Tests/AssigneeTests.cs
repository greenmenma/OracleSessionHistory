using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OracleSessionHistory.Core.Data;
using OracleSessionHistory.Core.Entities;
using OracleSessionHistory.Server.Api;

namespace OracleSessionHistory.Tests;

/// <summary>接続アプリ表の対応担当者</summary>
public sealed class AssigneeTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly AppDbContext _db;

    public AssigneeTests()
    {
        _conn.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_conn, o => o.MigrationsAssembly(AppDatabase.SqliteMigrationsAssembly)).Options);
        _db.Database.Migrate();
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    private static ConnectionCountNode Node(string kind, string label, params ConnectionCountNode[] children) =>
        new(kind, label, null, [], 0, null, 0, 0, 0, 0, 0, 0, 0, 0, 0, [], null, children);

    [Fact]
    public void 行のキーは上の段からの経路で作り担当者を付ける()
    {
        var tree = new[]
        {
            Node("machine", "WEB01", Node("exe", "w3wp.exe", Node("app", "IIS: Foo"))),
            Node("machine", "WEB02", Node("exe", "w3wp.exe", Node("app", "IIS: Foo"))),
        };
        var keyed = ConnectionTreeKeys.Apply(tree, new Dictionary<string, string> { ["machine:WEB02\nexe:w3wp.exe\napp:IIS: Foo"] = "山田" });

        Assert.Equal("machine:WEB01", keyed[0].Key);
        Assert.Equal("machine:WEB01\nexe:w3wp.exe\napp:IIS: Foo", keyed[0].Children[0].Children[0].Key);
        // 同じ名前のアプリでも端末が違えば別の行
        Assert.Null(keyed[0].Children[0].Children[0].Assignee);
        Assert.Equal("山田", keyed[1].Children[0].Children[0].Assignee);
    }

    [Fact]
    public async Task 担当者を保存し空にすると外す()
    {
        var now = new DateTime(2026, 11, 9, 10, 0, 0);
        Assert.Equal("山田", await AssigneeEndpoints.SaveAsync(_db, new AssigneeInput("machine:WEB01", " 山田 "), now));
        Assert.Equal("鈴木", await AssigneeEndpoints.SaveAsync(_db, new AssigneeInput("machine:WEB01", "鈴木"), now));
        Assert.Equal("鈴木", _db.AppAssignees.AsNoTracking().Single().Assignee);

        Assert.Null(await AssigneeEndpoints.SaveAsync(_db, new AssigneeInput("machine:WEB01", "  "), now));
        Assert.Empty(_db.AppAssignees.AsNoTracking());
        // 未設定の行を空にしてもエラーにしない
        Assert.Null(await AssigneeEndpoints.SaveAsync(_db, new AssigneeInput("machine:WEB02", null), now));
    }

    [Fact]
    public void 長すぎるキー_担当者は保存しない()
    {
        Assert.Null(AssigneeEndpoints.Validate(new AssigneeInput("machine:WEB01", "山田")));
        Assert.NotNull(AssigneeEndpoints.Validate(new AssigneeInput("", "山田")));
        Assert.NotNull(AssigneeEndpoints.Validate(new AssigneeInput(new string('a', AppAssigneeLimits.NodeKeyLength + 1), "山田")));
        Assert.NotNull(AssigneeEndpoints.Validate(new AssigneeInput("machine:WEB01", new string('a', AppAssigneeLimits.AssigneeLength + 1))));
    }
}
