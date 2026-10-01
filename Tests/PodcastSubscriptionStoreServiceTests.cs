using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public class PodcastSubscriptionStoreServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "slipmat-podcast-subscriptions-tests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Contract_RejectsSecretBearingPortableLocator()
    {
        var record = CreateRecord(
            portableFeedLocator: "https://example.test/feed.xml?token=secret");
        Assert.False(PodcastSubscriptionStoreService.IsValidRecord(record));
    }

    [Fact]
    public void Merge_EqualSequenceConflict_PrefersUnsubscribeTombstone()
    {
        var subscribed = CreateRecord(status: "subscribed", sequence: 2, originId: "device-b");
        var tombstone = CreateRecord(status: "unsubscribed", sequence: 2, originId: "device-a");

        var winner = PodcastSubscriptionStoreService.Merge(subscribed, tombstone);
        Assert.Equal("unsubscribed", winner.Status);
    }

    [Fact]
    public async Task MergeAsync_PersistsPerUserAndDoesNotLeakAcrossUsers()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var remote = CreateRecord();

        var first = await PodcastSubscriptionStoreService.MergeAsync(
            _root,
            userA,
            new List<PodcastSubscriptionRecordV1> { remote });

        Assert.Equal(1, first.Applied);
        Assert.Single(await PodcastSubscriptionStoreService.LoadAsync(_root, userA));
        Assert.Empty(await PodcastSubscriptionStoreService.LoadAsync(_root, userB));
    }

    [Fact]
    public async Task MergeAsync_StaleSubscribeCannotResurrectNewerTombstone()
    {
        var user = Guid.NewGuid();
        var initial = CreateRecord(status: "subscribed", sequence: 1, originId: "device-a");
        var tombstone = CreateRecord(status: "unsubscribed", sequence: 2, originId: "device-a");

        await PodcastSubscriptionStoreService.MergeAsync(
            _root,
            user,
            new List<PodcastSubscriptionRecordV1> { tombstone });
        var response = await PodcastSubscriptionStoreService.MergeAsync(
            _root,
            user,
            new List<PodcastSubscriptionRecordV1> { initial });

        Assert.Equal(0, response.Applied);
        Assert.Equal(1, response.Unchanged);
        Assert.Single(response.Records);
        Assert.Equal("unsubscribed", response.Records[0].Status);
        Assert.Equal(2, response.Records[0].Revision.Sequence);
    }

    [Fact]
    public async Task MergeAsync_AcceptsTombstoneWithoutExistingServerReplica()
    {
        var user = Guid.NewGuid();
        var tombstone = CreateRecord(status: "unsubscribed", sequence: 4, originId: "device-a");

        var response = await PodcastSubscriptionStoreService.MergeAsync(
            _root,
            user,
            new List<PodcastSubscriptionRecordV1> { tombstone });

        Assert.Equal(1, response.Applied);
        var loaded = await PodcastSubscriptionStoreService.LoadAsync(_root, user);
        Assert.Single(loaded);
        Assert.Equal("unsubscribed", loaded[0].Status);
    }

    [Fact]
    public async Task MergeAsync_RejectsInvalidInputWithoutReplacingKnownGoodState()
    {
        var user = Guid.NewGuid();
        var valid = CreateRecord();
        await PodcastSubscriptionStoreService.MergeAsync(
            _root,
            user,
            new List<PodcastSubscriptionRecordV1> { valid });

        var invalid = valid with { SubscriptionId = "wrong" };
        var response = await PodcastSubscriptionStoreService.MergeAsync(
            _root,
            user,
            new List<PodcastSubscriptionRecordV1> { invalid });

        Assert.Equal(1, response.Rejected);
        Assert.Single(response.Records);
        Assert.Equal(valid, response.Records[0]);
    }

    private static PodcastSubscriptionRecordV1 CreateRecord(
        string status = "subscribed",
        long sequence = 1,
        string originId = "device-a",
        string? portableFeedLocator = "https://example.test/feed.xml")
    {
        var feed = new PodcastSyndicationFeedRefV1(
            Version: 1,
            Kind: "syndication-feed",
            Resource: new PodcastProviderResourceRefV1(
                Version: 1,
                ProviderId: "rss",
                ResourceId: "feed-1"));
        var revision = new PodcastSubscriptionRevisionV1(sequence, originId);
        return new PodcastSubscriptionRecordV1(
            Version: 1,
            SubscriptionId: "podcast-subscription:syndication-feed:rss:feed-1",
            Feed: feed,
            PortableFeedLocator: portableFeedLocator,
            Status: status,
            CreatedRevision: new PodcastSubscriptionRevisionV1(1, "device-a"),
            Revision: revision);
    }
}
