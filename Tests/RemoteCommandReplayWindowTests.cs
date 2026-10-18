using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Services;
using Xunit;
namespace Jellyfin.Plugin.AudioGateway.Tests;

public sealed class RemoteCommandReplayWindowTests
{
    [Fact]
    public void DuplicateIsRejectedButSessionAndOwnerRemainDistinct()
    {
        var w = new RemoteCommandReplayWindow();
        Assert.True(w.TryAdmit("alice","phone","g1","play"));
        Assert.False(w.TryAdmit("alice","phone","g1","play"));
        Assert.True(w.TryAdmit("alice","phone","g2","play"));
        Assert.True(w.TryAdmit("bob","phone","g1","play"));
    }

    [Fact]
    public void CapacityFailsClosedUntilExpiry()
    {
        var now = DateTimeOffset.UnixEpoch;
        var w = new RemoteCommandReplayWindow(1, TimeSpan.FromSeconds(5), () => now);
        Assert.True(w.TryAdmit("a","b","c","1"));
        Assert.False(w.TryAdmit("a","b","c","2"));
        now = now.AddSeconds(5);
        Assert.True(w.TryAdmit("a","b","c","2"));
    }

    [Fact]
    public void InvalidIdentifiersAreRejected()
    {
        var w = new RemoteCommandReplayWindow();
        Assert.False(w.TryAdmit("","b","c","d"));
        Assert.False(w.TryAdmit("a","b","c","d\n"));
        Assert.False(w.TryAdmit("a","b","c",new string('x',129)));
    }

    [Fact]
    public async Task DuplicateConcurrencyIsAtomic()
    {
        var w = new RemoteCommandReplayWindow();
        var accepted = 0;
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            if (w.TryAdmit("a","b","c","d")) Interlocked.Increment(ref accepted);
        })));
        Assert.Equal(1,accepted);
    }
}
