using System.Collections.Generic;
using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public class TrackPlaybackEventComposerTests
{
    private const string StoreKey =
        "asv1-45babe3bb1fb2875c1a1185d168399d182a45f0bc2bfef66be054439121570e4";

    private static AnalysisSidecar MakeSidecar(
        List<StoredWaveformRef>? waveformRefs = null,
        AnalysisBlocks? analysis = null)
        => new(
            SchemaVersion: AnalysisSidecar.CurrentSchemaVersion,
            SubjectVersion: AnalysisSubjectV1.SubjectVersion,
            SubjectStoreKey: StoreKey,
            SourceFingerprint: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            DurationMs: 60000,
            WaveformRefs: waveformRefs ?? [],
            ProducerVersion: "0.1.0",
            GeneratedAt: "2026-04-06T00:00:00Z",
            Analysis: analysis);

    [Fact]
    public void Compose_ProjectsJellyfinItemIdOnlyAtClientBoundary()
    {
        var track = new TrackRow("track-1", "Blue Train", 60000);
        var sidecar = MakeSidecar(
            [
                new StoredWaveformRef(
                    "awf_v1_riff_mono_u8_peak",
                    10,
                    "\"etag-10\"",
                    Path: $"{StoreKey}/pps_10.dat")
            ]);

        var result = TrackPlaybackEventComposer.Compose(
            track,
            "abc123",
            sidecar,
            IncludeFlags.Waveform | IncludeFlags.Sidecar,
            10);

        Assert.NotNull(result.Waveform);
        Assert.Equal("abc123", result.Waveform!.ItemId);
        Assert.Equal(
            "/Plugins/AudioGateway/artifacts/waveform/abc123?variant=awf_v1_riff_mono_u8_peak&pps=10",
            result.Waveform.Url);
        Assert.NotNull(result.Sidecar);
        Assert.Equal("abc123", result.Sidecar!.ItemId);
        Assert.DoesNotContain("abc123", sidecar.SubjectStoreKey);
        Assert.True(result.Availability.HasWaveform);
        Assert.True(result.Availability.HasSidecar);
        Assert.False(result.Availability.HasDerivedAnalysis);
    }

    [Fact]
    public void Compose_OmitsSidecarAndWaveform_WhenNotRequested()
    {
        var track = new TrackRow("track-1", "Blue Train", 60000);
        var sidecar = MakeSidecar();

        var result = TrackPlaybackEventComposer.Compose(
            track,
            "abc123",
            sidecar,
            IncludeFlags.None,
            10);

        Assert.Null(result.Waveform);
        Assert.Null(result.Sidecar);
        Assert.True(result.Availability.HasSidecar);
        Assert.False(result.Availability.HasWaveform);
    }

    [Fact]
    public void Compose_IncludesAnalysisBlocks_WhenRequestedAndAvailable()
    {
        var track = new TrackRow("track-1", "Blue Train", 60000);
        var analysis = new AnalysisBlocks(
            Timing: new TimingAnalysis(120.0, 0.95),
            Harmonic: new HarmonicAnalysis("C minor", "5A", 0.9),
            Loudness: new LoudnessAnalysis(-14.0, -1.0, 8.0));
        var sidecar = MakeSidecar(analysis: analysis);

        var result = TrackPlaybackEventComposer.Compose(
            track,
            "abc123",
            sidecar,
            IncludeFlags.Analysis,
            10);

        Assert.Same(analysis, result.Analysis);
        Assert.True(result.Availability.HasDerivedAnalysis);
    }
}
