using Jellyfin.Plugin.AudioGateway.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
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
        serviceCollection.AddSingleton<ArtworkPaletteService>();
        serviceCollection.AddSingleton<IntegratedAudioAnalyzer>();
        serviceCollection.AddSingleton<IntegratedAnalysisWorker>();
        serviceCollection.AddSingleton<IHostedService>(
            services => services.GetRequiredService<IntegratedAnalysisWorker>());
        serviceCollection.AddSingleton<TrackPlaybackEventService>();
        serviceCollection.AddSingleton<IptvOrgCatalogService>();
        serviceCollection.AddSingleton<ILiveTvService, IptvOrgLiveTvService>();

        serviceCollection.AddSingleton<IRcloneProcessRunner, RcloneProcessRunner>();
        serviceCollection.AddSingleton<PluginCloudProjectionConfigurationSource>();
        serviceCollection.AddSingleton<ICloudProjectionConfigurationSource>(
            services => services.GetRequiredService<PluginCloudProjectionConfigurationSource>());
        serviceCollection.AddSingleton<ICloudProjectionConfigurationStore>(
            services => services.GetRequiredService<PluginCloudProjectionConfigurationSource>());
        serviceCollection.AddSingleton<ICloudLibraryProjection, JellyfinCloudLibraryProjection>();
        serviceCollection.AddSingleton<CloudProjectionService>();
        serviceCollection.AddSingleton<IScheduledTask, CloudProjectionRefreshTask>();

        serviceCollection.AddSingleton<CloudMigrationStore>();
        serviceCollection.AddSingleton<CloudMigrationQueue>();
        serviceCollection.AddSingleton<CloudMigrationCoordinator>();
        serviceCollection.AddSingleton<CloudMigrationWorker>();
        serviceCollection.AddSingleton<IHostedService>(
            services => services.GetRequiredService<CloudMigrationWorker>());
    }
}
