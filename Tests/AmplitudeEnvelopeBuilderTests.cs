using System;
using System.Buffers.Binary;
using System.Linq;
using Jellyfin.Plugin.AudioGateway.Services;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public class AmplitudeEnvelopeBuilderTests
{
    [Fact]
    public void StreamingBuilder_ProducesAllPeakTiersWithoutRetainingPcm()
    {
        var builder = new AmplitudeEnvelopeBuilder(100);
        for (var index = 0; index < 100; index++)
        {
            builder.Push(index == 50 ? 1f : index == 5 ? 0.5f : 0f);
        }

        var tiers = builder.Complete();

        Assert.Equal(100, builder.SampleCount);
        Assert.Equal(1000, builder.DurationMs);
        Assert.Equal(3, tiers.Count);
        Assert.Single(tiers[1]);
        Assert.Equal(10, tiers[10].Length);
        Assert.Equal(100, tiers[100].Length);
        Assert.Equal(byte.MaxValue, tiers[1][0]);
        Assert.Equal(byte.MaxValue, tiers[10][5]);
        Assert.Equal(128, tiers[100][5]);
    }

    [Fact]
    public void RiffEncoder_ProducesStandardMonoU8Envelope()
    {
        var bytes = AmplitudeEnvelopeBuilder.EncodeRiff(10, [0, 128, 255]);

        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(bytes, 8, 4));
        Assert.Equal("fmt ", System.Text.Encoding.ASCII.GetString(bytes, 12, 4));
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(20, 2)));
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(22, 2)));
        Assert.Equal(10u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24, 4)));
        Assert.Equal((ushort)8, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(34, 2)));
        Assert.Equal("JUNK", System.Text.Encoding.ASCII.GetString(bytes, 36, 4));
        Assert.Equal(8u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40, 4)));
        Assert.Equal("data", System.Text.Encoding.ASCII.GetString(bytes, 52, 4));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(56, 4)));
        Assert.Equal(new byte[] { 0, 128, 255 }, bytes.Skip(60).Take(3).ToArray());
        Assert.Equal(64, bytes.Length);
    }

    [Fact]
    public void NonDivisibleSourceRate_UsesIntegerBucketBoundaries()
    {
        var builder = new AmplitudeEnvelopeBuilder(22_050);
        for (var index = 0; index < 22_050; index++)
        {
            builder.Push(index == 22_049 ? 1f : 0f);
        }

        var tiers = builder.Complete();

        Assert.Single(tiers[1]);
        Assert.Equal(10, tiers[10].Length);
        Assert.Equal(100, tiers[100].Length);
        Assert.Equal(byte.MaxValue, tiers[1][0]);
        Assert.Equal(byte.MaxValue, tiers[10][^1]);
        Assert.Equal(byte.MaxValue, tiers[100][^1]);
    }

    [Fact]
    public void PartialTrailingSecond_MatchesCanonicalSlwsProjectionVector()
    {
        var builder = new AmplitudeEnvelopeBuilder(100);
        for (var index = 0; index <= 100; index++)
        {
            builder.Push(index / 255f);
        }

        var tiers = builder.Complete();

        Assert.Equal(new byte[] { 99, 100 }, tiers[1]);
        Assert.Equal(
            new byte[] { 9, 19, 29, 39, 49, 59, 69, 79, 89, 99, 100 },
            tiers[10]);
        Assert.Equal(
            Enumerable.Range(0, 101).Select(index => (byte)index).ToArray(),
            tiers[100]);

        var riff = AmplitudeEnvelopeBuilder.EncodeRiff(10, tiers[10]);
        Assert.Equal(72, riff.Length);
        Assert.Equal(0x6134_D217u, SpectralArtifactEncoder.Crc32(riff));
    }

    [Fact]
    public void NonFiniteSamples_FailClosedToSilence()
    {
        var builder = new AmplitudeEnvelopeBuilder(100);
        builder.Push(float.NaN);
        builder.Push(float.PositiveInfinity);

        var tiers = builder.Complete();
        Assert.All(tiers.Values, values => Assert.All(values, value => Assert.Equal(0, value)));
    }
}
