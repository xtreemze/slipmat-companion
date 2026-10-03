using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public class PortableAnalysisProtocolTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string ExamplePath(string filename)
    {
        return TestRepositoryPaths.Example(filename);
    }

    private static T Read<T>(string filename)
    {
        var json = File.ReadAllText(ExamplePath(filename));
        return JsonSerializer.Deserialize<T>(json, Options)
            ?? throw new InvalidOperationException($"Unable to deserialize {filename}");
    }

    private static void AssertRoundTrip<T>(string filename)
    {
        var json = File.ReadAllText(ExamplePath(filename));
        var value = JsonSerializer.Deserialize<T>(json, Options)
            ?? throw new InvalidOperationException($"Unable to deserialize {filename}");
        var roundTrip = JsonSerializer.Serialize(value, Options);

        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(roundTrip)),
            $"Portable fixture did not round-trip losslessly: {filename}");
    }

    [Fact]
    public void CapabilityFixtures_RoundTripWithoutHostSpecificFields()
    {
        AssertRoundTrip<PortableServerExtensionCapabilitiesV1>(
            "server_extension_capabilities.example.json");
        AssertRoundTrip<PortableServerExtensionCapabilitiesV1>(
            "server_extension_capabilities.degraded.example.json");

        var healthy = Read<PortableServerExtensionCapabilitiesV1>(
            "server_extension_capabilities.example.json");
        var degraded = Read<PortableServerExtensionCapabilitiesV1>(
            "server_extension_capabilities.degraded.example.json");

        Assert.Equal(PortableAnalysisProtocolV1.Version, healthy.ProtocolVersion);
        Assert.Equal("optional-acceleration", healthy.Mode);
        Assert.False(healthy.Authoritative);
        Assert.True(healthy.Modules.AnalysisArtifacts);
        Assert.True(healthy.Modules.AnalysisCoverage);
        Assert.Equal("healthy", healthy.Health.Analyzer.Status);
        Assert.Empty(healthy.DegradedReasons);

        Assert.False(degraded.Modules.AnalysisArtifacts);
        Assert.True(degraded.Modules.AnalysisCoverage);
        Assert.Equal("unavailable", degraded.Health.Analyzer.Status);
        Assert.NotEmpty(degraded.DegradedReasons);

        var raw = File.ReadAllText(
            ExamplePath("server_extension_capabilities.example.json"));
        Assert.DoesNotContain("supportedJellyfinVersion", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("jellyfin", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ArtifactDescriptorFixture_RoundTrips()
    {
        AssertRoundTrip<PortableAnalysisArtifactDescriptorV1>(
            "server_extension_analysis_artifact.example.json");

        var descriptor = Read<PortableAnalysisArtifactDescriptorV1>(
            "server_extension_analysis_artifact.example.json");
        Assert.Equal(PortableAnalysisProtocolV1.Version, descriptor.ProtocolVersion);
        Assert.Equal(1, descriptor.Subject.Version);
        Assert.StartsWith("asv1-", descriptor.Subject.StoreKey);
        Assert.Equal(2, descriptor.Artifacts.Count);
    }

    [Fact]
    public void CoverageFixtures_RoundTrip()
    {
        AssertRoundTrip<PortableAnalysisCoverageRequestV1>(
            "server_extension_analysis_coverage_request.example.json");
        AssertRoundTrip<PortableAnalysisCoverageResponseV1>(
            "server_extension_analysis_coverage_response.example.json");

        var response = Read<PortableAnalysisCoverageResponseV1>(
            "server_extension_analysis_coverage_response.example.json");
        Assert.Equal(1, response.Counts.Ready);
        Assert.Equal(1, response.Counts.Missing);
        Assert.Equal(1, response.Counts.Stale);
        Assert.Equal("source-fingerprint-mismatch", response.Items[2].Reason);
    }

    [Fact]
    public void SidecarProjection_OmitsStorePathsAndProviderIdentity()
    {
        var sidecar = Read<AnalysisSidecar>("analysis_sidecar_v2.example.json");
        var descriptor = PortableAnalysisProjector.FromSidecar(sidecar);
        var json = JsonSerializer.Serialize(descriptor, Options);

        Assert.Equal(sidecar.SubjectStoreKey, descriptor.Subject.StoreKey);
        Assert.DoesNotContain("path", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("url", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("itemId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("jellyfin", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("waveforms/", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SidecarProjection_PreservesSpectralAnalysisVersion()
    {
        var sidecar = Read<AnalysisSidecar>("analysis_sidecar_v2.example.json")
            with
            {
                SpectralRefs =
                [
                    new StoredWaveformRef(
                        Variant: "slws_v2_stereo_u8_max_5band",
                        Pps: 100,
                        Etag: "\"spectral\"")
                ],
                SpectralAnalysisVersion = 2,
            };

        var descriptor = PortableAnalysisProjector.FromSidecar(sidecar);
        var spectral = descriptor.Artifacts.Single(
            artifact => artifact.Kind == "spectral-waveform");

        Assert.Equal(2, spectral.AnalysisVersion);
    }

    [Fact]
    public void SidecarProjection_RejectsPathLikeVariant()
    {
        var sidecar = Read<AnalysisSidecar>("analysis_sidecar_v2.example.json");
        sidecar.WaveformRefs[0] = sidecar.WaveformRefs[0] with
        {
            Variant = "../secret",
        };

        Assert.Throws<ArgumentException>(() =>
            PortableAnalysisProjector.FromSidecar(sidecar));
    }

    [Fact]
    public void SidecarProjection_RejectsEmptyArtifactSet()
    {
        var sidecar = Read<AnalysisSidecar>("analysis_sidecar_v2.example.json")
            with { WaveformRefs = [], SpectralRefs = [] };

        Assert.Throws<ArgumentException>(() =>
            PortableAnalysisProjector.FromSidecar(sidecar));
    }

    [Fact]
    public void SidecarProjection_RejectsInvalidFingerprint()
    {
        var sidecar = Read<AnalysisSidecar>("analysis_sidecar_v2.example.json")
            with { SourceFingerprint = "secret" };

        Assert.Throws<ArgumentException>(() =>
            PortableAnalysisProjector.FromSidecar(sidecar));
    }
}
