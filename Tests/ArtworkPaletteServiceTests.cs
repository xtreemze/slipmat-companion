using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AudioGateway.Services;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public class ArtworkPaletteServiceTests
{
    [Fact]
    public void ExtractSwatches_PrioritizesPopulationButKeepsHueDiversity()
    {
        var pixels = new List<byte>();
        Add(pixels, 220, 45, 35, 255, 40);
        Add(pixels, 35, 75, 220, 255, 20);
        Add(pixels, 35, 190, 90, 255, 10);

        var swatches = ArtworkPaletteService.ExtractSwatches(pixels.ToArray());

        Assert.True(swatches.Count >= 3);
        Assert.True(swatches[0].Red > swatches[0].Blue);
        Assert.Contains(swatches, swatch => swatch.Blue > swatch.Red);
        Assert.Contains(swatches, swatch => swatch.Green > swatch.Red && swatch.Green > swatch.Blue);
        Assert.InRange(swatches.Sum(swatch => swatch.Weight), 0.99d, 1.01d);
    }

    [Fact]
    public void ExtractSwatches_IgnoresTransparentNearBlackNearWhiteAndAchromaticSamples()
    {
        var pixels = new List<byte>();
        Add(pixels, 200, 20, 20, 100, 10);
        Add(pixels, 3, 2, 4, 255, 10);
        Add(pixels, 250, 249, 250, 255, 10);
        Add(pixels, 120, 121, 122, 255, 10);

        Assert.Empty(ArtworkPaletteService.ExtractSwatches(pixels.ToArray()));
    }

    [Fact]
    public void BuildRevision_IsOpaqueDeterministicAndChangesWithArtworkEvidence()
    {
        var id = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var modified = new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc);

        var first = ArtworkPaletteService.BuildRevision(id, modified, 4096);
        var second = ArtworkPaletteService.BuildRevision(id, modified, 4096);
        var changed = ArtworkPaletteService.BuildRevision(id, modified.AddTicks(1), 4096);

        Assert.Equal(first, second);
        Assert.NotEqual(first, changed);
        Assert.Equal(64, first.Length);
        Assert.Matches("^[0-9a-f]{64}$", first);
    }

    private static void Add(
        ICollection<byte> target,
        byte red,
        byte green,
        byte blue,
        byte alpha,
        int count)
    {
        for (var index = 0; index < count; index++)
        {
            target.Add(red);
            target.Add(green);
            target.Add(blue);
            target.Add(alpha);
        }
    }
}
