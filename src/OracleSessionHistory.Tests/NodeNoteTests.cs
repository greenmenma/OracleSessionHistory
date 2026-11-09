using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OracleSessionHistory.Core.Data;
using OracleSessionHistory.Core.Entities;
using OracleSessionHistory.Server.Api;

namespace OracleSessionHistory.Tests;

/// <summary>接続アプリ表の行の情報（日本語名・メモ・対応担当者）</summary>
public sealed class NodeNoteTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 9, 10, 0, 0);
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly AppDbContext _db;

    public NodeNoteTests()
    {
        _conn.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_conn, o => o.MigrationsAssembly(AppDatabase.SqliteMigrationsAssembly)).Options);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    private static ConnectionCountNode Node(string kind, string label, params ConnectionCountNode[] children) =>
        new(kind, label, null, [], 0, null, 0, 0, 0, 0, 0, 0, 0, 0, 0, [], null, children);

    private static NodeNoteDto Note(string? main, params string[] subs) => new(null, null, main, subs);

    /// <summary>WEB01（w3wp.exe → Foo・Bar）、WEB02（w3wp.exe → Foo）</summary>
    private static ConnectionCountNode[] Tree() =>
    [
        Node("machine", "WEB01", Node("exe", "w3wp.exe", Node("app", "IIS: Foo"), Node("app", "IIS: Bar"))),
        Node("machine", "WEB02", Node("exe", "w3wp.exe", Node("app", "IIS: Foo"))),
    ];

    private static string Labels(IEnumerable<ConnectionCountNode> nodes) =>
        string.Join(",", nodes.Select(n => n.Children.Count == 0 ? n.Label : $"{n.Label}({Labels(n.Children)})"));

    [Fact]
    public void 行のキーは上の段からの経路で作り設定した情報を付ける()
    {
        var keyed = ConnectionTreeKeys.Apply(Tree(), new Dictionary<string, NodeNoteDto>
        {
            ["machine:WEB02\nexe:w3wp.exe\napp:IIS: Foo"] = new("受注管理", "受注の画面", "山田", ["鈴木"]),
        });
        Assert.Equal("machine:WEB01", keyed[0].Key);
        Assert.Equal("machine:WEB01\nexe:w3wp.exe\napp:IIS: Foo", keyed[0].Children[0].Children[0].Key);
        // 同じ名前のアプリでも端末が違えば別の行
        Assert.Null(keyed[0].Children[0].Children[0].Note);
        Assert.Equal("受注管理", keyed[1].Children[0].Children[0].Note!.DisplayName);
    }

    [Fact]
    public void 担当者で絞り込むと担当の行の下はすべて残し上の段は経路として残す()
    {
        var keyed = ConnectionTreeKeys.Apply(Tree(), new Dictionary<string, NodeNoteDto>
        {
            ["machine:WEB01\nexe:w3wp.exe\napp:IIS: Bar"] = Note("山田", "鈴木"),
            ["machine:WEB02"] = Note("佐藤"),
        });
        Assert.Equal("WEB01(w3wp.exe(IIS: Bar))", Labels(ConnectionTreeKeys.FilterByAssignee(keyed, "山田")));
        Assert.Equal("WEB01(w3wp.exe(IIS: Bar))", Labels(ConnectionTreeKeys.FilterByAssignee(keyed, "鈴木"))); // サブでも探す
        Assert.Equal("WEB02(w3wp.exe(IIS: Foo))", Labels(ConnectionTreeKeys.FilterByAssignee(keyed, "佐藤")));
        Assert.Empty(ConnectionTreeKeys.FilterByAssignee(keyed, "田中"));
        // 未設定：自分にも上の段にも担当者がいない最下段の行
        Assert.Equal("WEB01(w3wp.exe(IIS: Foo))", Labels(ConnectionTreeKeys.FilterByAssignee(keyed, ConnectionTreeKeys.Unassigned)));
        Assert.Equal(2, ConnectionTreeKeys.FilterByAssignee(keyed, " ").Count);
    }

    [Fact]
    public async Task 行の情報を保存しすべて空にすると削除する()
    {
        _db.Database.Migrate();
        var saved = await NodeNoteEndpoints.SaveAsync(_db,
            new NodeNoteInput("machine:WEB01", " 受注管理 ", "メモ", " 山田 ", ["鈴木", " ", "山田", "鈴木", "佐藤"]), Now);
        Assert.Equal("受注管理", saved!.DisplayName);
        Assert.Equal("山田", saved.MainAssignee);
        Assert.Equal(["鈴木", "佐藤"], saved.SubAssignees); // 空・重複・メインと同じ名前は除く
        Assert.Equal(["鈴木", "佐藤"], _db.AppNodeNotes.AsNoTracking().Single().SubAssigneeList);

        // サブだけにしても残る
        Assert.NotNull(await NodeNoteEndpoints.SaveAsync(_db, new NodeNoteInput("machine:WEB01", null, null, null, ["鈴木"]), Now));
        Assert.Null(await NodeNoteEndpoints.SaveAsync(_db, new NodeNoteInput("machine:WEB01", " ", "", null, [" "]), Now));
        Assert.Empty(_db.AppNodeNotes.AsNoTracking());
        // 未設定の行を空にしてもエラーにしない
        Assert.Null(await NodeNoteEndpoints.SaveAsync(_db, new NodeNoteInput("machine:WEB02", null, null, null, null), Now));
    }

    [Fact]
    public void 長すぎる値や多すぎるサブの担当者は保存しない()
    {
        NodeNoteInput Input(string key = "machine:WEB01", string? name = null, string? memo = null, string? main = null, string[]? subs = null) =>
            new(key, name, memo, main, subs);
        Assert.Null(NodeNoteEndpoints.Validate(Input(main: "山田", subs: ["鈴木"])));
        Assert.NotNull(NodeNoteEndpoints.Validate(Input(key: "")));
        Assert.NotNull(NodeNoteEndpoints.Validate(Input(key: new string('a', AppNodeNoteLimits.NodeKeyLength + 1))));
        Assert.NotNull(NodeNoteEndpoints.Validate(Input(name: new string('a', AppNodeNoteLimits.DisplayNameLength + 1))));
        Assert.NotNull(NodeNoteEndpoints.Validate(Input(memo: new string('a', AppNodeNoteLimits.MemoLength + 1))));
        Assert.NotNull(NodeNoteEndpoints.Validate(Input(subs: [new string('a', AppNodeNoteLimits.AssigneeLength + 1)])));
        Assert.NotNull(NodeNoteEndpoints.Validate(Input(subs: Enumerable.Range(1, AppNodeNoteLimits.MaxSubAssignees + 1).Select(i => $"u{i}").ToArray())));
    }

    [Fact]
    public void 以前の担当者はメインの担当者として引き継ぐ()
    {
        var migrator = _db.GetService<IMigrator>();
        var previous = _db.Database.GetMigrations().Single(m => m.EndsWith("_AddAppAssignees"));
        migrator.Migrate(previous);
        _db.Database.ExecuteSqlRaw("INSERT INTO AppAssignees (NodeKey, Assignee, UpdatedAt) VALUES ('machine:WEB01', '山田', '2026-11-09 10:00:00')");

        migrator.Migrate();
        var note = _db.AppNodeNotes.AsNoTracking().Single();
        Assert.Equal(("machine:WEB01", "山田"), (note.NodeKey, note.MainAssignee));
    }
}
