using Jellyfin.Plugin.DistributedTranscode.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.DistributedTranscode;

public static class ServiceRegistration
{
    public static IServiceCollection AddDistributedTranscodePlugin(this IServiceCollection services)
    {
        services.AddSingleton(provider => Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration());
        services.AddSingleton<MeshNodeService>();
        services.AddSingleton<JobDistributor>();
        services.AddSingleton<TranscodeJobManager>();
        services.AddSingleton<DistributeTranscodeService>();
        services.AddHostedService<DistributedTranscodeHostedService>();
        return services;
    }
}
