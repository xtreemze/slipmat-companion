using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public class RemoteMediaRelayServiceTests : IDisposable
{
    public RemoteMediaRelayServiceTests()
    {
        RemoteMediaRelayService.ClearForTesting();
    }

    public void Dispose()
    {
        RemoteMediaRelayService.ClearForTesting();
    }

    [Theory]
    [InlineData("podcast-enclosure")]
    [InlineData("radio-stream")]
    public void Prepare_ExchangesPublicLocatorForOpaqueCorsReadableHandle(string kind)
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000);
        var response = RemoteMediaRelayService.Prepare(
            new RemoteMediaRelayPrepareRequest(
                "https://media.example.com/audio.mp3?signature=private-value",
                kind),
            now);

        Assert.True(RemoteMediaRelayService.IsValidRelayId(response.RelayId));
        Assert.Equal($"/Plugins/AudioGateway/media-relay/{response.RelayId}", response.MediaPath);
        Assert.Equal("cors-readable", response.SampleAccess);
        Assert.Equal(now.Add(RemoteMediaRelayService.TicketLifetime).ToUnixTimeMilliseconds(), response.ExpiresAtMs);
        Assert.DoesNotContain("media.example.com", response.MediaPath, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-value", response.MediaPath, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://127.0.0.1/private.mp3")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("https://user:password@example.com/secret.mp3")]
    public void Prepare_RejectsUnsafeTargets(string target)
    {
        var exception = Assert.Throws<RemoteMediaRelayService.RelayException>(() =>
            RemoteMediaRelayService.Prepare(
                new RemoteMediaRelayPrepareRequest(target, "podcast-enclosure")));

        Assert.Equal("unsafe-target", exception.Code);
        Assert.Equal(HttpStatusCode.BadRequest, exception.ResponseStatus);
    }

    [Fact]
    public void Prepare_RemainsStrictlyBoundedAtCapacity()
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000);
        for (var index = 0; index < RemoteMediaRelayService.MaxTickets + 8; index++)
        {
            RemoteMediaRelayService.Prepare(
                new RemoteMediaRelayPrepareRequest(
                    $"https://media.example.com/audio-{index}.mp3",
                    "podcast-enclosure"),
                now.AddMilliseconds(index));
        }

        Assert.Equal(
            RemoteMediaRelayService.MaxTickets,
            RemoteMediaRelayService.TicketCountForTesting);
    }

    [Fact]
    public void Prepare_RejectsUnknownKinds()
    {
        var exception = Assert.Throws<RemoteMediaRelayService.RelayException>(() =>
            RemoteMediaRelayService.Prepare(
                new RemoteMediaRelayPrepareRequest("https://media.example.com/audio.mp3", "generic-proxy")));

        Assert.Equal("unsupported-kind", exception.Code);
        Assert.Equal(HttpStatusCode.BadRequest, exception.ResponseStatus);
    }

    [Fact]
    public async Task OpenAsync_RejectsExpiredTicketsBeforeNetworkAccess()
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000);
        var prepared = RemoteMediaRelayService.Prepare(
            new RemoteMediaRelayPrepareRequest(
                "https://media.example.com/audio.mp3",
                "podcast-enclosure"),
            now);

        var exception = await Assert.ThrowsAsync<RemoteMediaRelayService.RelayException>(() =>
            RemoteMediaRelayService.OpenAsync(
                prepared.RelayId,
                range: null,
                now.Add(RemoteMediaRelayService.TicketLifetime).AddSeconds(1),
                CancellationToken.None));

        Assert.Equal("relay-expired", exception.Code);
        Assert.Equal(HttpStatusCode.NotFound, exception.ResponseStatus);
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa!")]
    public void IsValidRelayId_RejectsMalformedHandles(string relayId)
    {
        Assert.False(RemoteMediaRelayService.IsValidRelayId(relayId));
    }
}
