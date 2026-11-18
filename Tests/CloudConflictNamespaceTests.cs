using System.Text;
using Jellyfin.Plugin.AudioGateway.Services;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public sealed class CloudConflictNamespaceTests
{
    [Fact]
    public void MatchesThePinnedRcloneLowercaseFingerprint()
        => CloudConflictNamespace.RequireVerifiedRcloneCaseMapping();

    [Fact]
    public void DistributesTheGoAndUnicodeNoticesInThePluginAssembly()
    {
        using var resource = typeof(CloudConflictNamespace).Assembly
            .GetManifestResourceStream("Jellyfin.Plugin.AudioGateway.ThirdPartyNotices.txt");
        Assert.NotNull(resource);
        using var reader = new StreamReader(resource);
        var notice = reader.ReadToEnd();
        Assert.Contains("Copyright 2009 The Go Authors.", notice);
        Assert.Contains("Copyright © 1991-2022 Unicode, Inc.", notice);
    }

    [Theory]
    [InlineData(0x0041, 0x0061)] // ASCII
    [InlineData(0x0100, 0x0101)] // Alternating uppercase and lowercase
    [InlineData(0x0101, 0x0101)] // Gap in an alternating range
    [InlineData(0x0130, 0x0069)] // Go simple lowercase for dotted I
    [InlineData(0x10400, 0x10428)] // Supplementary-plane Deseret
    [InlineData(0x10D50, 0x10D50)] // Unicode 16.0 mapping absent from pinned Go
    [InlineData(0x16EA0, 0x16EA0)] // Unicode 17.0 mapping absent from pinned Go
    public void UsesGoSimpleLowercaseRegardlessOfHostUnicodeVersion(int input, int expected)
        => Assert.Equal(expected, CloudConflictNamespace.RcloneLower(new Rune(input)).Value);

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
    [InlineData("i.nfo", false, "İ.nfo", false)]
    [InlineData("i", true, "İ", true)]
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
        Assert.Throws<InvalidOperationException>(() => CloudConflictNamespace.Match(
            [new("i.nfo", false)], [new("İ.nfo", false)], true));
    }

    [Fact]
    public void MatchesExactNonAsciiCharactersEvenWhenRcloneLowersThemDifferently()
    {
        var pair = Assert.Single(CloudConflictNamespace.Match(
            [new("İ.NFO", false)], [new("İ.nfo", false)], true));
        Assert.Equal("İ.NFO", pair.LocalPath);
        Assert.Equal("İ.nfo", pair.RemotePath);
    }
}
