using Jellyfin.Plugin.AudioGateway.Services;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public sealed class CloudConflictNamespaceTests
{
    [Fact]
    public void MatchesAsciiCaseDifferencesAtLeafAndAncestorWithoutLosingSpellings()
    {
        var local = new[] { new CloudConflictNamespaceEntry("Älbum", true), new CloudConflictNamespaceEntry("Älbum/track.flac", false) };
        var remote = new[] { new CloudConflictNamespaceEntry("ÄLBUM", true), new CloudConflictNamespaceEntry("ÄLBUM/Track.flac", false) };
        var pairs = CloudConflictNamespace.Match(local, remote, providerCaseInsensitive: true);
        var pair = Assert.Single(pairs);
        Assert.Equal("Älbum/track.flac", pair.LocalPath);
        Assert.Equal("ÄLBUM/Track.flac", pair.RemotePath);
    }

    [Fact]
    public void RequiresProviderFeatureForCaseAlias()
    {
        var local = new[] { new CloudConflictNamespaceEntry("a.flac", false) };
        var remote = new[] { new CloudConflictNamespaceEntry("A.flac", false) };
        Assert.Throws<InvalidOperationException>(() => CloudConflictNamespace.Match(local, remote, false));
    }

    [Theory]
    [InlineData("a", false, "A", false)]
    [InlineData("a", true, "A", true)]
    [InlineData("a", true, "A", false)]
    public void RejectsSameSideAliases(string left, bool leftDir, string right, bool rightDir)
    {
        var entries = new[] { new CloudConflictNamespaceEntry(left, leftDir), new CloudConflictNamespaceEntry(right, rightDir) };
        Assert.Throws<InvalidOperationException>(() => CloudConflictNamespace.Match(entries, [], true));
    }

    [Fact]
    public void RejectsCrossSideFileDirectoryAliasAndUnprovenUnicodeCase()
    {
        Assert.Throws<InvalidOperationException>(() => CloudConflictNamespace.Match(
            [new("dir", true)], [new("DIR", false)], true));
        Assert.Throws<InvalidOperationException>(() => CloudConflictNamespace.Match(
            [new("ä.flac", false)], [new("Ä.flac", false)], true));
    }
}
