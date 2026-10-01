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
}
