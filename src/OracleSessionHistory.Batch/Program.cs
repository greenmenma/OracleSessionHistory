using OracleSessionHistory.Batch;
using OracleSessionHistory.Core;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
// Windows サービスとして登録した場合にも動作するようにする
builder.Services.AddWindowsService(o => o.ServiceName = "OracleSessionHistory.Batch");

builder.Services.Configure<CollectorOptions>(builder.Configuration.GetSection("Collector"));
builder.Services.AddOracleSessionHistoryCore(builder.Configuration);
builder.Services.AddHostedService<CollectorHost>();
builder.Services.AddHostedService<ProcessInfoCollector>();
builder.Services.AddHostedService<UserNameCollector>();

var host = builder.Build();
host.Run();
