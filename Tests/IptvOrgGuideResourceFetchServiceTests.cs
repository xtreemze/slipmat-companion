using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AudioGateway.Services;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public class IptvOrgGuideResourceFetchServiceTests
{
    [Fact]
    public void ListedGuideTarget_RequiresExactNormalizedCatalogEvidence()
    {
        const string listed = "https://guide.example.com/guide.xml?channel=1";
        var snapshot = new IptvOrgCatalogSnapshot(
            ObservedAt: DateTimeOffset.UtcNow,
            Channels: Array.Empty<IptvOrgCatalogChannel>(),
            ById: new Dictionary<string, IptvOrgCatalogChannel>(StringComparer.Ordinal),
            GuideSourceUrls: new HashSet<string>(StringComparer.Ordinal) { listed });

        Assert.True(IptvOrgGuideResourceFetchService.IsListedGuideTarget(snapshot, listed));
        Assert.False(IptvOrgGuideResourceFetchService.IsListedGuideTarget(
            snapshot,
            "https://guide.example.com/guide.xml?channel=2"));
        Assert.False(IptvOrgGuideResourceFetchService.IsListedGuideTarget(
            snapshot,
            "http://localhost/guide.xml"));
        Assert.False(IptvOrgGuideResourceFetchService.IsListedGuideTarget(snapshot, null));
    }
}
