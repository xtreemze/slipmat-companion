using Jellyfin.Plugin.AudioGateway.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.AudioGateway;

/// <summary>
/// Registers Audio Gateway host services in Jellyfin's dependency-injection container.
/// </summary>
public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(
        IServiceCollection serviceCollection,
        IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<SidecarLoader>();
        serviceCollection.AddSingleton(_ => new JellyfinAnalysisSubjectFactory(applicationHost));
        serviceCollection.AddSingleton<JellyfinMetadataAdapter>();
        serviceCollection.AddSingleton<IntegratedAudioAnalyzer>();
        serviceCollection.AddSingleton<IntegratedAnalysisWorker>();
        serviceCollection.AddSingleton<IHostedService>(
            services => services.GetRequiredService<IntegratedAnalysisWorker>());
        serviceCollection.AddSingleton<TrackPlaybackEventService>();
    }
}
