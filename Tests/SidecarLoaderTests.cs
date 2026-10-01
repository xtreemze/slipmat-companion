using System;
using System.IO;
using System.Text.Json;
using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public class SidecarLoaderTests
{
    private const string StoreKey =
        "asv1-45babe3bb1fb2875c1a1185d168399d182a45f0bc2bfef66be054439121570e4";

    private static string ExamplePath(string filename)
    {
        return TestRepositoryPaths.Example(filename);
    }

    [Fact]
    public void LoadSidecar_ValidJson_DeserializesHostNeutralFields()
    {
        var loader = new SidecarLoader();
        var path = ExamplePath("analysis_sidecar_v2.example.json");

        var sidecar = loader.LoadSidecar(path);

        Assert.NotNull(sidecar);
        Assert.Equal(AnalysisSidecar.CurrentSchemaVersion, sidecar!.SchemaVersion);
        Assert.Equal(AnalysisSubjectV1.SubjectVersion, sidecar.SubjectVersion);
        Assert.Equal(StoreKey, sidecar.SubjectStoreKey);
        Assert.Equal(64, sidecar.SourceFingerprint.Length);
        Assert.Equal(562000L, sidecar.DurationMs);
        Assert.Equal(3, sidecar.WaveformRefs.Count);
        Assert.Equal(1, sidecar.WaveformRefs[0].Pps);
        Assert.Equal(10, sidecar.WaveformRefs[1].Pps);
        Assert.Equal(100, sidecar.WaveformRefs[2].Pps);
        Assert.NotNull(sidecar.WaveformRefs[0].Path);
    }

    [Fact]
    public void LoadSidecar_MissingFile_ReturnsNull()
    {
        var loader = new SidecarLoader();
        Assert.Null(loader.LoadSidecar("/nonexistent/subject.json"));
    }

    [Fact]
    public void LoadSidecar_CorruptJson_ReturnsNull()
    {
        var loader = new SidecarLoader();
        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmpFile, "{{{ not json }}}");
            Assert.Null(loader.LoadSidecar(tmpFile));
        }
        finally { File.Delete(tmpFile); }
    }

    [Fact]
    public void LoadSidecarFromStore_RejectsRawProviderId()
    {
        var loader = new SidecarLoader();
        Assert.Throws<ArgumentException>(() =>
            loader.LoadSidecarFromStore("/store", "abc123"));
    }

    [Fact]
    public void LoadSidecarFromStore_RejectsSubjectMismatchAsAbsent()
    {
        var loader = new SidecarLoader();
        var root = Directory.CreateTempSubdirectory("audio-gateway-sidecar-test");
        try
        {
            var analysisDir = Directory.CreateDirectory(Path.Combine(root.FullName, "analysis"));
            var fixture = File.ReadAllText(ExamplePath("analysis_sidecar_v2.example.json"));
            File.WriteAllText(Path.Combine(analysisDir.FullName, "asv1-0000000000000000000000000000000000000000000000000000000000000000.json"), fixture);

            Assert.Null(loader.LoadSidecarFromStore(root.FullName, "asv1-0000000000000000000000000000000000000000000000000000000000000000"));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void LoadSidecar_RoundTripFromSampleFixture()
    {
        var loader = new SidecarLoader();
        var path = ExamplePath("analysis_sidecar_v2.example.json");
        var sidecar = loader.LoadSidecar(path);
        Assert.NotNull(sidecar);

        var json = JsonSerializer.Serialize(sidecar, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
        });
        var reparsed = loader.LoadSidecarFromJson(json);
        Assert.NotNull(reparsed);
        Assert.Equal(sidecar!.SubjectStoreKey, reparsed!.SubjectStoreKey);
        Assert.Equal(sidecar.SchemaVersion, reparsed.SchemaVersion);
        Assert.Equal(sidecar.DurationMs, reparsed.DurationMs);
        Assert.Equal(sidecar.WaveformRefs.Count, reparsed.WaveformRefs.Count);
    }
}
