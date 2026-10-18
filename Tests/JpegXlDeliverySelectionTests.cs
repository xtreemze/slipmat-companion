using System;
using System.Security.Cryptography;
using Jellyfin.Plugin.AudioGateway.Services;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public sealed class JpegXlDeliverySelectionTests
{
    [Fact]
    public void UsesOriginalWhenClientCapabilityIsUnknown()
    {
        var source = Source();
        var result = JpegXlDeliverySelection.Select(source, Derivative(source), false);

        Assert.Same(source, result.Bytes);
        Assert.Equal("image/jpeg", result.ContentType);
        Assert.False(result.IsDerivative);
    }

    [Fact]
    public void UsesVerifiedDerivativeForCapableClient()
    {
        var source = Source();
        var candidate = Derivative(source);

        var result = JpegXlDeliverySelection.Select(source, candidate, true);

        Assert.Same(candidate.Bytes, result.Bytes);
        Assert.Equal("image/jxl", result.ContentType);
        Assert.True(result.IsDerivative);
    }

    [Fact]
    public void RejectsStaleOrNonSavingDerivatives()
    {
        var source = Source();
        var valid = Derivative(source);

        Assert.False(JpegXlDeliverySelection.Select(
            source, valid with { SourceJpegSha256 = new string('a', 64) }, true)
            .IsDerivative);
        Assert.False(JpegXlDeliverySelection.Select(
            source, valid with { Bytes = new byte[source.Length] }, true)
            .IsDerivative);
        Assert.False(JpegXlDeliverySelection.Select(
            source, valid with { Bytes = [] }, true)
            .IsDerivative);
        Assert.False(JpegXlDeliverySelection.Select(source, null, true).IsDerivative);
    }

    [Fact]
    public void SourceReplacementInvalidatesCachedDerivative()
    {
        var source = Source();
        var cached = Derivative(source);
        var replaced = (byte[])source.Clone();
        replaced[4] ^= 1;

        Assert.False(JpegXlDeliverySelection.Select(
            replaced, cached, true).IsDerivative);
    }

    private static byte[] Source() => [0xff, 0xd8, 0xff, 0xe0, 1, 2, 3, 4, 5, 6, 0xff, 0xd9];

    private static VerifiedJpegXl Derivative(byte[] source) =>
        new([0xff, 0x0a, 0x01], Convert.ToHexStringLower(SHA256.HashData(source)));
}
