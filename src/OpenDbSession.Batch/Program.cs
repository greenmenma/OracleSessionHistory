using OpenDbSession.Batch;
using OpenDbSession.Core;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
// Windows サービスとして登録した場合にも動作するようにする
builder.Services.AddWindowsService(o => o.ServiceName = "OpenDbSession.Batch");

builder.Services.Configure<CollectorOptions>(builder.Configuration.GetSection("Collector"));
builder.Services.AddOpenDbSessionCore(builder.Configuration);
builder.Services.AddHostedService<CollectorHost>();

var host = builder.Build();
host.Run();
