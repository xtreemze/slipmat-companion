using System;
using Jellyfin.Plugin.AudioGateway.Models;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public sealed class AlbumLoudnessSidecarV1Tests
{
    private const string SourceA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string SourceB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly string StoreKey = new AnalysisSubjectV1("jellyfin", "server", "album-1").StoreKey();

    private static AlbumLoudnessSidecarV1 CreateSidecar()
    {
        Assert.True(AlbumLoudnessManifestV1.TryCreate(
            [
                new AlbumLoudnessMemberV1("00000000-0000-0000-0000-000000000001", 1, 1, "stereo", 0, 48_000, 2, 100, 200, SourceA),
                new AlbumLoudnessMemberV1("00000000-0000-0000-0000-000000000002", 1, 2, "stereo", 0, 44_100, 2, 300, 400, SourceB),
            ],
            out var manifest,
            out _));

        return new AlbumLoudnessSidecarV1(
            AlbumLoudnessSidecarV1.CurrentVersion,
            StoreKey,
            AlbumLoudnessManifestV1.ComputeSourceFingerprint(manifest!.Members),
            manifest,
            120_000,
            new LoudnessMeasurementEvidenceV1(
                1,
                "album",
                "server-analysis",
                "gated-integrated-loudness",
                new LoudnessAnalyzerIdentityV1("ffmpeg-loudnorm", "1", "sha256:" + new string('c', 64)),
                -18.5,
                -1.2,
                7.4),
            "2026-11-10T10:00:00Z");
    }

    [Fact]
    public void IsCurrentForAcceptsConsistentAlbumEvidence()
    {
        Assert.True(CreateSidecar().IsCurrentFor(StoreKey));
    }

    [Fact]
    public void IsCurrentForRejectsWrongSubjectOrCorruptSourceIdentity()
    {
        var sidecar = CreateSidecar();

        Assert.False(sidecar.IsCurrentFor(new AnalysisSubjectV1("jellyfin", "server", "album-2").StoreKey()));
        Assert.False((sidecar with { SourceFingerprint = new string('d', 64) }).IsCurrentFor(StoreKey));
        Assert.False((sidecar with { Measurement = sidecar.Measurement with { Scope = "track" } }).IsCurrentFor(StoreKey));
        Assert.False((sidecar with { Measurement = sidecar.Measurement with { IntegratedLufs = double.NaN } }).IsCurrentFor(StoreKey));
    }

    [Fact]
    public void IsCurrentForRejectsManifestTampering()
    {
        var sidecar = CreateSidecar();
        var tamperedMember = sidecar.Manifest.Members[0] with { TrackNumber = 9 };
        var tamperedManifest = sidecar.Manifest with { Members = [tamperedMember, sidecar.Manifest.Members[1]] };

        Assert.False((sidecar with { Manifest = tamperedManifest }).IsCurrentFor(StoreKey));
    }
}
