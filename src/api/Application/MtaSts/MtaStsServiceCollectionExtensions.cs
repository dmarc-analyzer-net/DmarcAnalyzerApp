using DmarcAnalyzer.Api.Application.Analytics;
using Microsoft.Extensions.Options;

namespace DmarcAnalyzer.Api.Application.MtaSts;

public static class MtaStsServiceCollectionExtensions
{
    /// <summary>
    /// Everything MTA-STS monitoring needs, registered in one place so the worker
    /// host and the API host cannot drift apart — the same bug class the backup
    /// chain hit when it was registered on only one of them.
    /// Requires AddMemoryCache and IDnsTxtResolver, which both hosts provide.
    /// </summary>
    public static IServiceCollection AddMtaStsMonitoring(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<MtaStsOptions>(configuration.GetSection("MtaSts"));
        services.AddSingleton<IDnsMxResolver, DnsMxResolver>();

        services.AddHttpClient(MtaStsPolicyFetcher.ClientName, (sp, client) =>
            {
                var options = sp.GetRequiredService<IOptions<MtaStsOptions>>().Value;
                client.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.FetchTimeoutSeconds, 1, 120));
                client.DefaultRequestHeaders.UserAgent.ParseAdd("DmarcAnalyzer/1.0 (MTA-STS monitor)");
            })
            .ConfigurePrimaryHttpMessageHandler(sp =>
                MtaStsPolicyFetcher.CreateHandler(sp.GetRequiredService<IOptions<MtaStsOptions>>().Value))
            // Four info lines per fetch, per domain, per pass is log spam at any
            // fleet size; failures are recorded on mta_sts_state and OTel still
            // traces the requests. (This does not affect either of those.)
            .RemoveAllLoggers();

        services.AddSingleton<IMtaStsPolicyFetcher, MtaStsPolicyFetcher>();
        services.AddSingleton<IMtaStsCheckService, MtaStsCheckService>();
        services.AddScoped<IMtaStsStateCache, MtaStsStateCache>();
        services.AddScoped<IMtaStsInspectionService, MtaStsInspectionService>();

        return services;
    }
}
