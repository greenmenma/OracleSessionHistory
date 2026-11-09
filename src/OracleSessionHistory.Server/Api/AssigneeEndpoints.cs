using Microsoft.EntityFrameworkCore;
using OracleSessionHistory.Core.Data;
using OracleSessionHistory.Core.Entities;

namespace OracleSessionHistory.Server.Api;

/// <param name="NodeKey">接続アプリ表の行のキー（<see cref="ConnectionCountNode.Key"/>）</param>
/// <param name="Assignee">対応担当者。空・null は設定を外す</param>
public sealed record AssigneeInput(string NodeKey, string? Assignee);

/// <summary>接続アプリ表の行のキー（対応担当者の保存に使う）</summary>
public static class ConnectionTreeKeys
{
    /// <summary>段の区切り（名前に改行は含まれないため、別の経路と同じキーにならない）</summary>
    public const char Separator = '\n';

    /// <summary>上の段のキーに、この段の「種類:名前」をつなげる</summary>
    public static string Of(string? parentKey, string kind, string label) =>
        string.IsNullOrEmpty(parentKey) ? $"{kind}:{label}" : $"{parentKey}{Separator}{kind}:{label}";

    /// <summary>木の各行にキーと対応担当者を付ける</summary>
    public static IReadOnlyList<ConnectionCountNode> Apply(
        IReadOnlyList<ConnectionCountNode> nodes, IReadOnlyDictionary<string, string> assignees, string? parentKey = null) =>
        nodes.Select(n =>
        {
            var key = Of(parentKey, n.Kind, n.Label);
            return n with
            {
                Key = key,
                Assignee = assignees.GetValueOrDefault(key),
                Children = Apply(n.Children, assignees, key),
            };
        }).ToList();
}

/// <summary>接続アプリ表の対応担当者の API</summary>
public static class AssigneeEndpoints
{
    public static void MapAssigneeEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/assignees");

        // 入力候補：設定済みの担当者（重複なし・名前順）
        g.MapGet("", async (AppDbContext db, CancellationToken ct) =>
            await db.AppAssignees.AsNoTracking().Select(a => a.Assignee).Distinct().OrderBy(a => a).ToListAsync(ct));

        g.MapPut("", async (AssigneeInput input, AppDbContext db, CancellationToken ct) =>
        {
            if (Validate(input) is { } err) return Results.ValidationProblem(err);
            return Results.Ok(new { assignee = await SaveAsync(db, input, DateTime.Now, ct) });
        });
    }

    internal static Dictionary<string, string[]>? Validate(AssigneeInput input)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(input.NodeKey)) errors["nodeKey"] = ["行のキーがありません"];
        else if (input.NodeKey.Length > AppAssigneeLimits.NodeKeyLength) errors["nodeKey"] = ["この行は名前が長すぎるため担当者を設定できません"];
        if (input.Assignee?.Trim().Length > AppAssigneeLimits.AssigneeLength)
            errors["assignee"] = [$"担当者は {AppAssigneeLimits.AssigneeLength} 文字以内で入力してください"];
        return errors.Count > 0 ? errors : null;
    }

    /// <summary>担当者を保存する（空は削除）。保存後の担当者（削除した場合は null）を返す</summary>
    internal static async Task<string?> SaveAsync(AppDbContext db, AssigneeInput input, DateTime now, CancellationToken ct = default)
    {
        var name = input.Assignee?.Trim();
        var row = await db.AppAssignees.FindAsync([input.NodeKey], ct);
        if (string.IsNullOrEmpty(name))
        {
            if (row is not null) db.AppAssignees.Remove(row);
            name = null;
        }
        else if (row is null)
        {
            db.AppAssignees.Add(new AppAssignee { NodeKey = input.NodeKey, Assignee = name, UpdatedAt = now });
        }
        else
        {
            row.Assignee = name;
            row.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
        return name;
    }
}
