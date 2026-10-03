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
        Assert.Contains("do not use rclone authorize", html);
    }
}
