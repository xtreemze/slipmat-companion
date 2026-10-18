using System;
using Jellyfin.Plugin.AudioGateway.Services;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public sealed class ClientPresenceRegistryTests
{
    [Fact]
    public void PresenceIsPrivateToOwnerAndExpires()
    {
        var now = DateTimeOffset.Parse("2026-10-18T12:00:00Z");
        var registry = new ClientPresenceRegistry(() => now);
        var owner = Guid.NewGuid();
        Assert.True(registry.Observe(owner, Guid.NewGuid(), Guid.NewGuid(), " Player "));
        Assert.Empty(registry.ListForOwner(Guid.NewGuid()));
        Assert.Equal("Player", Assert.Single(registry.ListForOwner(owner)).DisplayName);
        now = now.AddMinutes(2);
        Assert.Empty(registry.ListForOwner(owner));
    }

    [Fact]
    public void ReconnectionRequiresExplicitGenerationReplacement()
    {
        var registry = new ClientPresenceRegistry();
        var owner = Guid.NewGuid();
        var client = Guid.NewGuid();
        var original = Guid.NewGuid();
        var next = Guid.NewGuid();
        Assert.True(registry.Observe(owner, client, original, "Old"));
        Assert.False(registry.Observe(owner, client, next, "New"));
        Assert.True(registry.ReplaceSession(owner, client, next, "New"));
        Assert.False(registry.Observe(owner, client, original, "Stale"));
        Assert.False(registry.Remove(owner, client, original));
        Assert.Equal("New", Assert.Single(registry.ListForOwner(owner)).DisplayName);
        Assert.True(registry.Remove(owner, client, next));
        Assert.Empty(registry.ListForOwner(owner));
    }

    [Fact]
    public void InvalidIdentityAndNamesAreRejected()
    {
        var registry = new ClientPresenceRegistry();
        var id = Guid.NewGuid();
        Assert.False(registry.Observe(Guid.Empty, id, id, "Client"));
        Assert.False(registry.Observe(id, Guid.Empty, id, "Client"));
        Assert.False(registry.Observe(id, id, Guid.Empty, "Client"));
        Assert.False(registry.Observe(id, id, id, " "));
        Assert.False(registry.Observe(id, id, id, new string('x', 129)));
        Assert.Empty(registry.ListForOwner(id));
    }

    [Fact]
    public void RegistryBoundsMemoryUnderUntrustedArrivalRates()
    {
        var registry = new ClientPresenceRegistry();
        var owner = Guid.NewGuid();
        for (var index = 0; index < 256; index++)
            Assert.True(registry.Observe(owner, Guid.NewGuid(), Guid.NewGuid(), "Client"));

        Assert.False(registry.Observe(owner, Guid.NewGuid(), Guid.NewGuid(), "Overflow"));
        Assert.Equal(256, registry.ListForOwner(owner).Count);
    }
}
