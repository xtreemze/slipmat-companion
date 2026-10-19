using System;
using System.Linq;
using Jellyfin.Plugin.AudioGateway.Models;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public sealed class RemoteSessionProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Project_DeduplicatesSessionIdentityAndPrefersNewestObservation()
    {
        var result = RemoteSessionProjection.Project(
        [
            new("one", "device-a", "Old", true, true, Now.AddSeconds(-10)),
            new("one", "device-a", "New", true, true, Now.AddSeconds(-1)),
            new("two", "device-b", "TV", true, true, Now)
        ], Now);
        Assert.False(result.Authoritative);
        Assert.Equal(1, result.SchemaVersion);
        Assert.Equal(2, result.Sessions.Count);
        Assert.Equal("jellyfin-session:one", result.Sessions[0].Id);
        Assert.Equal("New", result.Sessions[0].Name);
        Assert.Equal("jellyfin-session:two", result.Sessions[1].Id);
    }

    [Fact]
    public void Project_FailsClosedForStaleFutureInactiveAndInvalidSessions()
    {
        var result = RemoteSessionProjection.Project(
        [
            new("old", null, null, true, true, Now.AddSeconds(-46)),
            new("future", null, null, true, true, Now.AddSeconds(1)),
            new("inactive", null, null, false, true, Now),
            new("blocked", null, null, true, false, Now),
            new("../escape", null, null, true, true, Now),
            new("good", null, "TV\nName", true, true, Now)
        ], Now);
        Assert.Single(result.Sessions);
        Assert.Equal("TVName", result.Sessions.Single().Name);
    }

    [Fact]
    public void Project_DoesNotAdvertiseUndetectedDlnaOrContinuousStreaming()
    {
        var result = RemoteSessionProjection.Project(
        [
            new("id", null, "DLNA renderer", true, true, Now)
        ], Now);
        var device = Assert.Single(result.Sessions);
        Assert.Equal("jellyfin-session", device.Kind);
        Assert.DoesNotContain("stream", device.Commands);
        Assert.DoesNotContain("crossfade", device.Commands);
    }

    [Fact]
    public void Project_BoundsResultSize()
    {
        var observations = Enumerable.Range(0, 500)
            .Select(i => new RemoteSessionProjection.Observation(
                $"s{i}", null, "Device", true, true, Now));
        Assert.Equal(128, RemoteSessionProjection.Project(observations, Now).Sessions.Count);
    }
}
