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
        Assert.Contains("never deletes cloud media", html);
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

    [Fact]
    public void CloudConfigurationPage_ShowsSetupReadinessAndScanGuidance()
    {
        const string resourceName =
            "Jellyfin.Plugin.AudioGateway.Configuration.configPage.html";
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var html = reader.ReadToEnd();

        Assert.Contains("Setup readiness", html);
        Assert.Contains("cloud/rclone/preflight", html);
        Assert.Contains("CloudPreflightList", html);
        Assert.Contains("Re-check readiness", html);
        Assert.Contains("the library is usable now", html);
        Assert.Contains("The scan cannot be paused", html);
        Assert.Contains("Embedded cover-art extraction", html);
        Assert.Contains("CloudCacheRecommendation", html);
        Assert.Contains("CloudApplyCacheRecommendationButton", html);
        Assert.Contains("cacheRecommendations", html);
        Assert.Contains("Save settings to persist it", html);

        // Jellyfin injects only the page body; a <style> in <head> is dropped, leaving the
        // whole dashboard unstyled.
        var page = html.IndexOf("id=\"AudioGatewayConfigPage\"", System.StringComparison.Ordinal);
        var style = html.IndexOf("<style>", System.StringComparison.Ordinal);
        Assert.True(page >= 0 && style > page, "page <style> must live inside the page container");

        var start = html.IndexOf("function renderCloudPreflight", System.StringComparison.Ordinal);
        var end = html.IndexOf("async function loadCloudPreflight", System.StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        Assert.DoesNotContain("innerHTML", html.Substring(start, end - start));
    }
}
