using System.IO;
using Jellyfin.Plugin.AudioGateway.Configuration;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public class RuntimeSettingsTests
{
    [Fact]
    public void Defaults_RequireNoStoreOverride()
    {
        var config = new PluginConfiguration();
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
    public void ResolveJottacloudProjectionPath_FreshInstall_UsesDeterministicJellyfinManagedPath()
    {
        var config = new PluginConfiguration();
        var dataPath = Path.Combine(Path.GetTempPath(), "jellyfin-data");

        var first = RuntimeSettings.ResolveJottacloudProjectionPath(
            config,
            "Archive/Music",
            dataPath);
        var second = RuntimeSettings.ResolveJottacloudProjectionPath(
            config,
            "Archive/Music",
            dataPath);
        var other = RuntimeSettings.ResolveJottacloudProjectionPath(
            config,
            "Archive/Movies",
            dataPath);

        Assert.Equal(first, second);
        Assert.StartsWith(
            Path.Combine(dataPath, "audio-gateway", "cloud", "jottacloud"),
            first);
        Assert.NotEqual(first, other);
    }

    [Fact]
    public void ResolveJottacloudProjectionPath_ConfiguredOverrideWins()
    {
        var overridePath = Path.Combine(Path.GetTempPath(), "cloud-media-cache");
        var config = new PluginConfiguration
        {
            JottacloudProjectionPath = overridePath,
        };

        var resolved = RuntimeSettings.ResolveJottacloudProjectionPath(
            config,
            "Archive/Music",
            Path.Combine(Path.GetTempPath(), "jellyfin-data"));

        Assert.Equal(Path.GetFullPath(overridePath), resolved);
    }

}
