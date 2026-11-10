using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.AudioGateway.Services;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public class CloudMaintenanceProfileConfigurationTests
{
    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value));
    private static readonly CloudMaintenanceProfileBinding Binding = new("legacy", "remote", "music", "Music", "", false, "/mnt/Music");
    private const string Legacy = "<PluginConfiguration><CloudLibraries/><CloudProjectionEnabled>false</CloudProjectionEnabled><CloudProjectionPath></CloudProjectionPath><CloudRemoteName>remote</CloudRemoteName><CloudRemotePath>music</CloudRemotePath><CloudLibraryName>Music</CloudLibraryName><Other>keep</Other></PluginConfiguration>";

    [Fact]
    public void LegacyProfilePromotesAndRollsBackExactState()
    {
        var original = Bytes(Legacy);
        var promote = CloudMaintenanceProfileConfiguration.PrepareProfile(original, Hash(original), Binding, true);
        Assert.Contains("<CloudProjectionEnabled>true</CloudProjectionEnabled>", Encoding.UTF8.GetString(promote.AfterBytes));
        Assert.Contains("<Other>keep</Other>", Encoding.UTF8.GetString(promote.AfterBytes));
        var replay = CloudMaintenanceProfileConfiguration.PrepareProfile(promote.AfterBytes, promote.AfterHash, Binding, true);
        Assert.Equal(promote.AfterHash, replay.AfterHash);
        Assert.Equal(promote.AfterBytes, replay.AfterBytes);
        var rollback = CloudMaintenanceProfileConfiguration.PrepareProfile(promote.AfterBytes, promote.AfterHash, Binding, false);
        Assert.Contains("<CloudProjectionEnabled>false</CloudProjectionEnabled>", Encoding.UTF8.GetString(rollback.AfterBytes));
        Assert.Throws<InvalidOperationException>(() => CloudMaintenanceProfileConfiguration.PrepareProfile(original, Hash(original), Binding with { RemoteName = "foreign" }, true));
    }

    [Fact]
    public void RejectsMixedOrMultipleProfiles()
    {
        var bytes = Bytes(Legacy.Replace("<CloudLibraries/>", "<CloudLibraries><CloudLibraryProfile/><CloudLibraryProfile/></CloudLibraries>"));
        Assert.Throws<InvalidOperationException>(() => CloudMaintenanceProfileConfiguration.PrepareProfile(bytes, Hash(bytes), Binding, true));
    }
    [Fact]
    public void ExplicitProfileRequiresMatchingLegacyMirror()
    {
        var profile = "<CloudLibraries><CloudLibraryProfile><Id>p1</Id><Enabled>false</Enabled><ProjectionPath></ProjectionPath><RemoteName>remote</RemoteName><RemotePath>music</RemotePath><LibraryName>Music</LibraryName></CloudLibraryProfile></CloudLibraries>";
        var bytes = Bytes(Legacy.Replace("<CloudLibraries/>", profile));
        var plan = CloudMaintenanceProfileConfiguration.PrepareProfile(bytes, Hash(bytes), Binding with { ProfileId = "p1" }, true);
        Assert.Contains("<Enabled>true</Enabled>", Encoding.UTF8.GetString(plan.AfterBytes));
        var bad = Bytes(Encoding.UTF8.GetString(bytes).Replace("<CloudRemoteName>remote", "<CloudRemoteName>other"));
        Assert.Throws<InvalidOperationException>(() => CloudMaintenanceProfileConfiguration.PrepareProfile(bad, Hash(bad), Binding with { ProfileId = "p1" }, true));
    }

    [Fact]
    public void PolicyRequiresExactPathAndSupportedAudioFetchers()
    {
        var names = new[] { "EnableRealtimeMonitor", "EnableLUFSScan", "EnableChapterImageExtraction", "ExtractChapterImagesDuringLibraryScan", "EnableTrickplayImageExtraction", "ExtractTrickplayImagesDuringLibraryScan", "SaveLocalMetadata", "SaveSubtitlesWithMedia", "SaveLyricsWithMedia", "SaveTrickplayWithMedia" };
        var fields = string.Concat(names.Select(n => $"<{n}>true</{n}>"));
        var xml = "<LibraryOptions><PathInfos><MediaPathInfo><Path>/mnt/Music</Path></MediaPathInfo></PathInfos><TypeOptions><TypeOptions><Type>Audio</Type><ImageFetchers/></TypeOptions></TypeOptions>" + fields + "</LibraryOptions>";
        var bytes = Bytes(xml);
        var policy = new Jellyfin.Plugin.AudioGateway.Models.CloudLibraryScanPolicy(true, true, true, true, true, true, true, true, true, true, []);
        var plan = CloudMaintenanceProfileConfiguration.PreparePolicy(bytes, Hash(bytes), "/mnt/Music", policy, true);
        Assert.Contains("<EnableRealtimeMonitor>false</EnableRealtimeMonitor>", Encoding.UTF8.GetString(plan.AfterBytes));
        var replay = CloudMaintenanceProfileConfiguration.PreparePolicy(plan.AfterBytes, plan.AfterHash, "/mnt/Music", policy, true);
        Assert.Equal(plan.AfterHash, replay.AfterHash);
        Assert.Equal(plan.AfterBytes, replay.AfterBytes);
        var restored = CloudMaintenanceProfileConfiguration.PreparePolicy(plan.AfterBytes, plan.AfterHash, "/mnt/Music", policy, false);
        Assert.Contains("<EnableRealtimeMonitor>true</EnableRealtimeMonitor>", Encoding.UTF8.GetString(restored.AfterBytes));
        Assert.Throws<InvalidOperationException>(() => CloudMaintenanceProfileConfiguration.PreparePolicy(bytes, Hash(bytes), "/mnt/Other", policy, true));
        Assert.Throws<InvalidOperationException>(() => CloudMaintenanceProfileConfiguration.PreparePolicy(bytes, Hash(bytes), "/mnt/Music", policy with { AudioImageFetchers = ["Image Extractor"] }, true));
    }
    [Fact]
    public void MissingOptionalFalseFieldsRemainAbsentAndReplayExactly()
    {
        var names = new[] { "EnableRealtimeMonitor", "EnableLUFSScan", "EnableChapterImageExtraction", "ExtractChapterImagesDuringLibraryScan", "EnableTrickplayImageExtraction", "ExtractTrickplayImagesDuringLibraryScan", "SaveLocalMetadata", "SaveSubtitlesWithMedia" };
        var fields = string.Concat(names.Select(n => $"<{n}>false</{n}>"));
        var bytes = Bytes("<LibraryOptions><PathInfos><MediaPathInfo><Path>/mnt/Music</Path></MediaPathInfo></PathInfos><TypeOptions><TypeOptions><Type>Audio</Type><ImageFetchers/></TypeOptions></TypeOptions>" + fields + "</LibraryOptions>");
        var policy = new Jellyfin.Plugin.AudioGateway.Models.CloudLibraryScanPolicy(false, false, false, false, false, false, false, false, false, false, []);
        var plan = CloudMaintenanceProfileConfiguration.PreparePolicy(bytes, Hash(bytes), "/mnt/Music", policy, true);
        Assert.Equal(bytes, plan.AfterBytes);
        Assert.Equal(Hash(bytes), plan.AfterHash);
        Assert.Throws<InvalidOperationException>(() => CloudMaintenanceProfileConfiguration.PreparePolicy(bytes, Hash(bytes), "/mnt/Music", policy with { SaveLyricsWithMedia = true }, false));
    }
}
