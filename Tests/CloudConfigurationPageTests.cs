using System.IO;
using Jellyfin.Plugin.AudioGateway;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public class CloudConfigurationPageTests
{
    [Fact]
    public void CloudConfigurationPage_ExplainsExternalReconnectBoundary()
    {
        const string resourceName =
            "Jellyfin.Plugin.AudioGateway.Configuration.configPage.html";
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var html = reader.ReadToEnd();

        Assert.Contains("remote-authentication-required", html);
        Assert.Contains("rclone config reconnect", html);
        Assert.Contains("localhost:53682", html);
        Assert.Contains("Do not use rclone authorize", html);
    }

    [Fact]
    public void CloudConfigurationPage_OnboardsMultiLibraryVfsAndExplainsScanBoundary()
    {
        const string resourceName =
            "Jellyfin.Plugin.AudioGateway.Configuration.configPage.html";
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var html = reader.ReadToEnd();

        Assert.Contains("VFS setup and migration model", html);
        Assert.Contains("CloudLibraryProfile", html);
        Assert.Contains("Add VFS library", html);
        Assert.Contains("Remove profile", html);
        Assert.Contains("CloudLibraries", html);
        Assert.Contains("projectionId", html);
        Assert.Contains("Companion scheduled waveform/peak/loudness analysis does not traverse VFS libraries", html);
        Assert.Contains("copy it to an empty cloud destination", html);
        Assert.Contains("explicitly finalize local cleanup", html);
        Assert.Contains("migrate back", html);
        Assert.Contains("never uses rclone sync/move/purge", html);
    }
}
