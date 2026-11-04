// デバッグ用の Aspire AppHost。Batch・Server・Client をまとめて起動する。
var builder = DistributedApplication.CreateBuilder(args);

// Batch と Server が同じ SQLite ファイルを参照するよう、リポジトリ直下の .data に固定する
var dataDir = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", ".data"));
Directory.CreateDirectory(dataDir);
var appDb = $"Data Source={Path.Combine(dataDir, "opendbsession.db")}";

var server = builder.AddProject<Projects.OpenDbSession_Server>("server")
    .WithEnvironment("ConnectionStrings__AppDb", appDb)
    .WithHttpHealthCheck("/health");

builder.AddProject<Projects.OpenDbSession_Batch>("batch")
    .WithEnvironment("ConnectionStrings__AppDb", appDb)
    .WaitFor(server); // Server 側で DB を作成してから収集を開始する

builder.AddViteApp("client", "../OpenDbSession.Client")
    .WithReference(server)
    .WaitFor(server);

builder.Build().Run();
