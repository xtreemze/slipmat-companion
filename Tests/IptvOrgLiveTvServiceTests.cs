using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Services;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public class IptvOrgLiveTvServiceTests
{
    [Fact]
    public async Task SelectedChannel_ProjectsThroughJellyfinLiveTv_WithRequiredHeaders()
    {
        var snapshot = CreateSnapshot(
            new IptvOrgCatalogStream(
                ChannelId: "se.example",
                FeedId: "main",
                Title: "Example HD",
                Url: "https://media.example.com/live.m3u8",
                Referrer: "https://www.example.com/",
                UserAgent: "ExamplePlayer/1.0",
                Quality: "1080p",
                IsMainFeed: true));
        var catalog = new IptvOrgCatalogService(_ => Task.FromResult(snapshot));
        var service = new IptvOrgLiveTvService(
            catalog,
            () => new HashSet<string>(StringComparer.Ordinal) { "se.example" });

        var channel = Assert.Single(await service.GetChannelsAsync(CancellationToken.None));
        Assert.Equal("Example TV", channel.Name);
        Assert.Equal("se.example", channel.TunerChannelId);
        Assert.Equal("https://logos.example.com/example.svg", channel.ImageUrl);

        var source = Assert.Single(
            await service.GetChannelStreamMediaSources(channel.Id, CancellationToken.None));
        Assert.Equal("https://media.example.com/live.m3u8", source.Path);
        Assert.Equal("https://www.example.com/", source.RequiredHttpHeaders["Referer"]);
        Assert.Equal("ExamplePlayer/1.0", source.RequiredHttpHeaders["User-Agent"]);
        Assert.False(source.SupportsDirectPlay);
        Assert.True(source.SupportsDirectStream);
        Assert.True(source.SupportsTranscoding);
    }

    [Fact]
    public async Task UnselectedChannel_IsNotPublishedOrPlayableThroughJellyfin()
    {
        var snapshot = CreateSnapshot(
            new IptvOrgCatalogStream(
                ChannelId: "se.example",
                FeedId: "main",
                Title: null,
                Url: "https://media.example.com/live.ts",
                Referrer: null,
                UserAgent: null,
                Quality: null,
                IsMainFeed: true));
        var catalog = new IptvOrgCatalogService(_ => Task.FromResult(snapshot));
        var selectedService = new IptvOrgLiveTvService(
            catalog,
            () => new HashSet<string>(StringComparer.Ordinal) { "se.example" });
        var channel = Assert.Single(await selectedService.GetChannelsAsync(CancellationToken.None));

        var unselectedService = new IptvOrgLiveTvService(
            catalog,
            () => new HashSet<string>(StringComparer.Ordinal));

        Assert.Empty(await unselectedService.GetChannelsAsync(CancellationToken.None));
        Assert.Empty(
            await unselectedService.GetChannelStreamMediaSources(channel.Id, CancellationToken.None));
    }

    [Fact]
    public async Task HeaderlessByteStream_RemainsEligibleForDirectPlay()
    {
        var snapshot = CreateSnapshot(
            new IptvOrgCatalogStream(
                ChannelId: "se.example",
                FeedId: "main",
                Title: null,
                Url: "https://media.example.com/live.ts",
                Referrer: null,
                UserAgent: null,
                Quality: null,
                IsMainFeed: true));
        var catalog = new IptvOrgCatalogService(_ => Task.FromResult(snapshot));
        var service = new IptvOrgLiveTvService(
            catalog,
            () => new HashSet<string>(StringComparer.Ordinal) { "se.example" });

        var channel = Assert.Single(await service.GetChannelsAsync(CancellationToken.None));
        var source = Assert.Single(
            await service.GetChannelStreamMediaSources(channel.Id, CancellationToken.None));

        Assert.Empty(source.RequiredHttpHeaders);
        Assert.True(source.SupportsDirectPlay);
    }

    private static IptvOrgCatalogSnapshot CreateSnapshot(IptvOrgCatalogStream stream)
    {
        var channel = new IptvOrgCatalogChannel(
            Id: "se.example",
            Name: "Example TV",
            AlternateNames: Array.Empty<string>(),
            CountryCode: "SE",
            Network: "Example Network",
            Owners: Array.Empty<string>(),
            Categories: new[] { "general" },
            Languages: new[] { "swe" },
            LogoUrl: "https://logos.example.com/example.svg",
            Logos: new[]
            {
                new IptvOrgCatalogLogo(
                    ChannelId: "se.example",
                    FeedId: "main",
                    Url: "https://logos.example.com/example.svg",
                    Width: 512,
                    Height: 288,
                    Format: "SVG",
                    Tags: Array.Empty<string>(),
                    InUse: true),
            },
            Streams: new[] { stream },
            GuideSources: new[]
            {
                new IptvOrgCatalogGuideSource(
                    ChannelId: "se.example",
                    FeedId: "main",
                    Url: "https://guide.example.com/guide.xml",
                    Format: "XML"),
            },
            HasGuide: true);

        return new IptvOrgCatalogSnapshot(
            ObservedAt: DateTimeOffset.Parse("2026-10-02T09:00:00Z"),
            Channels: new[] { channel },
            ById: new Dictionary<string, IptvOrgCatalogChannel>(StringComparer.Ordinal)
            {
                [channel.Id] = channel,
            },
            GuideSourceUrls: new HashSet<string>(StringComparer.Ordinal)
            {
                "https://guide.example.com/guide.xml",
            });
    }
}
