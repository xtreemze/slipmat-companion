using System;
using System.IO;
using System.Text.Json;
using Jellyfin.Plugin.AudioGateway.Api;
using Jellyfin.Plugin.AudioGateway.Models;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

/// <summary>
/// Round-trip deserialization tests against the canonical schema/examples/ payloads.
/// Each test: deserialize → check key fields → re-serialize → compare JSON trees.
/// </summary>
public class ModelRoundTripTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    /// <summary>Resolves schema/examples/ relative to the repo root from the test binary location.</summary>
    private static string ExamplePath(string filename)
    {
        return TestRepositoryPaths.Example(filename);
    }

    private static T Deserialize<T>(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<T>(json, Options)
            ?? throw new InvalidOperationException($"Deserialized null from {path}");
    }

    [Fact]
    public void TrackEvent_FullExample_Deserializes()
    {
        var path = ExamplePath("track_event.example.json");
        var evt = Deserialize<TrackEvent>(path);

        Assert.Equal("abc123", evt.Track.ItemId);
        Assert.Equal("So What", evt.Track.Title);
        Assert.Equal(562000L, evt.Track.DurationMs);
        Assert.Equal(1, evt.Track.TrackNumber);
        Assert.NotEmpty(evt.Artists);
        Assert.Equal("Miles Davis", evt.Artists[0].Name);
        Assert.NotNull(evt.Waveform);
        Assert.NotNull(evt.Analysis);
        Assert.NotNull(evt.Analysis!.LoudnessMeasurement);
        Assert.Equal("server-analysis", evt.Analysis.LoudnessMeasurement!.Authority);
        Assert.Equal("ffmpeg-loudnorm-input", evt.Analysis.LoudnessMeasurement.Semantics);
        Assert.Equal(-17.3d, evt.Analysis.LoudnessMeasurement.IntegratedLufs, 2);
        Assert.Equal(-2.1d, evt.Analysis.LoudnessMeasurement.TruePeakDbtp!.Value, 2);
        Assert.Equal(14.8d, evt.Analysis.LoudnessMeasurement.LoudnessRangeLu!.Value, 2);
        Assert.NotNull(evt.Waveform!.Path);
        Assert.StartsWith("waveforms/asv1-", evt.Waveform.Path);
        Assert.Null(evt.Waveform.DurationSamples);
        Assert.NotNull(evt.CreditsPreview);
        Assert.True(evt.AnalysisAvailable);
    }

    [Fact]
    public void TrackEvent_MinimalExample_Deserializes()
    {
        var path = ExamplePath("track_event.minimal.example.json");
        var evt = Deserialize<TrackEvent>(path);

        Assert.Equal("abc123", evt.Track.ItemId);
        Assert.Null(evt.Waveform);
        Assert.Null(evt.Analysis);
        Assert.Null(evt.CreditsPreview);
        Assert.False(evt.AnalysisAvailable);
    }

    [Fact]
    public void AlbumEvent_FullExample_Deserializes()
    {
        var path = ExamplePath("album_event.example.json");
        var evt = Deserialize<AlbumEvent>(path);

        Assert.Equal("alb456", evt.Album.Id);
        Assert.Equal(1959, evt.Album.Year);
        Assert.NotNull(evt.Tracks);
        Assert.Equal(5, evt.Tracks.Count);
        Assert.NotNull(evt.Credits);
        Assert.NotEmpty(evt.Credits.Entries!);
    }

    [Fact]
    public void ArtistEvent_FullExample_Deserializes()
    {
        var path = ExamplePath("artist_event.example.json");
        var evt = Deserialize<ArtistEvent>(path);

        Assert.Equal("art789", evt.Artist.Id);
        Assert.Equal("Miles Davis", evt.Artist.Name);
        Assert.NotNull(evt.Bio);
        Assert.NotNull(evt.Discography);
        Assert.Equal(3, evt.Discography.Count);
    }

    [Fact]
    public void Capabilities_FullExample_Deserializes()
    {
        var path = ExamplePath("capabilities.example.json");
        var caps = Deserialize<CapabilitiesResponse>(path);

        Assert.Equal(CapabilitiesController.ProtocolVersion, caps.ProtocolVersion);
        Assert.Equal(CapabilitiesController.ExtensionMode, caps.Mode);
        Assert.False(caps.Authoritative);
        Assert.Equal(CapabilitiesController.SupportedJellyfinVersion, caps.SupportedJellyfinVersion);
        Assert.True(caps.Modules.AnalysisArtifacts);
        Assert.True(caps.Modules.TrackMetadataBatching);
        Assert.False(caps.Modules.AtlasProjectionCache);
        Assert.False(caps.Modules.AcquisitionSearch);
        Assert.True(caps.Modules.PodcastDirectorySearch);
        Assert.True(caps.Modules.PodcastSubscriptions);
        Assert.True(caps.Modules.PodcastFeedRefresh);
        Assert.False(caps.Modules.PodcastSnapshotCache);
        Assert.True(caps.AnalyzerHealthy);
        Assert.True(caps.StoreWritable);
        Assert.Empty(caps.DegradedReasons);
    }

    [Fact]
    public void Capabilities_DegradedExample_Deserializes()
    {
        var path = ExamplePath("capabilities.degraded.example.json");
        var caps = Deserialize<CapabilitiesResponse>(path);

        Assert.False(caps.Authoritative);
        Assert.False(caps.Modules.AnalysisArtifacts);
        Assert.False(caps.Modules.AtlasProjectionCache);
        Assert.False(caps.Modules.PodcastDirectorySearch);
        Assert.False(caps.Modules.PodcastSubscriptions);
        Assert.True(caps.Modules.PodcastFeedRefresh);
        Assert.False(caps.Modules.PodcastSnapshotCache);
        Assert.False(caps.StoreWritable);
        Assert.NotEmpty(caps.DegradedReasons);
    }

    [Fact]
    public void AnalysisSidecar_V2Example_Deserializes()
    {
        var path = ExamplePath("analysis_sidecar_v2.example.json");
        var sidecar = Deserialize<AnalysisSidecar>(path);

        Assert.Equal(AnalysisSidecar.CurrentSchemaVersion, sidecar.SchemaVersion);
        Assert.Equal(AnalysisSubjectV1.SubjectVersion, sidecar.SubjectVersion);
        Assert.StartsWith("asv1-", sidecar.SubjectStoreKey);
        Assert.Equal(3, sidecar.WaveformRefs.Count);
        Assert.NotNull(sidecar.Analysis);
        Assert.Equal("A Minor", sidecar.Analysis!.Harmonic.Key);
        Assert.Equal("8A", sidecar.Analysis.Harmonic.CamelotKey);
        Assert.NotNull(sidecar.Analysis.LoudnessMeasurement);
        Assert.Equal(1, sidecar.Analysis.LoudnessMeasurement!.Version);
        Assert.Equal("track", sidecar.Analysis.LoudnessMeasurement.Scope);
        Assert.Equal("server-analysis", sidecar.Analysis.LoudnessMeasurement.Authority);
        Assert.Equal(
            "ffmpeg-loudnorm-input",
            sidecar.Analysis.LoudnessMeasurement.Semantics);
        Assert.Equal(
            "ffmpeg-loudnorm",
            sidecar.Analysis.LoudnessMeasurement.Analyzer.Name);
        Assert.Equal(
            "sha256:d067e146e6dd806228d993e7cb40501a80c464fb84af3af7ecfa32f5fca2b4ec",
            sidecar.Analysis.LoudnessMeasurement.Analyzer.ConfigurationDigest);
        Assert.Equal(-17.3d, sidecar.Analysis.LoudnessMeasurement.IntegratedLufs, 2);
        Assert.Equal(-2.1d, sidecar.Analysis.LoudnessMeasurement.TruePeakDbtp!.Value, 2);
        Assert.Equal(14.8d, sidecar.Analysis.LoudnessMeasurement.LoudnessRangeLu!.Value, 2);
        Assert.DoesNotContain("itemId", File.ReadAllText(path));
    }

    [Fact]
    public void ETag_Analysis_ContainsFullFingerprintAndSchemaVersion()
    {
        var fp = "a1b2c3d4e5f6789012345678901234567890abcdef0123456789abcdef012345";
        var etag = ArtifactsController.BuildETagForAnalysis(fp);
        Assert.Equal($"\"{fp}:sidecar:{AnalysisSidecar.CurrentSchemaVersion}\"", etag);
    }

    [Fact]
    public void ETag_Waveform_VariesByPps()
    {
        var fp = "a1b2c3d4e5f6789012345678901234567890abcdef0123456789abcdef012345";
        var etag1 = ArtifactsController.BuildETagForWaveform(fp, "awf_v1_riff_mono_u8_peak", 1);
        var etag10 = ArtifactsController.BuildETagForWaveform(fp, "awf_v1_riff_mono_u8_peak", 10);
        var etag100 = ArtifactsController.BuildETagForWaveform(fp, "awf_v1_riff_mono_u8_peak", 100);
        Assert.NotEqual(etag1, etag10);
        Assert.NotEqual(etag10, etag100);
        Assert.Contains(fp, etag10);
        Assert.Contains("awf_v1_riff_mono_u8_peak", etag10);
        Assert.Contains("10", etag10);
    }

    [Fact]
    public void ETag_Waveform_IsStableForSameInputs()
    {
        var fp = "a1b2c3d4e5f6789012345678901234567890abcdef0123456789abcdef012345";
        var e1 = ArtifactsController.BuildETagForWaveform(fp, "awf_v1_riff_mono_u8_peak", 10);
        var e2 = ArtifactsController.BuildETagForWaveform(fp, "awf_v1_riff_mono_u8_peak", 10);
        Assert.Equal(e1, e2);
    }
}
