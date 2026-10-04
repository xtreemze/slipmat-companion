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
    public void CloudConfigurationPage_ExposesStagedMigrationWorkbench()
    {
        const string resourceName =
            "Jellyfin.Plugin.AudioGateway.Configuration.configPage.html";
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var html = reader.ReadToEnd();

        Assert.Contains("CloudMigrationDirection", html);
        Assert.Contains("Prepare all eligible", html);
        Assert.Contains("Prepare migration", html);
        Assert.Contains("Cut over", html);
        Assert.Contains("Rollback to local", html);
        Assert.Contains("Finalize and free local space", html);
        Assert.Contains("confirmationPath", html);
        Assert.Contains("local-to-vfs", html);
        Assert.Contains("vfs-to-local", html);
        Assert.Contains("copy and exact verification only", html);
        Assert.Contains("Cloud objects are never deleted by finalize", html);
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
        Assert.Contains("Automatic Audio Gateway tasks are payload-safe", html);
        Assert.Contains("does not queue a Jellyfin catalog scan", html);
        Assert.Contains("does not expose a supported cache-residency filter", html);
        Assert.Contains("CloudLibraryProfile", html);
        Assert.Contains("Add VFS library", html);
        Assert.Contains("Remove profile", html);
        Assert.Contains("CloudLibraries", html);
        Assert.Contains("projectionId", html);
        Assert.Contains("scheduled waveform/peak/loudness analysis never traverses VFS media", html);
        Assert.Contains("copy it to an empty cloud destination", html);
        Assert.Contains("Finalize is the only step that frees that retained local-media space", html);
        Assert.Contains("Review and cut over each library independently", html);
        Assert.Contains("explicitly finalize local cleanup", html);
        Assert.Contains("migrate back", html);
        Assert.Contains("never uses rclone sync/move/purge", html);
    }
}
