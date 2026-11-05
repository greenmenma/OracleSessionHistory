using System.Text.Json.Serialization;
using OracleSessionHistory.Core;
using OracleSessionHistory.Core.Data;
using OracleSessionHistory.Server.Api;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();
builder.Services.AddOracleSessionHistoryCore(builder.Configuration);
builder.Services.ConfigureHttpJsonOptions(o =>
{
    // ナビゲーションプロパティの循環参照を避ける
    o.SerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
});

var app = builder.Build();

await AppDatabase.InitializeAsync(app.Services, app.Logger, seedDemo: false);

// IIS のサブフォルダ（例：Default Web Site/OracleSessionHistory）に配置した場合は、IIS が仮想アプリケーションのパスを PathBase に設定する。
// Kestrel をリバースプロキシ配下のサブフォルダで動かす場合は設定 PathBase（例：/OracleSessionHistory）を指定する
if (app.Configuration["PathBase"] is { Length: > 0 } pathBase)
    app.UsePathBase(pathBase);
// PathBase を除いたパスでルーティングする（明示しないとパイプラインの先頭で PathBase を含むパスのまま照合される）
app.UseRouting();

app.UseExceptionHandler();

// Client は assets・API を相対パスで参照するため、/OracleSessionHistory を /OracleSessionHistory/ にそろえる
app.Use(async (ctx, next) =>
{
    if (ctx.Request.PathBase.HasValue && !ctx.Request.Path.HasValue
        && (HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method)))
    {
        ctx.Response.Redirect($"{ctx.Request.PathBase}/{ctx.Request.QueryString}");
        return;
    }
    await next();
});

// 本番配置時は Client のビルド結果（wwwroot）の assets を配信する
app.UseStaticFiles();

app.MapDefaultEndpoints();
app.MapTargetEndpoints();
app.MapSessionEndpoints();
app.MapReportEndpoints();
app.MapLinkEndpoints();

// 画面はハッシュルーターのため、index.html はアプリのルートだけで返す。
// ルート以外の不明なパスで返すと相対パスの assets を解決できないため、ルートへリダイレクトする。
// （ルーティングが静的ファイルより先に入るため、UseDefaultFiles ではなくここで返す）
app.MapFallback(async ctx =>
{
    var path = ctx.Request.Path;
    var index = app.Environment.WebRootFileProvider.GetFileInfo("index.html");
    if (path == "/" && HttpMethods.IsGet(ctx.Request.Method) && index.Exists)
    {
        ctx.Response.ContentType = "text/html; charset=utf-8";
        // 更新後に古い assets を参照し続けないよう、index.html はキャッシュさせない
        ctx.Response.Headers.CacheControl = "no-cache";
        await ctx.Response.SendFileAsync(index);
    }
    else if (path == "/" || path.StartsWithSegments("/api") || !HttpMethods.IsGet(ctx.Request.Method))
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
    else
        ctx.Response.Redirect($"{ctx.Request.PathBase}/");
});

app.Run();

/// <summary>テストから参照するための宣言</summary>
public partial class Program;
