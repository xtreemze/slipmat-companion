using Jellyfin.Plugin.AudioGateway.Services;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public sealed class OpusDeliveryPolicyTests
{
    private static OpusDeliveryRequest Eligible() => new(
        ClientCanDecodeOpus: true,
        ServerCanEncodeOpus: true,
        OpusContainerSupported: true,
        IsRemote: true,
        BandwidthConstrained: true,
        RequiresLossless: false,
        IsDjPerformance: false,
        IsMastering: false,
        RequiresBitPerfect: false,
        HasAuthorizedSource: true,
        SourceIsAudio: true);

    [Fact]
    public void SelectsOpusOnlyForCapableRemoteBandwidthConstrainedPlayback()
    {
        var result = OpusDeliveryPolicy.Select(Eligible());
        Assert.True(result.UseOpusDerivative);
        Assert.Equal("opus-eligible", result.Reason);
    }

    [Theory]
    [InlineData("codec")]
    [InlineData("encoder")]
    [InlineData("container")]
    [InlineData("local")]
    [InlineData("unconstrained")]
    [InlineData("lossless")]
    [InlineData("dj")]
    [InlineData("mastering")]
    [InlineData("bitperfect")]
    [InlineData("unauthorized")]
    [InlineData("notaudio")]
    public void RejectsUnsupportedOrFidelitySensitiveRequests(string condition)
    {
        var request = Eligible();
        request = condition switch
        {
            "codec" => request with { ClientCanDecodeOpus = false },
            "encoder" => request with { ServerCanEncodeOpus = false },
            "container" => request with { OpusContainerSupported = false },
            "local" => request with { IsRemote = false },
            "unconstrained" => request with { BandwidthConstrained = false },
            "lossless" => request with { RequiresLossless = true },
            "dj" => request with { IsDjPerformance = true },
            "mastering" => request with { IsMastering = true },
            "bitperfect" => request with { RequiresBitPerfect = true },
            "unauthorized" => request with { HasAuthorizedSource = false },
            "notaudio" => request with { SourceIsAudio = false },
            _ => request,
        };

        Assert.False(OpusDeliveryPolicy.Select(request).UseOpusDerivative);
    }
}
