// デバッグ用の Aspire AppHost。Batch・Server・Client をまとめて起動する。
var builder = DistributedApplication.CreateBuilder(args);

// デバッグ時は SQLite を使う（appsettings.json はリリース用の SQL Server 設定）。
// Batch と Server が同じ SQLite ファイルを参照するよう、リポジトリ直下の .data に固定する
var dataDir = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", ".data"));
Directory.CreateDirectory(dataDir);
var appDb = $"Data Source={Path.Combine(dataDir, "oraclesessionhistory.db")}";

var server = builder.AddProject<Projects.OracleSessionHistory_Server>("server")
    .WithEnvironment("AppDb__Provider", "Sqlite")
    .WithEnvironment("ConnectionStrings__AppDb", appDb)
    .WithHttpHealthCheck("/health");

builder.AddProject<Projects.OracleSessionHistory_Batch>("batch")
    .WithEnvironment("AppDb__Provider", "Sqlite")
    .WithEnvironment("ConnectionStrings__AppDb", appDb)
    .WithEnvironment("Collector__SeedDemoTarget", "true") // Oracle 無しで確認できるようデモ用の監視対象を登録
    .WaitFor(server); // Server 側で DB を作成してから収集を開始する

builder.AddViteApp("client", "../OracleSessionHistory.Client")
    .WithReference(server)
    .WaitFor(server);

builder.Build().Run();
