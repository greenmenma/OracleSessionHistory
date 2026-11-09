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

    private static AssigneeDto P(string user, string name) => new($"{user}@corp.local", name);
    private static readonly AssigneeDto Yamada = P("yamada", "山田 太郎");
    private static readonly AssigneeDto Suzuki = P("suzuki", "鈴木 花子");
    private static readonly AssigneeDto Sato = P("sato", "佐藤 次郎");

    private static NodeNoteDto Note(AssigneeDto? main, params AssigneeDto[] subs) => new(null, null, main, subs);

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
            ["machine:WEB02\nexe:w3wp.exe\napp:IIS: Foo"] = new("受注管理", "受注の画面", Yamada, [Suzuki]),
        });
        Assert.Equal("machine:WEB01", keyed[0].Key);
        Assert.Equal("machine:WEB01\nexe:w3wp.exe\napp:IIS: Foo", keyed[0].Children[0].Children[0].Key);
        // 同じ名前のアプリでも端末が違えば別の行
        Assert.Null(keyed[0].Children[0].Children[0].Note);
        Assert.Equal("受注管理", keyed[1].Children[0].Children[0].Note!.DisplayName);
    }

    [Fact]
    public void 担当者のアカウントで絞り込むと担当の行の下はすべて残し上の段は経路として残す()
    {
        var keyed = ConnectionTreeKeys.Apply(Tree(), new Dictionary<string, NodeNoteDto>
        {
            ["machine:WEB01\nexe:w3wp.exe\napp:IIS: Bar"] = Note(Yamada, Suzuki),
            ["machine:WEB02"] = Note(Sato),
        });
        Assert.Equal("WEB01(w3wp.exe(IIS: Bar))", Labels(ConnectionTreeKeys.FilterByAssignee(keyed, Yamada.Account)));
        Assert.Equal("WEB01(w3wp.exe(IIS: Bar))", Labels(ConnectionTreeKeys.FilterByAssignee(keyed, "SUZUKI@corp.local"))); // サブ・大文字小文字は区別しない
        Assert.Equal("WEB02(w3wp.exe(IIS: Foo))", Labels(ConnectionTreeKeys.FilterByAssignee(keyed, Sato.Account)));
        Assert.Empty(ConnectionTreeKeys.FilterByAssignee(keyed, "山田 太郎")); // 表示名では探さない
        // 未設定：自分にも上の段にも担当者がいない最下段の行
        Assert.Equal("WEB01(w3wp.exe(IIS: Foo))", Labels(ConnectionTreeKeys.FilterByAssignee(keyed, ConnectionTreeKeys.Unassigned)));
        Assert.Equal(2, ConnectionTreeKeys.FilterByAssignee(keyed, " ").Count);
    }

    [Fact]
    public async Task 行の情報を保存し担当者を置き換えすべて空にすると削除する()
    {
        _db.Database.Migrate();
        var saved = await NodeNoteEndpoints.SaveAsync(_db,
            new NodeNoteInput("machine:WEB01", " 受注管理 ", "メモ", Yamada, [Suzuki, new(" ", "x"), Yamada, P("SUZUKI", "重複"), Sato]), Now);
        Assert.Equal("受注管理", saved!.DisplayName);
        Assert.Equal(Yamada, saved.MainAssignee);
        // アカウントの無いもの・メインと同じ人は除く（アカウントの大文字小文字は区別しない）
        Assert.Equal([Suzuki, Sato], saved.SubAssignees);

        // 担当者を入れ替える（同じ人をメインからサブへ移しても一意インデックスに当たらない）
        saved = await NodeNoteEndpoints.SaveAsync(_db, new NodeNoteInput("machine:WEB01", null, null, Suzuki, [Yamada]), Now);
        Assert.Equal(Suzuki, saved!.MainAssignee);
        Assert.Equal([Yamada], saved.SubAssignees);
        Assert.Equal(2, _db.AppNodeAssignees.AsNoTracking().Count());

        Assert.Null(await NodeNoteEndpoints.SaveAsync(_db, new NodeNoteInput("machine:WEB01", " ", "", null, []), Now));
        Assert.Empty(_db.AppNodeNotes.AsNoTracking());
        Assert.Empty(_db.AppNodeAssignees.AsNoTracking()); // 担当者も削除される
        // 未設定の行を空にしてもエラーにしない
        Assert.Null(await NodeNoteEndpoints.SaveAsync(_db, new NodeNoteInput("machine:WEB02", null, null, null, null), Now));
    }

    [Fact]
    public void 長すぎる値や多すぎるサブの担当者は保存しない()
    {
        NodeNoteInput Input(string key = "machine:WEB01", string? name = null, string? memo = null, AssigneeDto? main = null, AssigneeDto[]? subs = null) =>
            new(key, name, memo, main, subs);
        Assert.Null(NodeNoteEndpoints.Validate(Input(main: Yamada, subs: [Suzuki])));
        Assert.NotNull(NodeNoteEndpoints.Validate(Input(key: "")));
        Assert.NotNull(NodeNoteEndpoints.Validate(Input(key: new string('a', AppNodeNoteLimits.NodeKeyLength + 1))));
        Assert.NotNull(NodeNoteEndpoints.Validate(Input(name: new string('a', AppNodeNoteLimits.DisplayNameLength + 1))));
        Assert.NotNull(NodeNoteEndpoints.Validate(Input(memo: new string('a', AppNodeNoteLimits.MemoLength + 1))));
        Assert.NotNull(NodeNoteEndpoints.Validate(Input(main: new(new string('a', AppNodeNoteLimits.AccountLength + 1), "x"))));
        Assert.NotNull(NodeNoteEndpoints.Validate(Input(subs: Enumerable.Range(1, AppNodeNoteLimits.MaxSubAssignees + 1).Select(i => P($"u{i}", $"u{i}")).ToArray())));
    }

    [Fact]
    public void 文字列の担当者は引き継がず担当者だけの行は削除する()
    {
        var migrator = _db.GetService<IMigrator>();
        migrator.Migrate(_db.Database.GetMigrations().Single(m => m.EndsWith("_AddAppNodeNotes")));
        _db.Database.ExecuteSqlRaw(
            "INSERT INTO AppNodeNotes (NodeKey, DisplayName, Memo, MainAssignee, SubAssignees, UpdatedAt) VALUES " +
            "('machine:WEB01', '受注管理', NULL, '山田', '鈴木', '2026-11-09 10:00:00'), " +
            "('machine:WEB02', NULL, NULL, '佐藤', NULL, '2026-11-09 10:00:00')");

        migrator.Migrate();
        var note = _db.AppNodeNotes.AsNoTracking().Include(n => n.Assignees).Single();
        Assert.Equal(("machine:WEB01", "受注管理"), (note.NodeKey, note.DisplayName));
        Assert.Empty(note.Assignees);
    }
}
