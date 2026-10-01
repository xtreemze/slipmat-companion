using System;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.AudioGateway.Services;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

/// <summary>
/// Unit tests for the static pure helpers on <see cref="JellyfinMetadataAdapter"/>.
///
/// Non-static methods (<c>GetTrackRow</c>, <c>GetAlbumCard</c>, <c>GetArtistCards</c>,
/// <c>BuildArtworkSet</c>) require <c>ILibraryManager</c>. Because Moq is not present in
/// this test project, those methods are covered by integration-level tests that run against
/// a real Jellyfin runtime (CI-only). Add Moq to the test .csproj to enable mock-based
/// unit tests for those paths.
/// </summary>
public class JellyfinMetadataAdapterTests
{
    // ── BuildImageUrl ─────────────────────────────────────────────────────────

    [Fact]
    public void BuildImageUrl_WithTag_ContainsTagParam()
    {
        var id   = Guid.NewGuid();
        var url  = JellyfinMetadataAdapter.BuildImageUrl(id, ImageType.Primary, 800, "abc123");
        Assert.Contains("tag=abc123", url);
    }

    [Fact]
    public void BuildImageUrl_WithoutTag_OmitsTagParam()
    {
        var id   = Guid.NewGuid();
        // Passing null — tag must be absent from the query string entirely.
        var url  = JellyfinMetadataAdapter.BuildImageUrl(id, ImageType.Primary, 800, null);
        Assert.DoesNotContain("tag=", url);
    }

    [Fact]
    public void BuildImageUrl_WithEmptyTag_OmitsTagParam()
    {
        var id   = Guid.NewGuid();
        // Empty string is also treated as "no tag" per the spec.
        var url  = JellyfinMetadataAdapter.BuildImageUrl(id, ImageType.Primary, 800, string.Empty);
        Assert.DoesNotContain("tag=", url);
    }

    [Fact]
    public void BuildImageUrl_StartsWithExpectedPath()
    {
        var id   = Guid.Parse("11223344-5566-7788-99aa-bbccddeeff00");
        var url  = JellyfinMetadataAdapter.BuildImageUrl(id, ImageType.Primary, 800, "tag1");
        Assert.StartsWith($"/Items/{id}/Images/Primary", url);
    }

    [Fact]
    public void BuildImageUrl_ContainsFillWidthParam()
    {
        var id   = Guid.NewGuid();
        var url  = JellyfinMetadataAdapter.BuildImageUrl(id, ImageType.Primary, 800, "t");
        Assert.Contains("fillWidth=800", url);
    }

    [Fact]
    public void BuildImageUrl_BackdropType_ContainsBackdropInPath()
    {
        var id  = Guid.NewGuid();
        var url = JellyfinMetadataAdapter.BuildImageUrl(id, ImageType.Backdrop, 1280, "bd1");
        Assert.Contains("/Images/Backdrop", url);
        Assert.Contains("fillWidth=1280", url);
    }

    // ── BuildCacheToken ───────────────────────────────────────────────────────

    [Fact]
    public void BuildCacheToken_SameInputs_SameOutput()
    {
        var itemId = "11223344-5566-7788-99aa-bbccddeeff00";
        var ticks  = 638_000_000_000L;

        var t1 = JellyfinMetadataAdapter.BuildCacheToken(itemId, ticks);
        var t2 = JellyfinMetadataAdapter.BuildCacheToken(itemId, ticks);

        Assert.Equal(t1, t2);
    }

    [Fact]
    public void BuildCacheToken_DecodesToValidJsonWithEAndTKeys()
    {
        var itemId = "aabbccdd-1234-5678-0000-ffeeddccbbaa";
        var ticks  = 999_000_000L;

        var token = JellyfinMetadataAdapter.BuildCacheToken(itemId, ticks);

        // Must be valid Base64.
        var bytes = Convert.FromBase64String(token);
        var json  = Encoding.UTF8.GetString(bytes);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Must contain "e" (entity / itemId) key.
        Assert.True(root.TryGetProperty("e", out var eProp), "JSON must have 'e' key");
        Assert.Equal(itemId, eProp.GetString());

        // Must contain "t" (ticks) key.
        Assert.True(root.TryGetProperty("t", out var tProp), "JSON must have 't' key");
        Assert.Equal(ticks, tProp.GetInt64());
    }

    [Fact]
    public void BuildCacheToken_DifferentTicks_DifferentOutput()
    {
        var itemId = "aabbccdd-1234-5678-0000-ffeeddccbbaa";

        var t1 = JellyfinMetadataAdapter.BuildCacheToken(itemId, 100L);
        var t2 = JellyfinMetadataAdapter.BuildCacheToken(itemId, 200L);

        Assert.NotEqual(t1, t2);
    }

    // ── TicksToMs ─────────────────────────────────────────────────────────────

    [Fact]
    public void TicksToMs_TenMillionTicks_IsOneThousandMs()
    {
        // 10,000,000 ticks × (100 ns/tick) = 1,000,000,000 ns = 1,000 ms
        Assert.Equal(1_000L, JellyfinMetadataAdapter.TicksToMs(10_000_000L));
    }

    [Fact]
    public void TicksToMs_Zero_IsZero()
    {
        Assert.Equal(0L, JellyfinMetadataAdapter.TicksToMs(0L));
    }

    [Fact]
    public void TicksToMs_TruncatesSubMillisecondRemainder()
    {
        // 10,001 ticks → 1 ms (integer division truncates)
        Assert.Equal(1L, JellyfinMetadataAdapter.TicksToMs(10_001L));
    }
}
