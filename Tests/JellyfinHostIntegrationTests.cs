using Jellyfin.Plugin.AudioGateway.Services;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.DependencyInjection;
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
            descriptor => descriptor.ServiceType == typeof(TrackPlaybackEventService)
                && descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(
            typeof(ServiceRegistrator).GetInterfaces(),
            type => type == typeof(IPluginServiceRegistrator));
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
