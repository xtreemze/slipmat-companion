using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services.Analysis;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public class HigherOrderAudioAnalysisTests
{
    [Fact]
    public void RhythmGridAnalyzer_DetectsConservative120BpmPulseGrid()
    {
        const int fps = 20;
        var frames = Enumerable.Repeat(default(RhythmEnergyFrame), 40 * fps).ToArray();
        var beatIndex = 0;
        for (var frame = 4; frame < frames.Length; frame += 10)
        {
            var downbeat = beatIndex % 4 == 0;
            frames[frame] = new RhythmEnergyFrame(
                Overall: downbeat ? 0.9f : 0.65f,
                Low: downbeat ? 0.95f : 0.28f,
                LowMid: downbeat ? 0.8f : 0.32f,
                Mid: 0.5f,
                High: 0.32f);
            beatIndex++;
        }

        var result = RhythmGridAnalyzer.Analyze(frames, fps, 40d);

        Assert.Equal(120d, result.Bpm);
        Assert.True(result.Confidence >= 0.55d);
        Assert.True(result.Beats.Count > 60);
        Assert.True(result.Downbeats.Count > 12);
        Assert.Equal((byte)4, result.BeatsPerBar);
    }

    [Fact]
    public void RhythmGridAnalyzer_SteadyEnergyFailsClosed()
    {
        var frames = Enumerable.Repeat(
            new RhythmEnergyFrame(0.2f, 0.1f, 0.1f, 0.1f, 0.1f),
            800).ToArray();

        var result = RhythmGridAnalyzer.Analyze(frames, 20, 40d);

        Assert.Null(result.Bpm);
        Assert.Empty(result.Beats);
        Assert.Empty(result.Downbeats);
    }

    [Fact]
    public void HarmonicAccumulator_MapsDetectedMajorKeyToCamelot()
    {
        const int sampleRate = 48_000;
        var analyzer = new HarmonicAccumulator(sampleRate);
        var frequencies = new[] { 261.625565d, 329.627557d, 391.995436d };

        for (var index = 0; index < sampleRate * 5; index++)
        {
            var sample = frequencies
                .Select(frequency =>
                    Math.Sin(2d * Math.PI * frequency * index / sampleRate))
                .Average() * 0.5d;
            analyzer.Push((float)sample);
        }

        var result = analyzer.Complete();

        Assert.Equal("C Major", result.Key);
        Assert.Equal("8B", result.CamelotKey);
        Assert.InRange(result.Confidence, 0d, 1d);
    }

    [Fact]
    public void SpectralArtifact_UsesCanonicalFiveBandSlwsV2Header()
    {
        const int sampleRate = 48_000;
        var spectral = new SpectralAccumulator(sampleRate);
        for (var index = 0; index < sampleRate * 2; index++)
        {
            spectral.Push((float)(0.7d * Math.Sin(2d * Math.PI * 1_000d * index / sampleRate)));
        }

        var emptyRhythm = new RhythmAnalysisResult(
            null,
            0d,
            Array.Empty<double>(),
            Array.Empty<double>(),
            null);
        var tiers = SpectralArtifactEncoder.EncodeTiers(spectral.Complete(), emptyRhythm);
        var bytes = tiers[100];

        Assert.Equal("SLWS", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4, 2)));
        Assert.Equal((ushort)52, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6, 2)));
        Assert.Equal((uint)sampleRate, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4)));
        Assert.Equal(100u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12, 4)));
        Assert.Equal((byte)1, bytes[20]);
        Assert.Equal((byte)5, bytes[23]);
        Assert.Equal(80u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24, 4)));
        Assert.Equal(250u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(28, 4)));
        Assert.Equal(2_000u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(32, 4)));
        Assert.Equal(8_000u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(36, 4)));

        var payload = bytes.AsSpan(52);
        byte maxSub = 0, maxBass = 0, maxMid = 0, maxPresence = 0, maxAir = 0;
        for (var offset = 0; offset + 5 < payload.Length; offset += 6)
        {
            maxSub = Math.Max(maxSub, payload[offset + 1]);
            maxBass = Math.Max(maxBass, payload[offset + 2]);
            maxMid = Math.Max(maxMid, payload[offset + 3]);
            maxPresence = Math.Max(maxPresence, payload[offset + 4]);
            maxAir = Math.Max(maxAir, payload[offset + 5]);
        }

        Assert.True(maxMid > maxSub);
        Assert.True(maxMid > maxBass);
        Assert.True(maxMid > maxPresence);
        Assert.True(maxMid > maxAir);
    }

    [Fact]
    public void SpectralArtifact_EmbedsCanonicalRhythmExtension()
    {
        var waveform = new SpectralWaveformData(
            100,
            10,
            1_000,
            Enumerable.Repeat(default(SpectralPeakFrame), 100).ToArray());
        var rhythm = new RhythmAnalysisResult(
            120d,
            0.9d,
            Enumerable.Range(0, 20).Select(index => index * 0.5d).ToArray(),
            Enumerable.Range(0, 5).Select(index => index * 2d).ToArray(),
            4);

        var bytes = SpectralArtifactEncoder.EncodeTiers(
            new SpectralWaveformData(
                100,
                100,
                1_000,
                Enumerable.Repeat(default(SpectralPeakFrame), 1_000).ToArray()),
            rhythm)[10];

        var headerLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6, 2));
        Assert.True(headerLength > 52);
        Assert.Equal("SRHY", Encoding.ASCII.GetString(bytes, 52, 4));
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(56, 2)));
        Assert.Equal(120f, BitConverter.Int32BitsToSingle(
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(64, 4))));
    }

    [Fact]
    public void FfmpegLoudnessParser_MapsLoudnormInputStatistics()
    {
        const string diagnostics = """
            [Parsed_loudnorm_0] {
                "input_i" : "-17.31",
                "input_tp" : "-0.82",
                "input_lra" : "8.40",
                "input_thresh" : "-27.10"
            }
            """;

        var result = FfmpegLoudnessParser.Parse(diagnostics);

        Assert.NotNull(result);
        Assert.Equal(-17.31d, result!.IntegratedLufs, 2);
        Assert.Equal(-0.82d, result.TruePeak, 2);
        Assert.Equal(8.4d, result.DynamicRange, 2);
    }

    [Fact]
    public void FfmpegLoudnessParser_RejectsMissingStatistics()
    {
        Assert.Null(FfmpegLoudnessParser.Parse("ffmpeg completed"));
    }

    [Fact]
    public void Crc32_MatchesIsoHdlcCheckValue()
    {
        Assert.Equal(
            0xCBF4_3926u,
            SpectralArtifactEncoder.Crc32(Encoding.ASCII.GetBytes("123456789")));
    }
}
