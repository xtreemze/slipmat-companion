using System;
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

    /// <summary>Total cache budget; the least recently used keys go first.</summary>
    public const long DefaultBudgetBytes = 1024L * 1024 * 1024;

    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

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

                if (written < 0 || !header.AsSpan().SequenceEqual(PngSignature))
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
