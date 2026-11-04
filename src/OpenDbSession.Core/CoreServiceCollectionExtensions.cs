using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenDbSession.Core.Collection;
using OpenDbSession.Core.Collection.Demo;
using OpenDbSession.Core.Collection.Oracle;
using OpenDbSession.Core.Data;
using OpenDbSession.Core.DbLinks;
using OpenDbSession.Core.Services;

namespace OpenDbSession.Core;

public static class CoreServiceCollectionExtensions
{
    /// <summary>
    /// アプリケーション DB と各データソースを登録する。
    /// 新しい種類の DB を監視対象にする場合は ISessionSource / IDbLinkInspector の実装をここに追加する。
    /// </summary>
    public static IServiceCollection AddOpenDbSessionCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddAppDatabase(configuration);

        services.AddSingleton<DemoWorld>();
        services.AddSingleton<ISessionSource, OracleSessionSource>();
        services.AddSingleton<ISessionSource, DemoSessionSource>();
        services.AddSingleton<SessionSourceFactory>();

        services.AddSingleton<IDbLinkInspector, OracleDbLinkInspector>();
        services.AddSingleton<IDbLinkInspector, DemoDbLinkInspector>();
        services.AddSingleton<DbLinkInspectorFactory>();

        services.AddSingleton<CollectionService>();
        return services;
    }
}
