using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.AudioGateway.Services;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public class CloudMaintenanceConfigurationTests
{
    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value));

    [Fact]
    public void PlansExactPatchPreservingUnknownElementsAndDetectsDrift()
    {
        var original = Bytes("<PluginConfiguration><Enabled>false</Enabled><Unknown secret=\"yes\">value</Unknown></PluginConfiguration>");
        var plan = CloudMaintenanceConfiguration.Prepare(original, Hash(original), "PluginConfiguration",
            [new(["Enabled"], "false", "true")]);
        Assert.Equal(Hash(original), plan.BeforeHash);
        Assert.Equal(Hash(plan.AfterBytes), plan.AfterHash);
        var xml = Encoding.UTF8.GetString(plan.AfterBytes);
        Assert.Contains("<Enabled>true</Enabled>", xml);
        Assert.Contains("<Unknown secret=\"yes\">value</Unknown>", xml);
        Assert.Throws<InvalidOperationException>(() => CloudMaintenanceConfiguration.Prepare(original, new string('0', 64), "PluginConfiguration", [new(["Enabled"], "false", "true")]));
    }

    [Theory]
    [InlineData("<PluginConfiguration><Enabled>false</Enabled><Enabled>false</Enabled></PluginConfiguration>")]
    [InlineData("<!DOCTYPE PluginConfiguration [<!ENTITY e 'false'>]><PluginConfiguration><Enabled>&e;</Enabled></PluginConfiguration>")]
    [InlineData("<Wrong><Enabled>false</Enabled></Wrong>")]
    [InlineData("<PluginConfiguration><Enabled><Nested>false</Nested></Enabled></PluginConfiguration>")]
    public void RejectsAmbiguousOrUnsafeXml(string input)
    {
        var bytes = Bytes(input);
        Assert.ThrowsAny<Exception>(() => CloudMaintenanceConfiguration.Prepare(bytes, Hash(bytes), "PluginConfiguration", [new(["Enabled"], "false", "true")]));
    }

    [Fact]
    public void RejectsUnexpectedValuesAndOverlappingPatches()
    {
        var bytes = Bytes("<LibraryOptions><EnableRealtimeMonitor>false</EnableRealtimeMonitor></LibraryOptions>");
        Assert.Throws<InvalidOperationException>(() => CloudMaintenanceConfiguration.Prepare(bytes, Hash(bytes), "LibraryOptions", [new(["EnableRealtimeMonitor"], "true", "false")]));
        Assert.Throws<InvalidOperationException>(() => CloudMaintenanceConfiguration.Prepare(bytes, Hash(bytes), "LibraryOptions", [new(["EnableRealtimeMonitor"], "false", "true"), new(["EnableRealtimeMonitor"], "true", "false")]));
    }
}
