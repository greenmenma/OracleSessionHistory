using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Aspire の共通設定（OpenTelemetry・ヘルスチェック）。
/// Aspire ダッシュボードでログ・トレース・メトリクスを確認できる。
/// </summary>
public static class Extensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();
        builder.AddFileLogging();
        builder.AddDefaultHealthChecks();
        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation())
            .WithTracing(tracing => tracing
                .AddSource(builder.Environment.ApplicationName)
                .AddAspNetCoreInstrumentation(o =>
                    o.Filter = ctx => !ctx.Request.Path.StartsWithSegments(HealthEndpointPath)
                                      && !ctx.Request.Path.StartsWithSegments(AlivenessEndpointPath))
                .AddHttpClientInstrumentation());

        // Aspire から起動された場合のみ OTLP エクスポーターを有効にする
        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
            builder.Services.AddOpenTelemetry().UseOtlpExporter();

        return builder;
    }

    /// <summary>
    /// エラー等のログをファイルに残す（設定 FileLog）。既定は Warning 以上を
    /// アプリのフォルダの logs/&lt;アプリ名&gt;-yyyyMMdd.log に出力し、31 日分保持する。
    /// コンソール・OpenTelemetry への出力はそのまま。
    /// </summary>
    public static TBuilder AddFileLogging<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var section = builder.Configuration.GetSection("FileLog");
        if (!section.GetValue("Enabled", true)) return builder;

        // 相対パスはアプリのフォルダ基準（IIS・Windows サービスではカレントディレクトリが異なるため）
        var dir = section["Path"];
        dir = string.IsNullOrWhiteSpace(dir)
            ? Path.Combine(AppContext.BaseDirectory, "logs")
            : Path.GetFullPath(dir, AppContext.BaseDirectory);
        var level = section.GetValue("MinimumLevel", LogLevel.Warning);
        var retainedDays = section.GetValue("RetainedDays", 31);

        var logger = new LoggerConfiguration()
            .MinimumLevel.Is(ToSerilogLevel(level))
            .WriteTo.File(
                Path.Combine(dir, builder.Environment.ApplicationName + "-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: retainedDays,
                // IIS のリサイクル時は新旧のプロセスが同時に書き込むため共有する
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}{NewLine}  {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        builder.Logging.AddSerilog(logger, dispose: true);
        return builder;
    }

    private static LogEventLevel ToSerilogLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => LogEventLevel.Verbose,
        LogLevel.Debug => LogEventLevel.Debug,
        LogLevel.Information => LogEventLevel.Information,
        LogLevel.Warning => LogEventLevel.Warning,
        LogLevel.Error => LogEventLevel.Error,
        _ => LogEventLevel.Fatal,
    };

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks().AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);
        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        if (app.Environment.IsDevelopment())
        {
            app.MapHealthChecks(HealthEndpointPath);
            app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions { Predicate = r => r.Tags.Contains("live") });
        }
        return app;
    }
}
