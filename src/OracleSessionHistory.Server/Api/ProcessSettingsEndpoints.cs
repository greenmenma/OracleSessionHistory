using Microsoft.EntityFrameworkCore;
using OracleSessionHistory.Core.Data;
using OracleSessionHistory.Core.Entities;
using OracleSessionHistory.Core.Services;
using OracleSessionHistory.Server.Support;

namespace OracleSessionHistory.Server.Api;

/// <summary>プロセス情報取得の設定（パスワードは返さず、設定済みかどうかだけ返す）</summary>
public sealed record ProcessSettingsDto(
    bool Enabled, string? UserName, bool HasPassword, string? DnsSuffix, int TimeoutSeconds, string? ExcludeMachines, DateTime? UpdatedAt,
    string? TargetPrograms)
{
    public static ProcessSettingsDto From(ProcessInspectionSettings? s) => s is null
        ? new(false, null, false, null, 10, null, null, null)
        : new(s.Enabled, s.UserName, !string.IsNullOrEmpty(s.Password), s.DnsSuffix, s.TimeoutSeconds, s.ExcludeMachines, s.UpdatedAt,
            s.TargetPrograms);
}

/// <param name="Password">null または "********" は変更なし、空文字はクリア</param>
public sealed record ProcessSettingsInput(
    bool Enabled, string? UserName, string? Password, string? DnsSuffix, int TimeoutSeconds, string? ExcludeMachines,
    string? TargetPrograms = null);

/// <param name="Host">接続テストする端末名</param>
public sealed record ProcessSettingsTestInput(string Host, ProcessSettingsInput Settings);

/// <summary>接続元サーバーのプロセス情報取得の設定 API</summary>
public static class ProcessSettingsEndpoints
{
    public static void MapProcessSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/process-settings");

        g.MapGet("", async (AppDbContext db, CancellationToken ct) =>
            ProcessSettingsDto.From(await FindAsync(db, ct)));

        g.MapPut("", async (ProcessSettingsInput input, AppDbContext db, CancellationToken ct) =>
        {
            if (Validate(input) is { } err) return Results.ValidationProblem(err);
            var s = await FindAsync(db, ct);
            if (s is null)
            {
                s = new ProcessInspectionSettings { Id = ProcessInspectionSettings.SingletonId };
                db.ProcessInspectionSettings.Add(s);
            }
            Apply(input, s);
            // Batch は毎回この設定を読み直す（UpdatedAt は変更の記録）
            s.UpdatedAt = DateTime.Now;
            await db.SaveChangesAsync(ct);
            // 登録した実行ファイル・対象外端末が変わった場合に備え、対象外にしていた接続中のセッションを調べ直す
            await ProcessInfoService.RequeueSkippedAsync(db, ct);
            return Results.Ok(ProcessSettingsDto.From(s));
        });

        // 入力中の設定（未保存）で接続テストする。Server（IIS）の実行環境から接続するため、Batch とは経路・アカウントが異なり得る
        g.MapPost("/test", async (ProcessSettingsTestInput input, AppDbContext db, ProcessInfoService service, ILogger<ProcessInfoService> logger, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(input.Host))
                return Results.Ok(new { success = false, message = "端末名を入力してください" });
            if (Validate(input.Settings) is { } err) return Results.ValidationProblem(err);
            var stored = await FindAsync(db, ct);
            var s = new ProcessInspectionSettings();
            Apply(input.Settings, s);
            s.Password = TestPassword(input.Settings.UserName, input.Settings.Password, stored?.UserName, stored?.Password);
            try
            {
                return Results.Ok(new { success = true, message = await service.TestAsync(input.Host.Trim(), s, ct) });
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "{Host} へのプロセス情報取得の接続テストに失敗しました", input.Host);
                return Results.Ok(new { success = false, message = ex.Message });
            }
        });
    }

    /// <summary>画面からのパスワード：null・マスクは保存済みの値、空文字はクリア、それ以外は新しい値</summary>
    internal static string? MergePassword(string? incoming, string? stored) => incoming switch
    {
        null or ConnectionStringMasker.Mask => stored,
        "" => null,
        _ => incoming,
    };

    /// <summary>
    /// 接続テストに使うパスワード。保存済みのパスワード（入力がマスクまたは未指定）は、保存済みと同じアカウントの場合だけ使う
    /// （アカウントを書き換えて、保存済みパスワードを別アカウント・任意の端末で試せないようにする）。
    /// </summary>
    internal static string? TestPassword(string? inputUser, string? inputPassword, string? storedUser, string? storedPassword)
    {
        if (inputPassword is not (null or ConnectionStringMasker.Mask)) return inputPassword.Length > 0 ? inputPassword : null;
        return string.Equals(inputUser?.Trim(), storedUser?.Trim(), StringComparison.OrdinalIgnoreCase) ? storedPassword : null;
    }

    private static Task<ProcessInspectionSettings?> FindAsync(AppDbContext db, CancellationToken ct) =>
        db.ProcessInspectionSettings.SingleOrDefaultAsync(x => x.Id == ProcessInspectionSettings.SingletonId, ct);

    private static void Apply(ProcessSettingsInput input, ProcessInspectionSettings s)
    {
        s.Enabled = input.Enabled;
        s.UserName = Blank(input.UserName);
        s.Password = MergePassword(input.Password, s.Password);
        s.DnsSuffix = Blank(input.DnsSuffix);
        s.TimeoutSeconds = input.TimeoutSeconds;
        s.ExcludeMachines = Blank(input.ExcludeMachines);
        s.TargetPrograms = Blank(input.TargetPrograms);
    }

    private static string? Blank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    private static Dictionary<string, string[]>? Validate(ProcessSettingsInput i)
    {
        var e = new Dictionary<string, string[]>();
        if (i.TimeoutSeconds is < 1 or > 120) e["timeoutSeconds"] = ["タイムアウトは 1〜120 秒で指定してください"];
        if (i.UserName is { Length: > 256 }) e["userName"] = ["アカウントが長すぎます"];
        if (i.TargetPrograms is { Length: > 2000 }) e["targetPrograms"] = ["実行ファイルの登録が長すぎます"];
        if (i.ExcludeMachines is { Length: > 2000 }) e["excludeMachines"] = ["対象外端末が長すぎます"];
        if (i.DnsSuffix is { Length: > 256 }) e["dnsSuffix"] = ["DNS サフィックスが長すぎます"];
        return e.Count > 0 ? e : null;
    }
}
