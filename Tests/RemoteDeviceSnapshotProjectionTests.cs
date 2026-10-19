using System;
using System.Linq;
using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public sealed class RemoteDeviceSnapshotProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private static RemoteDeviceObservation Item(string owner = "alice", string id = "living-room",
        string provider = "dlna", int observedSecondsAgo = 1) =>
        new(owner, provider, id, "Living room", "generation-1",
            Now.AddSeconds(-observedSecondsAgo), Now.AddSeconds(60),
            new[] { "pause", "play", "play", "unsupported" });

    [Fact]
    public void Snapshot_IsOwnerScopedNonAuthoritativeAndCapabilityBounded()
    {
        var result = RemoteDeviceSnapshotProjection.Create("alice",
            new[] { Item(), Item(owner: "bob"), Item(provider: "unknown", id: "other") }, Now);
        var device = Assert.Single(result.Devices);
        Assert.Equal("dlna:living-room", device.Id);
        Assert.Equal(new[] { "pause", "play" }, device.Actions);
        Assert.False(result.Authoritative);
        Assert.Equal(1, result.Version);
    }

    [Fact]
    public void Snapshot_DiscardsExpiredFutureAndInvalidIdentities()
    {
        var observations = new[]
        {
            Item(observedSecondsAgo: -1),
            Item(id: "bad:id"),
            Item(id: "expired") with { ExpiresAt = Now },
            Item(id: "future") with { ExpiresAt = Now.AddMinutes(5) },
        };
        Assert.Empty(RemoteDeviceSnapshotProjection.Create("alice", observations, Now).Devices);
    }

    [Fact]
    public void Snapshot_PrefersLatestObservationForSameDevice()
    {
        var older = Item() with { SessionGeneration = "generation-1", LastObservedAt = Now.AddSeconds(-20) };
        var newer = Item() with { SessionGeneration = "generation-2" };
        var entry = Assert.Single(RemoteDeviceSnapshotProjection.Create("alice", new[] { older, newer }, Now).Devices);
        Assert.Equal("generation-2", entry.Generation);
    }

    [Fact]
    public void Snapshot_TruncatesDeterministically()
    {
        var entries = Enumerable.Range(0, 260).Select(i => Item(id: $"device-{i:D3}"));
        var snapshot = RemoteDeviceSnapshotProjection.Create("alice", entries, Now);
        Assert.Equal(256, snapshot.Devices.Count);
        Assert.True(snapshot.Truncated);
        Assert.Equal("dlna:device-000", snapshot.Devices[0].Id);
        Assert.Equal("dlna:device-255", snapshot.Devices[^1].Id);
    }
}
