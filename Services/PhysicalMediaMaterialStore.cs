using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>
/// Content-addressed cache of Package Inspector surface maps (normal,
/// roughness, anisotropy) that clients generate on their GPU. The first client
/// to build a map set uploads it; every later open, on any device, loads the
/// finished maps and skips the generator passes and their shader compiles.
///
/// The companion owns no wear or condition semantics: a key is an opaque
/// client fingerprint of identity, condition recipe and quality tier
/// (<c>pmv1-</c> + SHA-256 hex). Entries are immutable (first write wins), PNG
/// only, size bounded, and evicted least recently used.
/// </summary>
public sealed partial class PhysicalMediaMaterialStore
{
    /// <summary>Largest accepted map (a 2048² RGBA PNG is far below this).</summary>
    public const long MaxAssetBytes = 16L * 1024 * 1024;

    /// <summary>Most assets one key may hold (eight surfaces × three maps, with room).</summary>
    public const int MaxAssetsPerKey = 32;

    /// <summary>Maximum decoded dimension accepted for any client material map.</summary>
    public const int MaxImageEdge = 4096;

    /// <summary>Total cache budget; the least recently used keys go first.</summary>
    public const long DefaultBudgetBytes = 1024L * 1024 * 1024;

    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private static readonly uint[] PngCrcTable = BuildPngCrcTable();

    private readonly Func<string> _storeRoot;
    private readonly long _budgetBytes;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public PhysicalMediaMaterialStore()
        : this(
            () => Configuration.RuntimeSettings.ResolveStoreRoot(
                Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration()),
            DefaultBudgetBytes)
    {
    }

    internal PhysicalMediaMaterialStore(Func<string> storeRoot, long budgetBytes)
    {
        _storeRoot = storeRoot;
        _budgetBytes = budgetBytes;
    }

    [GeneratedRegex("^pmv1-[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex CacheKeyPattern();

    [GeneratedRegex("^(normal|roughness|anisotropy)-[a-z][a-z-]{0,31}-[0-9]{1,2}\\.png$", RegexOptions.CultureInvariant)]
    private static partial Regex AssetNamePattern();

    public static bool IsValidCacheKey(string? key) => key is not null && CacheKeyPattern().IsMatch(key);

    public static bool IsValidAssetName(string? name) => name is not null && AssetNamePattern().IsMatch(name);

    /// <summary>The assets stored under a key, or empty.</summary>
    public IReadOnlyList<string> List(string cacheKey)
    {
        var directory = KeyDirectory(cacheKey);
        if (!Directory.Exists(directory))
        {
            return Array.Empty<string>();
        }

        Touch(directory);
        return Directory.EnumerateFiles(directory, "*.png")
            .Select(Path.GetFileName)
            .Where(IsValidAssetName)
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>The stored asset's path, or null when it is not cached.</summary>
    public string? Find(string cacheKey, string assetName)
    {
        var path = AssetPath(cacheKey, assetName);
        if (!File.Exists(path))
        {
            return null;
        }

        Touch(Path.GetDirectoryName(path)!);
        return path;
    }

    public enum PutResult
    {
        Stored,
        AlreadyStored,
        Rejected,
    }

    /// <summary>
    /// Stores an asset unless one is already there. The body must be a PNG no
    /// larger than <see cref="MaxAssetBytes"/>; the write is atomic.
    /// </summary>
    public async Task<PutResult> PutAsync(
        string cacheKey,
        string assetName,
        Stream body,
        CancellationToken cancellationToken)
    {
        var path = AssetPath(cacheKey, assetName);
        if (File.Exists(path))
        {
            return PutResult.AlreadyStored;
        }

        var directory = Path.GetDirectoryName(path)!;
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(path))
            {
                return PutResult.AlreadyStored;
            }

            if (Directory.Exists(directory)
                && Directory.EnumerateFiles(directory, "*.png").Count() >= MaxAssetsPerKey)
            {
                return PutResult.Rejected;
            }

            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
            try
            {
                long written;
                var header = new byte[PngSignature.Length];
                await using (var file = File.Create(temporary))
                {
                    written = await CopyBoundedAsync(body, file, header, cancellationToken).ConfigureAwait(false);
                }

                if (written < 0 || !header.AsSpan().SequenceEqual(PngSignature)
                    || !IsStructurallyValidPng(File.ReadAllBytes(temporary)))
                {
                    return PutResult.Rejected;
                }

                File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }

            Touch(directory);
            Evict(protectedKey: cacheKey);
            return PutResult.Stored;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Validate the chunk structure, CRCs and decoded image bounds before
    /// making an upload immutable. This is not an image decoder: the client
    /// must still treat all fetched maps as untrusted input and fall back
    /// to local generation if decoding fails.
    /// </summary>
    private static bool IsStructurallyValidPng(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8 + 25 + 12 + 12 || !bytes[..8].SequenceEqual(PngSignature))
        {
            return false;
        }

        var offset = PngSignature.Length;
        var hasHeader = false;
        var hasData = false;
        var dataEnded = false;

        while (offset <= bytes.Length - 12)
        {
            var chunkLength = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
            if (chunkLength > (uint)(bytes.Length - offset - 12))
            {
                return false;
            }

            var size = (int)chunkLength;
            var type = bytes.Slice(offset + 4, 4);
            var data = bytes.Slice(offset + 8, size);
            var storedCrc = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset + 8 + size, 4));
            var actualCrc = ComputePngCrc(bytes.Slice(offset + 4, size + 4));
            if (storedCrc != actualCrc)
            {
                return false;
            }

            if (!hasHeader)
            {
                if (!type.SequenceEqual("IHDR"u8) || size != 13)
                {
                    return false;
                }

                var width = BinaryPrimitives.ReadUInt32BigEndian(data[..4]);
                var height = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4));
                // Canvas material maps are non-interlaced 8-bit RGB/RGBA.
                // Bounds also limit the allocation made by client decoders.
                if (width is < 1 or > MaxImageEdge || height is < 1 or > MaxImageEdge
                    || data[8] != 8 || (data[9] != 2 && data[9] != 6)
                    || data[10] != 0 || data[11] != 0 || data[12] != 0)
                {
                    return false;
                }

                hasHeader = true;
            }
            else if (type.SequenceEqual("IHDR"u8))
            {
                return false;
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                if (dataEnded || size == 0)
                {
                    return false;
                }

                hasData = true;
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                return hasData && size == 0 && offset + 12 == bytes.Length;
            }
            else
            {
                // Unknown critical PNG chunks cannot be safely interpreted.
                if ((type[0] & 0x20) == 0 && !type.SequenceEqual("PLTE"u8))
                {
                    return false;
                }

                // PLTE belongs before IDAT, and IDAT chunks are contiguous.
                if (type.SequenceEqual("PLTE"u8) && hasData)
                {
                    return false;
                }

                if (hasData)
                {
                    dataEnded = true;
                }
            }

            offset += 12 + size;
        }

        return false;
    }

    private static uint[] BuildPngCrcTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var crc = index;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }

            table[index] = crc;
        }

        return table;
    }

    private static uint ComputePngCrc(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in bytes)
        {
            crc = (crc >> 8) ^ PngCrcTable[(int)((crc ^ value) & 0xFF)];
        }

        return ~crc;
    }

    /// <summary>Copies at most <see cref="MaxAssetBytes"/>; -1 when the body is larger or empty.</summary>
    private static async Task<long> CopyBoundedAsync(
        Stream source,
        Stream destination,
        byte[] header,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (total < header.Length)
            {
                Array.Copy(buffer, 0, header, (int)total, (int)Math.Min(read, header.Length - total));
            }

            total += read;
            if (total > MaxAssetBytes)
            {
                return -1;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return total == 0 ? -1 : total;
    }

    /// <summary>
    /// Drops whole keys, least recently used first, until under budget. The key
    /// just written is never the one dropped.
    /// </summary>
    private void Evict(string protectedKey)
    {
        var root = MaterialsRoot();
        if (!Directory.Exists(root))
        {
            return;
        }

        var keys = Directory.EnumerateDirectories(root)
            .Select(directory => new DirectoryInfo(directory))
            .Where(directory => IsValidCacheKey(directory.Name)
                && !string.Equals(directory.Name, protectedKey, StringComparison.Ordinal))
            .Select(directory => (
                Directory: directory,
                Bytes: directory.EnumerateFiles().Sum(file => file.Length),
                Used: directory.LastWriteTimeUtc))
            .OrderBy(entry => entry.Used)
            .ToList();
        var total = keys.Sum(entry => entry.Bytes)
            + new DirectoryInfo(Path.Combine(root, protectedKey)).EnumerateFiles().Sum(file => file.Length);
        foreach (var entry in keys)
        {
            if (total <= _budgetBytes)
            {
                break;
            }

            entry.Directory.Delete(recursive: true);
            total -= entry.Bytes;
        }
    }

    /// <summary>Marks a key as recently used (its directory time is the LRU clock).</summary>
    private static void Touch(string directory)
    {
        try
        {
            Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow);
        }
        catch (IOException)
        {
            // Read-only store: eviction order degrades, nothing else does.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string MaterialsRoot() => Path.Combine(_storeRoot(), "physical-media-materials");

    private string KeyDirectory(string cacheKey)
    {
        if (!IsValidCacheKey(cacheKey))
        {
            throw new ArgumentException("Invalid physical-media material cache key.", nameof(cacheKey));
        }

        return Path.Combine(MaterialsRoot(), cacheKey);
    }

    private string AssetPath(string cacheKey, string assetName)
    {
        if (!IsValidAssetName(assetName))
        {
            throw new ArgumentException("Invalid physical-media material asset name.", nameof(assetName));
        }

        return Path.Combine(KeyDirectory(cacheKey), assetName);
    }
}
