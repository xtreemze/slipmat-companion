using Jellyfin.Plugin.AudioGateway.Services.Analysis;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public sealed class BoundaryAccumulatorParityTests
{
    [Fact]
    public void ZeroSampleRateProducesNoEvidenceLikeRustAccumulator()
    {
        var accumulator = new BoundaryAccumulator(0, 2);

        accumulator.PushFrame([0.8f, -0.8f]);

        Assert.Null(accumulator.Complete());
    }
}
