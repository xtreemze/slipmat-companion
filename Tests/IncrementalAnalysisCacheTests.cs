using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;
using Jellyfin.Plugin.AudioGateway.Services.Analysis;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public sealed class IncrementalAnalysisCacheTests
{
    private const string StoreKey =
        "asv1-0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task CompleteArtifactsAndUnchangedSourceStamp_SkipFullSourceRead()
    {
        using var fixture = new CacheFixture();
        await fixture.WriteStampAsync();

        Assert.True(fixture.CanSkip());
    }

    [Fact]
    public async Task SourceLengthOrModificationTimeChange_RequiresFingerprint()
    {
        using var fixture = new CacheFixture();
        await fixture.WriteStampAsync();

        File.AppendAllText(fixture.MediaPath, "-changed");
        Assert.False(fixture.CanSkip());

        // Even a same-length source whose modification time changed needs
        // the definitive hash comparison before its artifacts may be reused.
        File.WriteAllText(fixture.MediaPath, "fixture");
        var original = AnalysisSourceStamp.Capture(fixture.MediaPath)!;
        File.SetLastWriteTimeUtc(
            fixture.MediaPath,
            new DateTime(original.LastWriteUtcTicks, DateTimeKind.Utc).AddSeconds(2));
        Assert.False(fixture.CanSkip());
    }

    [Fact]
    public async Task MissingSourceStamp_RequiresFingerprint()
    {
        using var fixture = new CacheFixture();
        await fixture.WriteStampAsync();
        AnalysisSourceStamp.Delete(fixture.Root, StoreKey);

        Assert.False(fixture.CanSkip());
    }

    [Fact]
    public async Task MissingAmplitudeOrSpectralTier_RequiresRegeneration()
    {
        using var fixture = new CacheFixture();
        await fixture.WriteStampAsync();

        var amplitudePath = StorePaths.WaveformDatPath(
            fixture.Root, StoreKey, IntegratedAudioAnalyzer.AmplitudeVariant, 10);
        File.Delete(amplitudePath);
        Assert.False(fixture.CanSkip());

        File.WriteAllBytes(amplitudePath, [1]);
        var spectralPath = StorePaths.WaveformDatPath(
            fixture.Root, StoreKey, SpectralArtifactEncoder.Variant, 100);
        File.Delete(spectralPath);
        Assert.False(fixture.CanSkip());
    }

    [Fact]
    public async Task MissingOrIncompatibleSidecar_RequiresRegeneration()
    {
        using var fixture = new CacheFixture();
        await fixture.WriteStampAsync();

        Assert.False(IntegratedAudioAnalyzer.CanSkipUnmodifiedSource(
            fixture.Root, StoreKey, fixture.MediaPath, null));
        Assert.False(IntegratedAudioAnalyzer.CanSkipUnmodifiedSource(
            fixture.Root,
            StoreKey,
            fixture.MediaPath,
            fixture.Sidecar with { SchemaVersion = "2.0.0" }));
    }

    private sealed class CacheFixture : IDisposable
    {
        public CacheFixture()
        {
            Root = Directory.CreateTempSubdirectory("slipmat-analysis-cache").FullName;
            MediaPath = Path.Combine(Root, "track.flac");
            File.WriteAllText(MediaPath, "fixture");

            Sidecar = new AnalysisSidecar(
                SchemaVersion: AnalysisSidecar.CurrentSchemaVersion,
                SubjectVersion: AnalysisSubjectV1.SubjectVersion,
                SubjectStoreKey: StoreKey,
                SourceFingerprint: new string('a', 64),
                DurationMs: 1_000,
                WaveformRefs: AmplitudeEnvelopeBuilder.Tiers.Select(pps =>
                    new StoredWaveformRef(
                        IntegratedAudioAnalyzer.AmplitudeVariant, pps, "etag")).ToList(),
                ProducerVersion: "test",
                GeneratedAt: "2026-10-15T00:00:00Z",
                Analysis: new AnalysisBlocks(
                    new TimingAnalysis(120, 1),
                    new HarmonicAnalysis("C Major", "8B", 1),
                    new LoudnessAnalysis(-14, -1, 5)),
                SpectralRefs: SpectralArtifactEncoder.Tiers.Select(pps =>
                    new StoredWaveformRef(SpectralArtifactEncoder.Variant, pps, "etag")).ToList(),
                SpectralAnalysisVersion: SpectralArtifactEncoder.FormatVersion);

            foreach (var pps in AmplitudeEnvelopeBuilder.Tiers)
            {
                WriteTier(IntegratedAudioAnalyzer.AmplitudeVariant, pps);
            }

            foreach (var pps in SpectralArtifactEncoder.Tiers)
            {
                WriteTier(SpectralArtifactEncoder.Variant, pps);
            }
        }

        public string Root { get; }

        public string MediaPath { get; }

        public AnalysisSidecar Sidecar { get; }

        public bool CanSkip() => IntegratedAudioAnalyzer.CanSkipUnmodifiedSource(
            Root, StoreKey, MediaPath, Sidecar);

        public async Task WriteStampAsync()
        {
            var stamp = AnalysisSourceStamp.Capture(MediaPath);
            Assert.NotNull(stamp);
            await AnalysisSourceStamp.WriteAsync(Root, StoreKey, stamp!, CancellationToken.None);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private void WriteTier(string variant, int pps)
        {
            var path = StorePaths.WaveformDatPath(Root, StoreKey, variant, pps);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, [1]);
        }
    }
}
