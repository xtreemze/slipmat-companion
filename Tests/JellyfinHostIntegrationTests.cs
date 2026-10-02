using Jellyfin.Plugin.AudioGateway.Services;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public class JellyfinHostIntegrationTests
{
    [Fact]
    public void ServiceRegistrator_RegistersGatewayServicesWithJellyfinDi()
    {
        var services = new ServiceCollection();

        new ServiceRegistrator().RegisterServices(services, null!);

        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(SidecarLoader)
                && descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(JellyfinAnalysisSubjectFactory)
                && descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(JellyfinMetadataAdapter)
                && descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(IntegratedAudioAnalyzer)
                && descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(IntegratedAnalysisWorker)
                && descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(IHostedService)
                && descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(TrackPlaybackEventService)
                && descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(IptvOrgCatalogService)
                && descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(ILiveTvService)
                && descriptor.ImplementationType == typeof(IptvOrgLiveTvService)
                && descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(IJottacloudCliProcessRunner)
                && descriptor.ImplementationType == typeof(JottacloudCliProcessRunner)
                && descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(IJottacloudProjectionConfigurationSource)
                && descriptor.ImplementationType == typeof(PluginJottacloudProjectionConfigurationSource)
                && descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(IJottacloudLibraryProjection)
                && descriptor.ImplementationType == typeof(JellyfinJottacloudLibraryProjection)
                && descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(JottacloudProjectionService)
                && descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(IScheduledTask)
                && descriptor.ImplementationType == typeof(JottacloudProjectionRefreshTask)
                && descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(
            typeof(ServiceRegistrator).GetInterfaces(),
            type => type == typeof(IPluginServiceRegistrator));
    }

    [Fact]
    public void BackfillTask_IsDiscoverableByJellyfin()
    {
        Assert.Contains(
            typeof(AnalysisBackfillTask).GetInterfaces(),
            type => type == typeof(MediaBrowser.Model.Tasks.IScheduledTask));
    }

    [Fact]
    public void Plugin_ExposesNativeJellyfinConfigurationPage()
    {
        Assert.Contains(
            typeof(Plugin).GetInterfaces(),
            type => type == typeof(IHasWebPages));
        Assert.Contains(
            "Jellyfin.Plugin.AudioGateway.Configuration.configPage.html",
            typeof(Plugin).Assembly.GetManifestResourceNames());
    }
}
