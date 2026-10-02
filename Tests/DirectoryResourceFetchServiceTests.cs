using Jellyfin.Plugin.AudioGateway.Services;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public class DirectoryResourceFetchServiceTests
{
    [Theory]
    [InlineData(
        "gpodder.net",
        "https://gpodder.net/search.json?q=music&scale_logo=256")]
    [InlineData(
        "radio-browser",
        "https://all.api.radio-browser.info/json/servers")]
    [InlineData(
        "radio-browser",
        "https://de1.api.radio-browser.info/json/stations/search?name=jazz&hidebroken=true&limit=20&order=votes&reverse=true")]
    [InlineData(
        "radio-browser",
        "https://de1.api.radio-browser.info/json/stations/topvote/24?hidebroken=true")]
    [InlineData(
        "iptv-org",
        "https://iptv-org.github.io/api/channels.json")]
    [InlineData(
        "iptv-org",
        "https://iptv-org.github.io/api/streams.json")]
    public void TryValidateDirectoryTarget_AllowsOnlyKnownDirectoryShapes(
        string providerId,
        string url)
    {
        Assert.True(
            DirectoryResourceFetchService.TryValidateDirectoryTarget(
                providerId,
                url,
                out var target,
                out var maxBytes));
        Assert.NotNull(target);
        Assert.True(maxBytes > 0);
    }

    [Theory]
    [InlineData(
        "gpodder.net",
        "https://evil.example/search.json?q=music&scale_logo=256")]
    [InlineData(
        "gpodder.net",
        "https://gpodder.net/search.json?q=x&scale_logo=256")]
    [InlineData(
        "gpodder.net",
        "https://gpodder.net/search.json?q=music&scale_logo=512")]
    [InlineData(
        "radio-browser",
        "https://de1.api.radio-browser.info/json/stations/search?name=jazz&limit=500")]
    [InlineData(
        "radio-browser",
        "https://de1.api.radio-browser.info/json/stations/search?name=jazz&hidebroken=false&limit=20&order=votes&reverse=true")]
    [InlineData(
        "radio-browser",
        "https://de1.api.radio-browser.info/json/stations/search?name=jazz&hidebroken=true&limit=20&order=name&reverse=true")]
    [InlineData(
        "radio-browser",
        "https://de1.api.radio-browser.info/json/stations/search?name=jazz&url=https%3A%2F%2Fevil.example")]
    [InlineData(
        "iptv-org",
        "https://iptv-org.github.io/api/channels.json?unexpected=true")]
    [InlineData(
        "iptv-org",
        "https://iptv-org.github.io/api/unknown.json")]
    [InlineData(
        "unknown",
        "https://iptv-org.github.io/api/channels.json")]
    public void TryValidateDirectoryTarget_RejectsUnknownHostsShapesAndQueryExpansion(
        string providerId,
        string url)
    {
        Assert.False(
            DirectoryResourceFetchService.TryValidateDirectoryTarget(
                providerId,
                url,
                out _,
                out _));
    }
}
