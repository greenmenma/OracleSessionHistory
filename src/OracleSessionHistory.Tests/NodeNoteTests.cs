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

    private static ConnectionCountNode Counted(string kind, string label, int month1, params ConnectionCountNode[] children) =>
        new(kind, label, null, [], 0, null, 0, 0, 0, 0, 0, 0, 0, month1, 0, [], null, children);

    /// <summary>
    /// WEB01（販売.exe・w3wp.exe → Foo）、WEB02（販売.exe・w3wp.exe → Foo）、
    /// DB01（ORACLE.EXE → DB Link 基幹 → PC9 → 販売.exe）
    /// </summary>
    private static ConnectionCountNode[] AppTree() =>
    [
        Counted("machine", "WEB01", 6, Counted("exe", "販売.exe", 2), Counted("exe", "w3wp.exe", 4, Counted("app", "Foo", 4))),
        Counted("machine", "WEB02", 1, Counted("exe", "販売.exe", 1)),
        Counted("machine", "DB01", 3, Counted("exe", "ORACLE.EXE", 3,
            Counted("dblink", "基幹", 3, Counted("machine", "PC9", 3, Counted("exe", "販売.exe", 3))))),
        Counted("machine", "WEB03", 5, Counted("exe", "w3wp.exe", 5, Counted("app", "Foo", 5))),
    ];

    [Fact]
    public void アプリの行の日本語名はアプリ単位で共通にしプールは端末ごと()
    {
        var keyed = ConnectionTreeKeys.Apply(AppTree(), new Dictionary<string, NodeNoteDto>
        {
            [ConnectionTreeKeys.AppKeyOf("販売.exe")] = new("販売管理", null, null, []),
            ["machine:WEB02\nexe:販売.exe"] = new("古い名前", "メモ", Yamada, []),
            ["machine:WEB01\nexe:w3wp.exe\napp:Foo"] = new("受注", null, null, []),
        });
        var hanbai1 = keyed[0].Children[0];
        Assert.Equal((ConnectionTreeKeys.AppKeyOf("販売.exe"), "machine:WEB01\nexe:販売.exe"), (hanbai1.NameKey, hanbai1.NoteKey));
        Assert.Equal("販売管理", hanbai1.Note!.DisplayName);
        // アプリ単位の名前を優先し、行のメモ・担当者はそのまま
        Assert.Equal(("販売管理", "メモ"), (keyed[1].Children[0].Note!.DisplayName, keyed[1].Children[0].Note!.Memo));
        // DB Link 接続元のアプリも同じ名前
        Assert.Equal("販売管理", keyed[2].Children[0].Children[0].Children[0].Children[0].Note!.DisplayName);
        // w3wp.exe・プールは端末ごと
        var foo1 = keyed[0].Children[1].Children[0];
        Assert.Equal(foo1.NoteKey, foo1.NameKey);
        Assert.Equal("受注", foo1.Note!.DisplayName);
        Assert.Null(keyed[3].Children[0].Children[0].Note);
        Assert.Equal(keyed[0].Children[1].Key, keyed[0].Children[1].NameKey);
    }

    [Fact]
    public void アプリ別は実行ファイルとプールの下に端末を並べDB_Link経由は接続元の端末にする()
    {
        var notes = new Dictionary<string, NodeNoteDto>
        {
            [ConnectionTreeKeys.AppKeyOf("販売.exe")] = new("販売管理", null, null, []),
            ["machine:WEB02\nexe:販売.exe"] = new("古い名前", "メモ", Yamada, []),
        };
        var view = ReportEndpoints.ToAppView(ConnectionTreeKeys.Apply(AppTree(), notes), notes);
        Assert.Equal("w3wp.exe(Foo(WEB03,WEB01)),販売.exe(PC9,WEB01,WEB02)", Labels(view));

        var hanbai = view[1];
        Assert.Equal((6, "販売管理", ConnectionTreeKeys.AppKeyOf("販売.exe")), (hanbai.Month1, hanbai.Note!.DisplayName, hanbai.NameKey));
        Assert.Null(hanbai.NoteKey); // アプリの行にはメモ・担当者を設定しない
        Assert.Equal("DB Link 基幹（DB01）経由", hanbai.Children[0].Detail);
        // 端末の行は端末別の最下段と同じキー。アプリ単位の日本語名は端末の行では設定しない
        var web02 = hanbai.Children[2];
        Assert.Equal(("machine:WEB02\nexe:販売.exe", null, "メモ"), (web02.NoteKey, web02.NameKey, web02.Note!.Memo));
        Assert.Null(web02.Note.DisplayName);
        // w3wp.exe はアプリ単位の名前を付けず、プールの下の端末で設定する
        Assert.Null(view[0].NameKey);
        Assert.Null(view[0].Children[0].NameKey);
        Assert.Equal("machine:WEB03\nexe:w3wp.exe\napp:Foo", view[0].Children[0].Children[0].NameKey);
        Assert.Equal(9, view[0].Month1);
    }

    [Fact]
    public async Task 日本語名をアプリ単位のキーに保存し行の日本語名は外す()
    {
        _db.Database.Migrate();
        var appKey = ConnectionTreeKeys.AppKeyOf("販売.exe");
        await NodeNoteEndpoints.SaveAsync(_db, new NodeNoteInput("machine:WEB02\nexe:販売.exe", "古い名前", "メモ", Yamada, []), Now);

        var saved = await NodeNoteEndpoints.SaveAsync(_db, new NodeNoteInput("machine:WEB02\nexe:販売.exe", "販売管理", "メモ", Yamada, [], appKey), Now);
        Assert.Equal(("販売管理", "メモ"), (saved!.DisplayName, saved.Memo));
        var rows = _db.AppNodeNotes.AsNoTracking().OrderBy(n => n.NodeKey).ToList();
        Assert.Equal([(appKey, "販売管理", (string?)null), ("machine:WEB02\nexe:販売.exe", null, "メモ")],
            rows.Select(r => (r.NodeKey, r.DisplayName, r.Memo)).OrderBy(r => r.NodeKey, StringComparer.Ordinal));

        // アプリ別のアプリの行から：日本語名だけを保存する
        saved = await NodeNoteEndpoints.SaveAsync(_db, new NodeNoteInput(null, "販売システム", null, null, null, appKey), Now);
        Assert.Equal("販売システム", saved!.DisplayName);
        Assert.Null(await NodeNoteEndpoints.SaveAsync(_db, new NodeNoteInput(null, " ", null, null, null, appKey), Now));
        Assert.Equal(["machine:WEB02\nexe:販売.exe"], _db.AppNodeNotes.AsNoTracking().Select(n => n.NodeKey).ToList());
        Assert.NotNull(NodeNoteEndpoints.Validate(new NodeNoteInput(null, "x", null, null, null)));
        Assert.Null(NodeNoteEndpoints.Validate(new NodeNoteInput(null, "x", null, null, null, appKey)));
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
