using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Services;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public sealed class PhysicalMediaMaterialStoreTests : IDisposable
{
    private static readonly string Key = "pmv1-" + new string('a', 64);
    // Real 2x2 non-interlaced RGBA PNG, with valid IHDR/IDAT/IEND CRCs.
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAEUlEQVR4nGP4z8DwH4QZYAwAR8oH+WdZbrcAAAAASUVORK5CYII=");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pm-store-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private PhysicalMediaMaterialStore Store(long budget = PhysicalMediaMaterialStore.DefaultBudgetBytes)
        => new(() => _root, budget);

    [Theory]
    [InlineData("normal-clear-plastic-0.png", true)]
    [InlineData("anisotropy-foil-0.png", true)]
    [InlineData("roughness-grain-12.png", true)]
    [InlineData("normal-../x-0.png", false)]
    [InlineData("albedo-paper-0.png", false)]
    [InlineData("normal-paper-0.jpg", false)]
    public void Asset_names_are_a_closed_vocabulary(string name, bool valid)
        => Assert.Equal(valid, PhysicalMediaMaterialStore.IsValidAssetName(name));

    [Fact]
    public void Keys_must_be_versioned_lowercase_digests()
    {
        Assert.True(PhysicalMediaMaterialStore.IsValidCacheKey(Key));
        Assert.False(PhysicalMediaMaterialStore.IsValidCacheKey("pmv1-" + new string('A', 64)));
        Assert.False(PhysicalMediaMaterialStore.IsValidCacheKey("../pmv1"));
    }

    [Fact]
    public async Task Stores_once_lists_and_finds()
    {
        var store = Store();
        Assert.Equal(
            PhysicalMediaMaterialStore.PutResult.Stored,
            await store.PutAsync(Key, "normal-paper-0.png", new MemoryStream(Png), CancellationToken.None));
        Assert.Equal(
            PhysicalMediaMaterialStore.PutResult.AlreadyStored,
            await store.PutAsync(Key, "normal-paper-0.png", new MemoryStream(Png.Reverse().ToArray()), CancellationToken.None));

        Assert.Equal(new[] { "normal-paper-0.png" }, store.List(Key));
        var path = store.Find(Key, "normal-paper-0.png");
        Assert.NotNull(path);
        Assert.Equal(Png, await File.ReadAllBytesAsync(path!));
        Assert.Null(store.Find(Key, "roughness-paper-0.png"));
    }

    [Fact]
    public async Task Rejects_anything_that_is_not_a_PNG_and_leaves_no_partial_file()
    {
        var store = Store();
        Assert.Equal(
            PhysicalMediaMaterialStore.PutResult.Rejected,
            await store.PutAsync(Key, "normal-paper-0.png", new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }), CancellationToken.None));
        Assert.Empty(store.List(Key));
        Assert.Empty(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Rejects_corrupt_chunks_truncated_png_and_trailing_bytes()
    {
        var store = Store();
        var badCrc = (byte[])Png.Clone();
        badCrc[45] ^= 1; // IDAT payload mutation without a matching CRC.
        var truncated = Png[..^1];
        var trailing = Png.Concat(new byte[] { 0 }).ToArray();
        var fakeSignature = Png[..8].Concat(new byte[] { 1, 2, 3, 4 }).ToArray();

        foreach (var payload in new[] { badCrc, truncated, trailing, fakeSignature })
        {
            Assert.Equal(
                PhysicalMediaMaterialStore.PutResult.Rejected,
                await store.PutAsync(Key, "normal-paper-0.png", new MemoryStream(payload), CancellationToken.None));
            Assert.Empty(store.List(Key));
        }

        Assert.Empty(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Rejects_oversized_decoded_png_even_when_chunk_crcs_are_valid()
    {
        // Valid PNG structure with a 4097-pixel-wide IHDR and matching CRC.
        // The store must not publish a map beyond its decoder-memory budget.
        var oversized = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAEAEAAAABCAYAAACx4wBCAAAAH0lEQVR4nO3BAQ0AAADCoPdPbQ43oAAAAAAAAACAewMgAQABPhr3KAAAAABJRU5ErkJggg==");
        var store = Store();
        Assert.Equal(
            PhysicalMediaMaterialStore.PutResult.Rejected,
            await store.PutAsync(Key, "normal-paper-0.png", new MemoryStream(oversized), CancellationToken.None));
        Assert.Empty(store.List(Key));
    }

    [Fact]
    public async Task Evicts_least_recently_used_keys_over_budget()
    {
        var store = Store(budget: Png.Length * 2);
        var keys = Enumerable.Range(0, 3).Select(index => "pmv1-" + new string((char)('a' + index), 64)).ToArray();
        foreach (var key in keys)
        {
            await store.PutAsync(key, "normal-paper-0.png", new MemoryStream(Png), CancellationToken.None);
            // Distinct LRU clocks on coarse file systems.
            Directory.SetLastWriteTimeUtc(
                Path.Combine(_root, "physical-media-materials", key),
                DateTime.UtcNow.AddMinutes(-10 + Array.IndexOf(keys, key)));
        }

        await store.PutAsync(keys[2], "roughness-paper-0.png", new MemoryStream(Png), CancellationToken.None);

        Assert.Empty(store.List(keys[0]));
        Assert.NotEmpty(store.List(keys[2]));
    }
}
