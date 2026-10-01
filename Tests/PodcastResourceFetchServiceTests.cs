using System;
using System.Net;
using Jellyfin.Plugin.AudioGateway.Services;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public class PodcastResourceFetchServiceTests
{
    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("2606:4700:4700::1111")]
    public void IsPublicAddress_AcceptsGloballyRoutableAddresses(string value)
    {
        Assert.True(PodcastResourceFetchService.IsPublicAddress(IPAddress.Parse(value)));
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("10.1.2.3")]
    [InlineData("100.64.1.2")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.10")]
    [InlineData("192.0.2.1")]
    [InlineData("198.18.0.1")]
    [InlineData("198.51.100.1")]
    [InlineData("203.0.113.1")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1")]
    [InlineData("2001:db8::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("2002:0a00:0001::1")]
    public void IsPublicAddress_RejectsNonGlobalAndEmbeddedPrivateAddresses(string value)
    {
        Assert.False(PodcastResourceFetchService.IsPublicAddress(IPAddress.Parse(value)));
    }

    [Theory]
    [InlineData("https://example.com/feed.xml")]
    [InlineData("http://feeds.example.net:8080/show.rss?format=full")]
    public void TryValidateTargetUri_AcceptsHttpPublicHostSyntax(string value)
    {
        Assert.True(PodcastResourceFetchService.TryValidateTargetUri(value, out var uri));
        Assert.NotNull(uri);
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://user:password@example.com/feed.xml")]
    [InlineData("https://example.com/feed.xml#fragment")]
    [InlineData("http://localhost/feed")]
    [InlineData("http://service.local/feed")]
    [InlineData("http://127.0.0.1/feed")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://[::1]/feed")]
    public void TryValidateTargetUri_RejectsUnsafeSyntaxAndLiteralTargets(string value)
    {
        Assert.False(PodcastResourceFetchService.TryValidateTargetUri(value, out _));
    }

    [Fact]
    public void TryResolveRedirect_RevalidatesAndRejectsTlsDowngrade()
    {
        var current = new Uri("https://feeds.example.com/show.rss");

        Assert.True(PodcastResourceFetchService.TryResolveRedirect(
            current,
            "/canonical/show.rss",
            out var relative));
        Assert.Equal("https://feeds.example.com/canonical/show.rss", relative?.AbsoluteUri);

        Assert.False(PodcastResourceFetchService.TryResolveRedirect(
            current,
            "http://feeds.example.com/show.rss",
            out _));
        Assert.False(PodcastResourceFetchService.TryResolveRedirect(
            current,
            "https://127.0.0.1/show.rss",
            out _));
        Assert.False(PodcastResourceFetchService.TryResolveRedirect(
            current,
            "https://example.com/show.rss#fragment",
            out _));
    }

    [Fact]
    public void MaxBytesForKind_IsBoundedAndRejectsUnknownKinds()
    {
        Assert.Equal(4 * 1024 * 1024, PodcastResourceFetchService.MaxBytesForKind("feed"));
        Assert.Equal(1024 * 1024, PodcastResourceFetchService.MaxBytesForKind("chapters"));
        Assert.Equal(2 * 1024 * 1024, PodcastResourceFetchService.MaxBytesForKind("transcript"));
        Assert.Equal(0, PodcastResourceFetchService.MaxBytesForKind("image"));
    }
}
