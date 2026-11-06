using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OracleSessionHistory.Core.Collection;
using OracleSessionHistory.Core.Collection.Demo;
using OracleSessionHistory.Core.Collection.Oracle;
using OracleSessionHistory.Core.Data;
using OracleSessionHistory.Core.DbLinks;
using OracleSessionHistory.Core.Processes;
using OracleSessionHistory.Core.Services;
using OracleSessionHistory.Core.Users;

namespace OracleSessionHistory.Core;

public static class CoreServiceCollectionExtensions
{
    /// <summary>
    /// アプリケーション DB と各データソースを登録する。
    /// 新しい種類の DB を監視対象にする場合は ISessionSource / IDbLinkInspector の実装をここに追加する。
    /// </summary>
    public static IServiceCollection AddOracleSessionHistoryCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddAppDatabase(configuration);

        services.AddSingleton<DemoWorld>();
        services.AddSingleton<ISessionSource, OracleSessionSource>();
        services.AddSingleton<ISessionSource, DemoSessionSource>();
        services.AddSingleton<SessionSourceFactory>();

        services.AddSingleton<IDbLinkInspector, OracleDbLinkInspector>();
        services.AddSingleton<IDbLinkInspector, DemoDbLinkInspector>();
        services.AddSingleton<DbLinkInspectorFactory>();

        // 接続元サーバーのプロセス情報（WMI/CIM）
        services.AddSingleton<CimProcessInspector>();
        services.AddSingleton<DemoProcessInspector>();
        services.AddSingleton(sp => new ProcessInspectorResolver(
            sp.GetRequiredService<CimProcessInspector>(), sp.GetRequiredService<DemoProcessInspector>()));
        services.AddSingleton<HostBackoff>();
        services.AddSingleton<ProcessInfoService>();

        // 実行ユーザの表示名（AD）。Windows 以外（デバッグ）はデモの疑似ディレクトリ
        services.AddSingleton<AdUserDirectory>();
        services.AddSingleton<IUserDirectory>(sp => OperatingSystem.IsWindows()
            ? sp.GetRequiredService<AdUserDirectory>()
            : new DemoUserDirectory());
        services.AddSingleton<UserNameService>();

        services.AddSingleton<CollectionService>();
        return services;
    }
}
