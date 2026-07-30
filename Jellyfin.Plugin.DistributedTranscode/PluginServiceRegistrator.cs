using Jellyfin.Plugin.DistributedTranscode.Configuration;
using Jellyfin.Plugin.DistributedTranscode.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.DistributedTranscode;

public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton(_ => Plugin.Instance?.Configuration ?? new PluginConfiguration());
        serviceCollection.AddSingleton<MeshNodeService>();
        serviceCollection.AddSingleton<JobDistributor>();
        serviceCollection.AddSingleton<TranscodeJobManager>();
        serviceCollection.AddSingleton<DistributeTranscodeService>();
        serviceCollection.AddHostedService<DistributedTranscodeHostedService>();
    }
}
