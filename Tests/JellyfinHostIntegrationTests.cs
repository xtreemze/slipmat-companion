using System.IO;
using Jellyfin.Plugin.AudioGateway.Services;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Tasks;
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
            descriptor => descriptor.ServiceType == typeof(IRcloneProcessRunner)
                && descriptor.ImplementationType == typeof(RcloneProcessRunner)
                && descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(ICloudProjectionConfigurationSource)
                && descriptor.ImplementationType == typeof(PluginCloudProjectionConfigurationSource)
                && descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(ICloudLibraryProjection)
                && descriptor.ImplementationType == typeof(JellyfinCloudLibraryProjection)
                && descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(CloudProjectionService)
                && descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(IScheduledTask)
                && descriptor.ImplementationType == typeof(CloudProjectionRefreshTask)
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

    [Fact]
    public void Plugin_CloudConfigurationPage_ContainsRcloneFolderManager()
    {
        var resourceName = "Jellyfin.Plugin.AudioGateway.Configuration.configPage.html";
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var html = reader.ReadToEnd();

        Assert.Contains("CloudBrowserPath", html);
        Assert.Contains("CloudBrowserEntries", html);
        Assert.Contains("CloudUseFolderButton", html);
        Assert.Contains("Use this folder", html);
        Assert.Contains("CloudRemoteName", html);
        Assert.Contains("CloudCreateFolderButton", html);
        Assert.Contains("rclone", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Jottacloud", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("jotta-cli", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Plugin_ConfigurationPage_UsesThemeAdaptiveAccessibleFormControls()
    {
        var resourceName = "Jellyfin.Plugin.AudioGateway.Configuration.configPage.html";
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var html = reader.ReadToEnd();

        Assert.Contains("--slipmat-control-surface", html);
        Assert.Contains("color-mix(in srgb, currentColor", html);
        Assert.Contains(":focus-visible", html);
        Assert.Contains("::placeholder", html);
        Assert.Contains("@media (forced-colors: active)", html);
        Assert.Contains("background: Canvas !important", html);
        Assert.Contains("color: CanvasText !important", html);
        Assert.DoesNotContain("background: white", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("background-color: white", html, StringComparison.OrdinalIgnoreCase);
    }

}
