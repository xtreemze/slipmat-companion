using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Services;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public class IptvOrgCatalogServiceTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("main", true)]
    [InlineData("other", false)]
    public void FeedReferenceRule_RejectsUnknownScopedEvidenceWhenFeedsAreDeclared(
        string? feedId,
        bool expected)
    {
        var knownFeeds = new HashSet<string>(StringComparer.Ordinal) { "main" };

        Assert.Equal(
            expected,
            IptvOrgCatalogService.IsFeedReferenceAllowed(feedId, knownFeeds));
    }

    [Fact]
    public void FeedReferenceRule_AllowsScopedEvidenceWhenChannelHasNoDeclaredFeeds()
    {
        Assert.True(
            IptvOrgCatalogService.IsFeedReferenceAllowed(
                "provider-feed",
                new HashSet<string>(StringComparer.Ordinal)));
    }

    [Fact]
    public async Task RefreshAsync_RetainsLastKnownGoodSnapshotAfterProviderFailure()
    {
        var calls = 0;
        var initial = Snapshot("https://guide.example.com/a.xml", "Example A");
        var catalog = new IptvOrgCatalogService(_ =>
        {
            calls++;
            if (calls == 1)
            {
                return Task.FromResult(initial);
            }

            throw new InvalidOperationException("provider unavailable");
        });

        var loaded = await catalog.GetAsync(CancellationToken.None);
        var refreshed = await catalog.RefreshAsync(CancellationToken.None);

        Assert.Same(initial, loaded);
        Assert.Same(initial, refreshed.Snapshot);
        Assert.True(refreshed.UsedLastKnownGood);
        Assert.Same(initial, await catalog.GetAsync(CancellationToken.None));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task RefreshAsync_PublishesNewSnapshotAtomically()
    {
        var calls = 0;
        var first = Snapshot("https://guide.example.com/a.xml", "Example A");
        var second = Snapshot("https://guide.example.com/b.xml", "Example B");
        var catalog = new IptvOrgCatalogService(_ =>
        {
            calls++;
            return Task.FromResult(calls == 1 ? first : second);
        });

        Assert.Same(first, await catalog.GetAsync(CancellationToken.None));
        var refreshed = await catalog.RefreshAsync(CancellationToken.None);

        Assert.False(refreshed.UsedLastKnownGood);
        Assert.Same(second, refreshed.Snapshot);
        Assert.Same(second, await catalog.GetAsync(CancellationToken.None));
    }

    private static IptvOrgCatalogSnapshot Snapshot(string guideUrl, string name)
    {
        var channel = new IptvOrgCatalogChannel(
            Id: "se.example",
            Name: name,
            AlternateNames: Array.Empty<string>(),
            CountryCode: "SE",
            Network: null,
            Owners: Array.Empty<string>(),
            Categories: Array.Empty<string>(),
            Languages: Array.Empty<string>(),
            LogoUrl: null,
            Logos: Array.Empty<IptvOrgCatalogLogo>(),
            Streams: new[]
            {
                new IptvOrgCatalogStream(
                    ChannelId: "se.example",
                    FeedId: null,
                    Title: null,
                    Url: "https://media.example.com/live.ts",
                    Referrer: null,
                    UserAgent: null,
                    Quality: null,
                    IsMainFeed: false),
            },
            GuideSources: new[]
            {
                new IptvOrgCatalogGuideSource(
                    ChannelId: "se.example",
                    FeedId: null,
                    Url: guideUrl,
                    Format: "XML"),
            },
            HasGuide: true);

        return new IptvOrgCatalogSnapshot(
            ObservedAt: DateTimeOffset.UtcNow,
            Channels: new[] { channel },
            ById: new Dictionary<string, IptvOrgCatalogChannel>(StringComparer.Ordinal)
            {
                [channel.Id] = channel,
            },
            GuideSourceUrls: new HashSet<string>(StringComparer.Ordinal)
            {
                guideUrl,
            });
    }
}
