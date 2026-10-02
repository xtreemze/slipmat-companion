using System;
using Jellyfin.Plugin.AudioGateway.Services.Analysis;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public class RhythmNumericalParityTests
{
    [Fact]
    public void PositiveLogDelta_MatchesRustLn1pAtSubnormalScale()
    {
        const float current = 1e-12f;

        var actual = RhythmGridAnalyzer.PositiveLogDelta(current, 0f);

        Assert.Equal(9.999999955041971e-10d, actual);
        Assert.NotEqual(
            Math.Log(1d + current * 1_000d),
            actual);
        Assert.NotEqual(
            double.LogP1(current * 1_000d),
            actual);
    }

    [Fact]
    public void PositiveLogDelta_StillFailsClosedForFallingEnergy()
    {
        Assert.Equal(
            0d,
            RhythmGridAnalyzer.PositiveLogDelta(0.1f, 0.2f));
    }
}
