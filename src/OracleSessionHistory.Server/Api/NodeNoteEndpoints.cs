using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using OracleSessionHistory.Core.Data;
using OracleSessionHistory.Core.Entities;
using OracleSessionHistory.Core.Users;

namespace OracleSessionHistory.Server.Api;

/// <summary>対応担当者（AD から選んだ人）</summary>
/// <param name="Account">AD のアカウント（userPrincipalName、無ければ sAMAccountName）</param>
/// <param name="DisplayName">選んだ時点の AD の表示名</param>
public sealed record AssigneeDto(string Account, string DisplayName);

/// <summary>接続アプリ表の行に設定した情報（画面に返す形）</summary>
public sealed record NodeNoteDto(string? DisplayName, string? Memo, AssigneeDto? MainAssignee, IReadOnlyList<AssigneeDto> SubAssignees)
{
    public static NodeNoteDto From(AppNodeNote n)
    {
        var main = n.Assignees.FirstOrDefault(a => a.IsMain);
        return new(n.DisplayName, n.Memo, main is null ? null : new(main.Account, main.DisplayName),
            n.Assignees.Where(a => !a.IsMain).OrderBy(a => a.SortOrder).Select(a => new AssigneeDto(a.Account, a.DisplayName)).ToList());
    }

    /// <summary>メイン・サブの担当者すべて（画面には返さない）</summary>
    [JsonIgnore]
    public IEnumerable<AssigneeDto> Assignees => MainAssignee is null ? SubAssignees : [MainAssignee, .. SubAssignees];
}

/// <param name="NodeKey">接続アプリ表の行のキー（<see cref="ConnectionCountNode.Key"/>）</param>
/// <param name="MainAssignee">メインの担当者（AD から選んだ人。null は未設定）</param>
/// <param name="SubAssignees">サブの担当者（重複・メインと同じ人は除く）</param>
public sealed record NodeNoteInput(string NodeKey, string? DisplayName, string? Memo, AssigneeDto? MainAssignee, IReadOnlyList<AssigneeDto>? SubAssignees);

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
    /// 担当者（AD のアカウント）で絞り込む。担当者の行はその下の段をすべて残し、上の段は経路として残す。
    /// <see cref="Unassigned"/> は、自分にも上の段にも担当者がいない最下段の行（とその経路）を残す
    /// </summary>
    public static IReadOnlyList<ConnectionCountNode> FilterByAssignee(IReadOnlyList<ConnectionCountNode> nodes, string? assignee)
    {
        if (string.IsNullOrWhiteSpace(assignee)) return nodes;
        var account = assignee.Trim();
        return account == Unassigned ? FilterUnassigned(nodes) : FilterAccount(nodes, account);
    }

    private static List<ConnectionCountNode> FilterAccount(IReadOnlyList<ConnectionCountNode> nodes, string account)
    {
        var result = new List<ConnectionCountNode>();
        foreach (var n in nodes)
        {
            if (n.Note?.Assignees.Any(a => string.Equals(a.Account, account, StringComparison.OrdinalIgnoreCase)) == true) result.Add(n);
            else if (FilterAccount(n.Children, account) is { Count: > 0 } children) result.Add(n with { Children = children });
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
    /// <summary>AD の検索で返す最大件数</summary>
    private const int MaxSearchResults = 20;

    public static void MapNodeNoteEndpoints(this IEndpointRouteBuilder app)
    {
        // 担当者を選ぶための AD の検索（名前・アカウント・メールアドレス）
        app.MapGet("/api/directory/people", async (string? q, IUserDirectory directory, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q)) return Results.Ok(Array.Empty<DirectoryPerson>());
            try
            {
                return Results.Ok(await directory.SearchAsync(q.Trim(), MaxSearchResults, ct));
            }
            catch (UserDirectoryException ex)
            {
                // AdUserDirectory が警告ログを出している
                return Results.Problem(title: ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        // 絞り込みの候補：設定済みの担当者（メイン・サブ。アカウントで重複を除き表示名順）
        app.MapGet("/api/assignees", async (AppDbContext db, CancellationToken ct) =>
            (await db.AppNodeAssignees.AsNoTracking().OrderByDescending(a => a.Id).ToListAsync(ct))
                .DistinctBy(a => a.Account, StringComparer.OrdinalIgnoreCase)
                .Select(a => new AssigneeDto(a.Account, a.DisplayName))
                .OrderBy(a => a.DisplayName, StringComparer.CurrentCulture).ToList());

        app.MapPut("/api/node-notes", async (NodeNoteInput input, AppDbContext db, CancellationToken ct) =>
        {
            if (Validate(input) is { } err) return Results.ValidationProblem(err);
            var saved = await SaveAsync(db, input, DateTime.Now, ct);
            return Results.Ok(new { note = saved });
        });
    }

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    private static AssigneeDto? Clean(AssigneeDto? a) =>
        Clean(a?.Account) is { } account ? new AssigneeDto(account, Clean(a!.DisplayName) ?? account) : null;

    /// <summary>サブの担当者を整える（アカウントの無いもの・重複・メインと同じ人を除く）</summary>
    internal static List<AssigneeDto> CleanSubs(IReadOnlyList<AssigneeDto>? subs, AssigneeDto? main) =>
        (subs ?? []).Select(Clean).OfType<AssigneeDto>()
            .Where(s => !string.Equals(s.Account, main?.Account, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(s => s.Account, StringComparer.OrdinalIgnoreCase).ToList();

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
        if ((main is null ? subs : [main, .. subs]).Any(a =>
                a.Account.Length > AppNodeNoteLimits.AccountLength || a.DisplayName.Length > AppNodeNoteLimits.AccountLength))
            errors["assignee"] = ["担当者のアカウント・表示名が長すぎます"];
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
        var row = await db.AppNodeNotes.Include(n => n.Assignees).FirstOrDefaultAsync(n => n.NodeKey == input.NodeKey, ct);
        if (displayName is null && memo is null && main is null && subs.Count == 0)
        {
            if (row is not null)
            {
                db.AppNodeNotes.Remove(row); // 担当者も削除される（カスケード）
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
        row.UpdatedAt = now;
        // 担当者は置き換える（一意インデックスに当たらないよう、先に削除を保存する）
        if (row.Assignees.Count > 0)
        {
            db.AppNodeAssignees.RemoveRange(row.Assignees);
            row.Assignees.Clear();
            await db.SaveChangesAsync(ct);
        }
        if (main is not null)
            row.Assignees.Add(new AppNodeAssignee { NodeKey = row.NodeKey, IsMain = true, Account = main.Account, DisplayName = main.DisplayName });
        row.Assignees.AddRange(subs.Select((a, i) => new AppNodeAssignee
        {
            NodeKey = row.NodeKey, IsMain = false, SortOrder = i, Account = a.Account, DisplayName = a.DisplayName,
        }));
        await db.SaveChangesAsync(ct);
        return NodeNoteDto.From(row);
    }
}
