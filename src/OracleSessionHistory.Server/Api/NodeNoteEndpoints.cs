using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using OracleSessionHistory.Core.Data;
using OracleSessionHistory.Core.Entities;

namespace OracleSessionHistory.Server.Api;

/// <summary>接続アプリ表の行に設定した情報（画面に返す形）</summary>
public sealed record NodeNoteDto(string? DisplayName, string? Memo, string? MainAssignee, IReadOnlyList<string> SubAssignees)
{
    public static NodeNoteDto From(AppNodeNote n) => new(n.DisplayName, n.Memo, n.MainAssignee, n.SubAssigneeList);

    /// <summary>メイン・サブの担当者すべて（画面には返さない）</summary>
    [JsonIgnore]
    public IEnumerable<string> Assignees => MainAssignee is null ? SubAssignees : [MainAssignee, .. SubAssignees];
}

/// <param name="NodeKey">接続アプリ表の行のキー（<see cref="ConnectionCountNode.Key"/>）</param>
/// <param name="SubAssignees">サブの担当者（空の名前・重複・メインと同じ名前は除く）</param>
public sealed record NodeNoteInput(string NodeKey, string? DisplayName, string? Memo, string? MainAssignee, IReadOnlyList<string>? SubAssignees);

/// <summary>接続アプリ表の行のキーと、担当者での絞り込み</summary>
public static class ConnectionTreeKeys
{
    /// <summary>段の区切り（名前に改行は含まれないため、別の経路と同じキーにならない）</summary>
    public const char Separator = '\n';

    /// <summary>担当者での絞り込みで、担当者が決まっていない行を表す値</summary>
    public const string Unassigned = "(未設定)";

    /// <summary>上の段のキーに、この段の「種類:名前」をつなげる</summary>
    public static string Of(string? parentKey, string kind, string label) =>
        string.IsNullOrEmpty(parentKey) ? $"{kind}:{label}" : $"{parentKey}{Separator}{kind}:{label}";

    /// <summary>木の各行にキーと設定した情報を付ける</summary>
    public static IReadOnlyList<ConnectionCountNode> Apply(
        IReadOnlyList<ConnectionCountNode> nodes, IReadOnlyDictionary<string, NodeNoteDto> notes, string? parentKey = null) =>
        nodes.Select(n =>
        {
            var key = Of(parentKey, n.Kind, n.Label);
            return n with { Key = key, Note = notes.GetValueOrDefault(key), Children = Apply(n.Children, notes, key) };
        }).ToList();

    /// <summary>
    /// 担当者で絞り込む。担当者の行はその下の段をすべて残し、上の段は経路として残す。
    /// <see cref="Unassigned"/> は、自分にも上の段にも担当者がいない最下段の行（とその経路）を残す
    /// </summary>
    public static IReadOnlyList<ConnectionCountNode> FilterByAssignee(IReadOnlyList<ConnectionCountNode> nodes, string? assignee)
    {
        if (string.IsNullOrWhiteSpace(assignee)) return nodes;
        var name = assignee.Trim();
        return name == Unassigned ? FilterUnassigned(nodes) : FilterNamed(nodes, name);
    }

    private static List<ConnectionCountNode> FilterNamed(IReadOnlyList<ConnectionCountNode> nodes, string name)
    {
        var result = new List<ConnectionCountNode>();
        foreach (var n in nodes)
        {
            if (n.Note?.Assignees.Contains(name, StringComparer.OrdinalIgnoreCase) == true) result.Add(n);
            else if (FilterNamed(n.Children, name) is { Count: > 0 } children) result.Add(n with { Children = children });
        }
        return result;
    }

    private static List<ConnectionCountNode> FilterUnassigned(IReadOnlyList<ConnectionCountNode> nodes)
    {
        var result = new List<ConnectionCountNode>();
        foreach (var n in nodes)
        {
            if (n.Note?.Assignees.Any() == true) continue; // この行の下はすべて担当者が決まっている
            if (n.Children.Count == 0) result.Add(n);
            else if (FilterUnassigned(n.Children) is { Count: > 0 } children) result.Add(n with { Children = children });
        }
        return result;
    }
}

/// <summary>接続アプリ表の行の情報（日本語名・メモ・対応担当者）の API</summary>
public static class NodeNoteEndpoints
{
    public static void MapNodeNoteEndpoints(this IEndpointRouteBuilder app)
    {
        // 絞り込み・入力の候補：設定済みの担当者（メイン・サブ。重複なし・名前順）
        app.MapGet("/api/assignees", async (AppDbContext db, CancellationToken ct) =>
            (await db.AppNodeNotes.AsNoTracking().ToListAsync(ct))
                .SelectMany(n => NodeNoteDto.From(n).Assignees)
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCulture).ToList());

        app.MapPut("/api/node-notes", async (NodeNoteInput input, AppDbContext db, CancellationToken ct) =>
        {
            if (Validate(input) is { } err) return Results.ValidationProblem(err);
            var saved = await SaveAsync(db, input, DateTime.Now, ct);
            return Results.Ok(new { note = saved });
        });
    }

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    /// <summary>サブの担当者を整える（空・重複・メインと同じ名前を除く。改行は名前に含めない）</summary>
    internal static List<string> CleanSubs(IReadOnlyList<string>? subs, string? main) =>
        (subs ?? []).Select(s => Clean(s?.Replace("\r", " ").Replace("\n", " ")))
            .OfType<string>()
            .Where(s => !string.Equals(s, main, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    internal static Dictionary<string, string[]>? Validate(NodeNoteInput input)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(input.NodeKey)) errors["nodeKey"] = ["行のキーがありません"];
        else if (input.NodeKey.Length > AppNodeNoteLimits.NodeKeyLength) errors["nodeKey"] = ["この行は名前が長すぎるため設定できません"];
        if (Clean(input.DisplayName)?.Length > AppNodeNoteLimits.DisplayNameLength)
            errors["displayName"] = [$"日本語名は {AppNodeNoteLimits.DisplayNameLength} 文字以内で入力してください"];
        if (Clean(input.Memo)?.Length > AppNodeNoteLimits.MemoLength)
            errors["memo"] = [$"メモは {AppNodeNoteLimits.MemoLength} 文字以内で入力してください"];
        var main = Clean(input.MainAssignee);
        var subs = CleanSubs(input.SubAssignees, main);
        if (main?.Length > AppNodeNoteLimits.AssigneeLength || subs.Any(s => s.Length > AppNodeNoteLimits.AssigneeLength))
            errors["assignee"] = [$"担当者は 1 人 {AppNodeNoteLimits.AssigneeLength} 文字以内で入力してください"];
        if (subs.Count > AppNodeNoteLimits.MaxSubAssignees)
            errors["subAssignees"] = [$"サブの担当者は {AppNodeNoteLimits.MaxSubAssignees} 人までです"];
        return errors.Count > 0 ? errors : null;
    }

    /// <summary>行の情報を保存する（すべて空なら削除）。保存後の情報（削除した場合は null）を返す</summary>
    internal static async Task<NodeNoteDto?> SaveAsync(AppDbContext db, NodeNoteInput input, DateTime now, CancellationToken ct = default)
    {
        var main = Clean(input.MainAssignee);
        var subs = CleanSubs(input.SubAssignees, main);
        var displayName = Clean(input.DisplayName);
        var memo = Clean(input.Memo);
        var row = await db.AppNodeNotes.FindAsync([input.NodeKey], ct);
        if (displayName is null && memo is null && main is null && subs.Count == 0)
        {
            if (row is not null)
            {
                db.AppNodeNotes.Remove(row);
                await db.SaveChangesAsync(ct);
            }
            return null;
        }
        if (row is null)
        {
            row = new AppNodeNote { NodeKey = input.NodeKey };
            db.AppNodeNotes.Add(row);
        }
        row.DisplayName = displayName;
        row.Memo = memo;
        row.MainAssignee = main;
        row.SubAssignees = subs.Count == 0 ? null : string.Join(AppNodeNoteLimits.SubSeparator, subs);
        row.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return NodeNoteDto.From(row);
    }
}
