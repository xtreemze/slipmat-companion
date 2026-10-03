using Jellyfin.Plugin.AudioGateway.Services.Analysis;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public sealed class RhythmAccumulatorParityTests
{
    [Fact]
    public void ZeroSampleRateFailsClosedLikeRustAccumulator()
    {
        var accumulator = new RhythmAccumulator(0, 2);

        accumulator.PushFrame([0.8f, -0.8f]);
        var result = accumulator.Complete(null);

        Assert.Null(result.Bpm);
        Assert.Empty(result.Beats);
        Assert.Empty(result.Downbeats);
        Assert.Null(result.BeatsPerBar);
        Assert.Empty(accumulator.Frames);
    }
}
