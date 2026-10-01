using System.IO;
using Jellyfin.Plugin.AudioGateway.Configuration;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public class RuntimeSettingsTests
{
    [Fact]
    public void Defaults_RequireNoExternalAnalyzerOrStoreOverride()
    {
        var config = new PluginConfiguration();

        Assert.False(config.AnalyzerEnabled);
        Assert.Equal(string.Empty, config.AnalyzerBaseUrl);
        Assert.Equal(string.Empty, config.StoreRoot);
    }

    [Fact]
    public void ResolveStoreRoot_FreshInstall_UsesJellyfinManagedDataPath()
    {
        var config = new PluginConfiguration();
        var dataPath = Path.Combine(Path.GetTempPath(), "jellyfin-data");

        var resolved = RuntimeSettings.ResolveStoreRoot(
            config,
            dataPath,
            environmentRoot: null,
            legacyStoreAvailable: false);

        Assert.Equal(Path.Combine(dataPath, "audio-gateway"), resolved);
    }

    [Fact]
    public void ResolveStoreRoot_LegacyDefaultWithoutMount_FallsBackToJellyfinDataPath()
    {
        var config = new PluginConfiguration { StoreRoot = RuntimeSettings.LegacyStoreRoot };

        var resolved = RuntimeSettings.ResolveStoreRoot(
            config,
            jellyfinDataPath: "/jellyfin/data",
            environmentRoot: null,
            legacyStoreAvailable: false);

        Assert.Equal(Path.Combine("/jellyfin/data", "audio-gateway"), resolved);
    }

    [Fact]
    public void ResolveStoreRoot_PreservesMountedLegacyStore()
    {
        var config = new PluginConfiguration { StoreRoot = RuntimeSettings.LegacyStoreRoot };

        var resolved = RuntimeSettings.ResolveStoreRoot(
            config,
            jellyfinDataPath: "/jellyfin/data",
            environmentRoot: null,
            legacyStoreAvailable: true);

        Assert.Equal(RuntimeSettings.LegacyStoreRoot, resolved);
    }

    [Fact]
    public void ResolveStoreRoot_EnvironmentOverrideWins()
    {
        var config = new PluginConfiguration { StoreRoot = "/configured" };
        var environmentRoot = Path.Combine(Path.GetTempPath(), "audio-gateway-env");

        var resolved = RuntimeSettings.ResolveStoreRoot(
            config,
            jellyfinDataPath: "/jellyfin/data",
            environmentRoot,
            legacyStoreAvailable: true);

        Assert.Equal(Path.GetFullPath(environmentRoot), resolved);
    }

    [Fact]
    public void ResolveAnalyzerEndpoint_Default_IsNotConfigured()
    {
        var endpoint = RuntimeSettings.ResolveAnalyzerEndpoint(
            new PluginConfiguration(),
            environmentUrl: null);

        Assert.False(endpoint.ExplicitlyConfigured);
        Assert.Null(endpoint.BaseUrl);
    }

    [Fact]
    public void ResolveAnalyzerEndpoint_ConfigRequiresExplicitEnablement()
    {
        var disabled = RuntimeSettings.ResolveAnalyzerEndpoint(
            new PluginConfiguration { AnalyzerBaseUrl = "http://localhost:8765" },
            environmentUrl: null);
        var enabled = RuntimeSettings.ResolveAnalyzerEndpoint(
            new PluginConfiguration
            {
                AnalyzerEnabled = true,
                AnalyzerBaseUrl = "http://localhost:8765",
            },
            environmentUrl: null);

        Assert.False(disabled.ExplicitlyConfigured);
        Assert.True(enabled.ExplicitlyConfigured);
        Assert.Equal("http://localhost:8765", enabled.BaseUrl);
    }

    [Fact]
    public void ResolveAnalyzerEndpoint_EnvironmentOverrideEnablesAnalyzer()
    {
        var endpoint = RuntimeSettings.ResolveAnalyzerEndpoint(
            new PluginConfiguration(),
            "http://audio-analyzer:8765");

        Assert.True(endpoint.ExplicitlyConfigured);
        Assert.Equal("http://audio-analyzer:8765", endpoint.BaseUrl);
    }
}
