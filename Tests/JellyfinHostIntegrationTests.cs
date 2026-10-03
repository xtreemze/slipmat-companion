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
            descriptor => descriptor.ServiceType == typeof(ArtworkPaletteService)
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
            services,
            descriptor => descriptor.ServiceType == typeof(ICloudProjectionConfigurationStore)
                && descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(CloudMigrationStore)
                && descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(CloudMigrationQueue)
                && descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(CloudMigrationCoordinator)
                && descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(CloudMigrationWorker)
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
        Assert.Contains("This remote root is empty", html);
        Assert.Contains("rclone lsd", html);
        Assert.Contains("new local mount folder", html);
        Assert.Contains("CloudCachePath", html);
        Assert.Contains("CloudCacheMaxSizeGiB", html);
        Assert.Contains("CloudCacheMaxAgeHours", html);
        Assert.Contains("CloudCacheMinFreeSpaceGiB", html);
        Assert.Contains("read-only rclone VFS mount", html);
        Assert.Contains("rclone", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Jottacloud", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("jotta-cli", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Plugin_ConfigurationPage_UsesJellyfinWebNativeFormComponents()
    {
        var resourceName = "Jellyfin.Plugin.AudioGateway.Configuration.configPage.html";
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var html = reader.ReadToEnd();

        Assert.Contains(
            "data-require=\"emby-input,emby-button,emby-select,emby-checkbox\"",
            html);
        Assert.Contains("class=\"selectContainer\"", html);
        Assert.Contains("class=\"selectLabel\"", html);
        Assert.Contains("class=\"emby-select-withcolor emby-select\"", html);
        Assert.Contains("class=\"checkboxContainer checkboxContainer-withDescription\"", html);
        Assert.Contains("class=\"emby-checkbox-label\"", html);
        Assert.Contains("is=\"emby-checkbox\"", html);
        Assert.Contains("class=\"raised emby-button\"", html);
        Assert.Contains("class=\"raised button-submit block emby-button\"", html);
        Assert.Contains("button.className = 'raised emby-button';", html);
        Assert.DoesNotContain("class=\"emby-button\"", html);
        Assert.DoesNotContain("--slipmat-control-surface", html);
        Assert.DoesNotContain("color-mix(in srgb, currentColor", html);
    }

    [Fact]
    public void IntegratedAnalyzer_ProbesJellyfinFfmpegDirectlyForLoudnorm()
    {
        var ffmpeg = Environment.GetEnvironmentVariable("SLIPMAT_PARITY_FFMPEG");
        Assert.False(
            string.IsNullOrWhiteSpace(ffmpeg),
            "SLIPMAT_PARITY_FFMPEG must point to the Jellyfin FFmpeg parity binary.");

        Assert.True(
            IntegratedAudioAnalyzer.ProbeLoudnormSupport(ffmpeg!),
            "The Jellyfin FFmpeg parity binary must execute the loudnorm filter used by integrated analysis.");
    }

}
