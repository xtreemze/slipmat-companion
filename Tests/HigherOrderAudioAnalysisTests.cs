using System;
using System.Buffers.Binary;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.AudioGateway.Services;
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
    public void RhythmGridAnalyzer_RestrictsGridToCanonicalAudibleBoundaries()
    {
        const int fps = 20;
        var frames = Enumerable.Repeat(default(RhythmEnergyFrame), 40 * fps).ToArray();
        for (var frame = 4; frame < frames.Length; frame += 10)
        {
            frames[frame] = new RhythmEnergyFrame(0.8f, 0.7f, 0.5f, 0.4f, 0.3f);
        }

        var result = RhythmGridAnalyzer.Analyze(frames, fps, 40d, 5d, 35d);

        Assert.NotNull(result.Bpm);
        Assert.All(result.Beats, value => Assert.InRange(value, 5d, 35d));
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
    public void HarmonicAccumulator_UsesRustStrictFrameBoundary()
    {
        const int sampleRate = 48_000;
        var exactFrame = new HarmonicAccumulator(sampleRate);
        for (var index = 0; index < HarmonicAccumulator.FrameSize; index++)
        {
            exactFrame.Push((float)Math.Sin(
                2d * Math.PI * 261.625565d * index / sampleRate));
        }

        Assert.Equal("Unknown", exactFrame.Complete().Key);

        var withLookahead = new HarmonicAccumulator(sampleRate);
        for (var index = 0; index <= HarmonicAccumulator.FrameSize; index++)
        {
            withLookahead.Push((float)Math.Sin(
                2d * Math.PI * 261.625565d * index / sampleRate));
        }

        Assert.NotEqual("Unknown", withLookahead.Complete().Key);
    }

    [Fact]
    public void HarmonicProjection_UsesFirstChannelLikeLocalAnalyzer()
    {
        Assert.Equal(
            0.75f,
            HigherOrderAudioAnalysisBuilder.HarmonicProjection([0.75f, -0.75f]));
        Assert.Equal(
            0f,
            HigherOrderAudioAnalysisBuilder.HarmonicProjection([float.NaN, 0.5f]));
    }

    [Fact]
    public void SpectralArtifact_PreservesSourceRateAndStereoLanes()
    {
        const int sampleRate = 44_100;
        var spectral = new SpectralAccumulator(sampleRate, 2);
        for (var index = 0; index < sampleRate * 2; index++)
        {
            var left = (float)(0.7d * Math.Sin(
                2d * Math.PI * 1_000d * index / sampleRate));
            var right = (float)(0.7d * Math.Sin(
                2d * Math.PI * 60d * index / sampleRate));
            spectral.PushFrame([left, right]);
        }

        var emptyRhythm = EmptyRhythm();
        var bytes = SpectralArtifactEncoder.EncodeTiers(
            spectral.Complete(),
            null,
            emptyRhythm)[100];

        Assert.Equal("SLWS", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4, 2)));
        Assert.Equal((ushort)52, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6, 2)));
        Assert.Equal((uint)sampleRate, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4)));
        Assert.Equal(100u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12, 4)));
        Assert.Equal((byte)2, bytes[20]);
        Assert.Equal((byte)5, bytes[23]);

        var payload = bytes.AsSpan(52);
        byte leftMid = 0;
        byte leftSub = 0;
        byte rightSub = 0;
        byte rightMid = 0;
        for (var offset = 0; offset + 11 < payload.Length; offset += 12)
        {
            leftSub = Math.Max(leftSub, payload[offset + 1]);
            leftMid = Math.Max(leftMid, payload[offset + 3]);
            rightSub = Math.Max(rightSub, payload[offset + 7]);
            rightMid = Math.Max(rightMid, payload[offset + 9]);
        }

        Assert.True(leftMid > leftSub);
        Assert.True(rightSub > rightMid);
    }

    [Fact]
    public void BoundaryClassifier_DetectsCanonicalAuthoredFade()
    {
        const double duration = 240d;
        var values = Enumerable.Repeat(0.6f, BoundaryAccumulator.EnvelopeBins).ToArray();
        var fadeBins = (int)Math.Round(
            20d / duration * BoundaryAccumulator.EnvelopeBins,
            MidpointRounding.AwayFromZero);
        var start = values.Length - fadeBins;
        for (var offset = 0; offset < fadeBins; offset++)
        {
            var fraction = offset / (float)(fadeBins - 1);
            values[start + offset] = 0.6f * (1f - fraction) + 0.04f * fraction;
        }

        var result = BoundaryClassifier.Derive(
            duration,
            values,
            [values.ToArray(), values.ToArray(), values.ToArray(), values.ToArray()]);

        Assert.NotNull(result);
        Assert.Equal(OutroBoundaryKind.FadeOut, result!.Outro.Kind);
        Assert.True(result.Outro.Confidence >= 0.9f);
        Assert.InRange(result.Outro.StartSeconds, 218d, 222d);
    }

    [Fact]
    public void BoundaryClassifier_DoesNotMislabelAbruptLevelStepAsFade()
    {
        var values = Enumerable.Repeat(0.6f, BoundaryAccumulator.EnvelopeBins).ToArray();
        Array.Fill(values, 0.06f, values.Length - 150, 150);

        var result = BoundaryClassifier.Derive(
            240d,
            values,
            [values.ToArray(), values.ToArray(), values.ToArray(), values.ToArray()]);

        Assert.NotNull(result);
        Assert.NotEqual(OutroBoundaryKind.FadeOut, result!.Outro.Kind);
    }

    [Fact]
    public void BoundaryClassifier_FullLevelMediaEndIsHardEnd()
    {
        var values = Enumerable.Repeat(0.5f, BoundaryAccumulator.EnvelopeBins).ToArray();

        var result = BoundaryClassifier.Derive(240d, values);

        Assert.NotNull(result);
        Assert.Equal(OutroBoundaryKind.HardEnd, result!.Outro.Kind);
    }

    [Fact]
    public void BoundaryClassifier_LeadingAndTrailingSilenceDefineAudibleRange()
    {
        var values = Enumerable.Repeat(0.001f, BoundaryAccumulator.EnvelopeBins).ToArray();
        Array.Fill(values, 0.5f, 75, values.Length - 150);

        var result = BoundaryClassifier.Derive(240d, values);

        Assert.NotNull(result);
        Assert.True(result!.AudibleStartSeconds > 9d);
        Assert.True(result.AudibleEndSeconds < 231d);
        Assert.Equal(IntroBoundaryKind.GradualEntry, result.Intro.Kind);
        Assert.Equal(OutroBoundaryKind.HardEnd, result.Outro.Kind);
    }

    [Fact]
    public void SlwsParityFixture_MatchesCanonicalRustSbndSrhyContainer()
    {
        var frames = Enumerable.Repeat(
            new SpectralPeakFrame(default, default),
            1_000).ToArray();
        var detailed = new SpectralWaveformData(
            SampleRate: 100,
            FramesPerSecond: 100,
            ChannelCount: 1,
            SourceFrameCount: 1_000,
            Frames: frames);
        var boundaries = new TrackBoundaryAnalysis(
            AudibleStartSeconds: 0.5d,
            AudibleEndSeconds: 9.5d,
            Intro: new IntroBoundary(
                IntroBoundaryKind.FadeIn,
                0.5d,
                2d,
                0.82f),
            Outro: new OutroBoundary(
                OutroBoundaryKind.FadeOut,
                7d,
                9.5d,
                0.91f),
            QuickFade: null,
            NoiseFloorRms: 0.01f);
        var rhythm = new RhythmAnalysisResult(
            120d,
            0.9d,
            Enumerable.Range(0, 20).Select(index => index * 0.5d).ToArray(),
            Enumerable.Range(0, 5).Select(index => index * 2d).ToArray(),
            4);

        var bytes = SpectralArtifactEncoder.EncodeTiers(
            detailed,
            boundaries,
            rhythm)[10];

        Assert.Equal(884, bytes.Length);
        Assert.Equal((ushort)284, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6, 2)));
        Assert.Equal("SBND", Encoding.ASCII.GetString(bytes, 52, 4));
        Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(56, 2)));
        Assert.Equal((ushort)6, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(62, 2)));
        Assert.Equal("SRHY", Encoding.ASCII.GetString(bytes, 156, 4));
        Assert.Equal(0xAD98_B5E9u, SpectralArtifactEncoder.Crc32(bytes));
    }

    [Fact]
    public void ExactSourceProjections_AreExplicitAndBounded()
    {
        Assert.Equal(0.5f, HigherOrderAudioAnalysisBuilder.HarmonicProjection([0.5f, 0f]));
        Assert.Equal(-0.8f, IntegratedAudioAnalyzer.AmplitudeProjection([0.2f, -0.8f]));
        Assert.Equal(0.5f, IntegratedAudioAnalyzer.AmplitudeProjection([0.5f, float.NaN]));

        var mono = new IntegratedAudioAnalyzer.SourceAudioFormat(2, 44_100, 1, 1);
        var stereo = new IntegratedAudioAnalyzer.SourceAudioFormat(3, 48_000, 2, 2);
        var surround = new IntegratedAudioAnalyzer.SourceAudioFormat(5, 96_000, 6, 2);

        Assert.Equal(
            "aformat=sample_fmts=flt:channel_layouts=mono",
            IntegratedAudioAnalyzer.BuildAnalysisProjection(mono));
        Assert.Equal(
            "aformat=sample_fmts=flt:channel_layouts=stereo",
            IntegratedAudioAnalyzer.BuildAnalysisProjection(stereo));
        Assert.StartsWith(
            "pan=stereo|c0=c0|c1=c1",
            IntegratedAudioAnalyzer.BuildAnalysisProjection(surround));
        Assert.StartsWith(
            "[0:5]asplit=2[loud][analysis];",
            IntegratedAudioAnalyzer.BuildFilterGraph(
                surround,
                IntegratedAudioAnalyzer.BuildAnalysisProjection(surround)));
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

    private static RhythmAnalysisResult EmptyRhythm()
        => new(
            null,
            0d,
            Array.Empty<double>(),
            Array.Empty<double>(),
            null);
}
