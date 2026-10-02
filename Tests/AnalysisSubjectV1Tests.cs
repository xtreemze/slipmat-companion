using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Jellyfin.Plugin.AudioGateway.Models;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public class AnalysisSubjectV1Tests
{
    private sealed record SubjectVector(
        string Name,
        AnalysisSubjectV1 Subject,
        string StoreKey);

    private static string ExamplePath(string filename)
    {
        return TestRepositoryPaths.Example(filename);
    }

    [Fact]
    public void StoreKey_MatchesSharedRustCompatibilityVectors()
    {
        var json = File.ReadAllText(ExamplePath("analysis_subject_v1_vectors.json"));
        var vectors = JsonSerializer.Deserialize<List<SubjectVector>>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(vectors);
        Assert.Equal(3, vectors!.Count);
        foreach (var vector in vectors)
        {
            Assert.Equal(vector.StoreKey, vector.Subject.StoreKey());
        }
    }

    [Fact]
    public void StoreKey_SeparatesProviderInstanceAndRepresentation()
    {
        var baseline = new AnalysisSubjectV1("jellyfin", "server-a", "track-1");
        var otherServer = baseline with { ProviderInstanceId = "server-b" };
        var representation = baseline with { RepresentationId = "media-source-a" };

        Assert.NotEqual(baseline.StoreKey(), otherServer.StoreKey());
        Assert.NotEqual(baseline.StoreKey(), representation.StoreKey());
    }

    [Fact]
    public void StoreKey_DoesNotLeakRawSubjectMaterial()
    {
        var subject = new AnalysisSubjectV1(
            "local",
            "bibliothèque",
            "音楽/../track.flac?token=secret",
            "versión-α");

        var key = subject.StoreKey();

        Assert.StartsWith("asv1-", key);
        Assert.DoesNotContain("track", key);
        Assert.DoesNotContain("secret", key);
        Assert.DoesNotContain("/", key);
        Assert.DoesNotContain("..", key);
    }

    [Theory]
    [InlineData("asv1-45babe3bb1fb2875c1a1185d168399d182a45f0bc2bfef66be054439121570e4", true)]
    [InlineData("asv1-45BABE3BB1FB2875C1A1185D168399D182A45F0BC2BFEF66BE054439121570E4", false)]
    [InlineData("asv1-45babe", false)]
    [InlineData("abc123", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void StoreKey_ValidationMatchesCanonicalShape(string? value, bool expected)
    {
        Assert.Equal(expected, AnalysisSubjectV1.IsStoreKey(value));
    }

    [Theory]
    [InlineData("", "provider", "resource", null)]
    [InlineData("host", "", "resource", null)]
    [InlineData("host", "provider", "", null)]
    [InlineData("host", "provider", "resource", "")]
    public void StoreKey_RejectsEmptyIdentityFields(
        string hostKind,
        string providerInstanceId,
        string resourceId,
        string? representationId)
    {
        var subject = new AnalysisSubjectV1(
            hostKind,
            providerInstanceId,
            resourceId,
            representationId);

        Assert.Throws<ArgumentException>(() => subject.StoreKey());
    }
}
