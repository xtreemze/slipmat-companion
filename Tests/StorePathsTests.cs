using System;
using System.IO;
using Jellyfin.Plugin.AudioGateway.Services;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public class StorePathsTests
{
    private const string StoreKey =
        "asv1-0000000000000000000000000000000000000000000000000000000000000000";

    [Fact]
    public void ValidateStoreKey_AcceptsAnalysisSubjectKey()
        => StorePaths.ValidateStoreKey(StoreKey);

    [Theory]
    [InlineData("abc123")]
    [InlineData("asv1-")]
    [InlineData("asv1-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("asv1-000000000000000000000000000000000000000000000000000000000000000g")]
    [InlineData("../etc/passwd")]
    [InlineData("")]
    public void ValidateStoreKey_RejectsRawOrMalformedKeys(string key)
        => Assert.Throws<ArgumentException>(() => StorePaths.ValidateStoreKey(key));

    [Theory]
    [InlineData("awf_v1_native_mono_b8")]
    [InlineData("slws_v2_stereo_u8_max_5band")]
    public void ValidatePathSegment_AcceptsArtifactVariants(string value)
        => StorePaths.ValidatePathSegment(value);

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("abc/def")]
    [InlineData("abc\\def")]
    [InlineData("")]
    [InlineData("  ")]
    public void ValidatePathSegment_RejectsTraversalAndEmpty(string value)
        => Assert.Throws<ArgumentException>(() => StorePaths.ValidatePathSegment(value));

    [Fact]
    public void WaveformDatPath_UsesSubjectStoreKey()
    {
        var path = StorePaths.WaveformDatPath("/store", StoreKey, "awf_v1_native_mono_b8", 10);
        Assert.Equal(
            Path.Combine("/store", "waveforms", StoreKey, "awf_v1_native_mono_b8", "pps_10.dat"),
            path);
    }

    [Fact]
    public void SidecarPath_UsesSubjectStoreKey()
    {
        var path = StorePaths.SidecarPath("/store", StoreKey);
        Assert.Equal(Path.Combine("/store", "analysis", $"{StoreKey}.json"), path);
    }

    [Fact]
    public void IsWithinRoot_ReturnsTrueForChildPath()
        => Assert.True(StorePaths.IsWithinRoot("/store", $"/store/analysis/{StoreKey}.json"));

    [Fact]
    public void IsWithinRoot_ReturnsFalseForParentEscape()
        => Assert.False(StorePaths.IsWithinRoot("/store", "/etc/passwd"));
}
