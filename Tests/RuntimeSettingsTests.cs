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
    public void ResolveCloudProjectionPath_FreshInstall_UsesDeterministicJellyfinManagedPath()
    {
        var config = new PluginConfiguration();
        var dataPath = Path.Combine(Path.GetTempPath(), "jellyfin-data");

        var first = RuntimeSettings.ResolveCloudProjectionPath(
            config,
            "tele2",
            "Archive/Music",
            dataPath);
        var second = RuntimeSettings.ResolveCloudProjectionPath(
            config,
            "tele2",
            "Archive/Music",
            dataPath);
        var other = RuntimeSettings.ResolveCloudProjectionPath(
            config,
            "drive",
            "Archive/Music",
            dataPath);

        Assert.Equal(first, second);
        Assert.StartsWith(
            Path.Combine(dataPath, "audio-gateway", "cloud", "rclone"),
            first);
        Assert.NotEqual(first, other);
    }

    [Fact]
    public void ResolveCloudProjectionPath_ConfiguredOverrideWins()
    {
        var overridePath = Path.Combine(Path.GetTempPath(), "cloud-media-cache");
        var config = new PluginConfiguration
        {
            CloudProjectionPath = overridePath,
        };

        var resolved = RuntimeSettings.ResolveCloudProjectionPath(
            config,
            "tele2",
            "Archive/Music",
            Path.Combine(Path.GetTempPath(), "jellyfin-data"));

        Assert.Equal(Path.GetFullPath(overridePath), resolved);
    }

    [Fact]
    public void ResolveCloudCachePath_FreshInstall_UsesSeparateDeterministicManagedPath()
    {
        var config = new PluginConfiguration();
        var dataPath = Path.Combine(Path.GetTempPath(), "jellyfin-data");

        var mount = RuntimeSettings.ResolveCloudProjectionPath(
            config,
            "tele2",
            "Archive/Music",
            dataPath);
        var cache = RuntimeSettings.ResolveCloudCachePath(
            config,
            "tele2",
            "Archive/Music",
            dataPath);

        Assert.StartsWith(
            Path.Combine(dataPath, "audio-gateway", "cloud", "rclone", "cache"),
            cache);
        Assert.NotEqual(mount, cache);
    }

    [Fact]
    public void IsCloudProjectionPath_MatchesOnlyFilesWithinConfiguredMount()
    {
        using var temp = new TemporaryDirectory();
        var mount = Path.Combine(temp.Path, "mount");
        var config = new PluginConfiguration
        {
            CloudRemoteName = "tele2",
            CloudRemotePath = "Archive/Music",
            CloudProjectionPath = mount,
        };

        Assert.True(RuntimeSettings.IsCloudProjectionPath(
            config,
            Path.Combine(mount, "Artist", "Album", "track.flac")));
        Assert.False(RuntimeSettings.IsCloudProjectionPath(
            config,
            Path.Combine(temp.Path, "local", "track.flac")));
    }


    private sealed class TemporaryDirectory : System.IDisposable
    {
        public TemporaryDirectory()
        {
            Path = Directory.CreateTempSubdirectory("audio-gateway-runtime-settings").FullName;
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

}
