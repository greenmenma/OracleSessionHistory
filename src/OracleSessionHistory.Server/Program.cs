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

app.UseExceptionHandler();
app.MapDefaultEndpoints();

app.MapTargetEndpoints();
app.MapSessionEndpoints();
app.MapReportEndpoints();
app.MapLinkEndpoints();

// 本番配置時は Client のビルド結果（wwwroot）を配信する
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

app.Run();

/// <summary>テストから参照するための宣言</summary>
public partial class Program;
