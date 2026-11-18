using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>A single observed namespace entry, including empty directories.</summary>
public sealed record CloudConflictNamespaceEntry(string Path, bool IsDirectory);

/// <summary>Exact spellings of a file on each side of a matched namespace.</summary>
public sealed record CloudConflictNamespacePair(string? LocalPath, string? RemotePath);

/// <summary>Validate namespace identity before reading payloads or planning mutations.</summary>
public static partial class CloudConflictNamespace
{
    internal const string RcloneGoVersion = "go1.26.5";
    // SHA-256 of unicode.ToLower(r) as uint32 little-endian for every scalar in
    // increasing order, generated with the official Go 1.26.5 toolchain (Unicode 15.0.0).
    private const string RcloneLowerMappingSha256 = "10a1522878b42be935ecf849f2689253e6818040e1f683683336581cebb3fd47";
    private static readonly Lazy<bool> RcloneCaseMappingMatches = new(VerifyRcloneCaseMapping);

    internal static void RequireVerifiedRcloneCaseMapping()
    {
        if (!RcloneCaseMappingMatches.Value) throw new InvalidOperationException("conflict-provider-case-unsupported");
    }

    /// <summary>Match only uniquely observed paths whose differences are ASCII letter case.</summary>
    public static IReadOnlyList<CloudConflictNamespacePair> Match(
        IEnumerable<CloudConflictNamespaceEntry> local, IEnumerable<CloudConflictNamespaceEntry> remote,
        bool providerCaseInsensitive)
    {
        var localEntries = Build(local);
        var remoteEntries = Build(remote);
        if (providerCaseInsensitive)
        {
            var localFolded = BuildRcloneKeys(localEntries.Values);
            var remoteFolded = BuildRcloneKeys(remoteEntries.Values);
            foreach (var (key, entry) in localFolded)
                if (remoteFolded.TryGetValue(key, out var other)
                    && (entry.IsDirectory != other.IsDirectory || !AsciiEqual(entry.Path, other.Path))) throw Invalid();
        }
        var result = new List<CloudConflictNamespacePair>();
        foreach (var entry in localEntries.Values)
        {
            if (remoteEntries.TryGetValue(entry.Path, out var other))
            {
                if (entry.IsDirectory != other.IsDirectory || !AsciiEqual(entry.Path, other.Path)) throw Invalid();
                if (!string.Equals(entry.Path, other.Path, StringComparison.Ordinal) && !providerCaseInsensitive) throw Invalid();
                if (!entry.IsDirectory) result.Add(new(entry.Path, other.Path));
            }
            else if (!entry.IsDirectory) result.Add(new(entry.Path, null));
        }
        foreach (var entry in remoteEntries.Values)
            if (!entry.IsDirectory && !localEntries.ContainsKey(entry.Path)) result.Add(new(null, entry.Path));
        return result.OrderBy(pair => pair.LocalPath ?? pair.RemotePath, StringComparer.Ordinal).ToArray();
    }

    private static Dictionary<string, CloudConflictNamespaceEntry> Build(IEnumerable<CloudConflictNamespaceEntry> entries)
    {
        var map = new Dictionary<string, CloudConflictNamespaceEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
            if (entry is null || string.IsNullOrEmpty(entry.Path) || !map.TryAdd(entry.Path, entry)) throw Invalid();
        foreach (var entry in map.Values)
        {
            var slash = entry.Path.LastIndexOf('/');
            if (slash < 0) continue;
            var parent = entry.Path[..slash];
            if (!map.TryGetValue(parent, out var found) || !found.IsDirectory
                || !string.Equals(parent, found.Path, StringComparison.Ordinal)) throw Invalid();
        }
        return map;
    }

    private static Dictionary<string, CloudConflictNamespaceEntry> BuildRcloneKeys(IEnumerable<CloudConflictNamespaceEntry> entries)
    {
        var map = new Dictionary<string, CloudConflictNamespaceEntry>(StringComparer.Ordinal);
        Span<char> scalar = stackalloc char[2];
        foreach (var entry in entries)
        {
            var key = new StringBuilder(entry.Path.Length);
            foreach (var rune in entry.Path.EnumerateRunes())
            {
                var length = RcloneLower(rune).EncodeToUtf16(scalar);
                key.Append(scalar[..length]);
            }
            if (!map.TryAdd(key.ToString(), entry)) throw Invalid();
        }
        return map;
    }

    // rclone's March uses Go strings.ToLower. Use its pinned simple lowercase
    // table so provider matching does not depend on the host's Unicode version.
    internal static Rune RcloneLower(Rune rune)
    {
        var value = rune.Value;
        var first = 0;
        var last = RcloneLowerRanges.Length - 1;
        while (first <= last)
        {
            var middle = first + (last - first) / 2;
            var range = RcloneLowerRanges[middle];
            if (value < range.First)
                last = middle - 1;
            else if (value > range.Last)
                first = middle + 1;
            else
                return (value - range.First) % range.Stride == 0 ? new Rune(value + range.Delta) : rune;
        }
        return rune;
    }

    private static bool VerifyRcloneCaseMapping()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> block = stackalloc byte[4096];
        var offset = 0;
        for (var value = 0; value <= 0x10ffff; value++)
        {
            if (!Rune.TryCreate(value, out var rune)) continue;
            BinaryPrimitives.WriteInt32LittleEndian(block[offset..], RcloneLower(rune).Value);
            offset += sizeof(int);
            if (offset != block.Length) continue;
            hash.AppendData(block);
            offset = 0;
        }
        hash.AppendData(block[..offset]);
        return Convert.ToHexStringLower(hash.GetHashAndReset()) == RcloneLowerMappingSha256;
    }

    internal static bool AsciiEqual(string left, string right)
    {
        if (left.Length != right.Length) return false;
        for (var i = 0; i < left.Length; i++)
        {
            var a = left[i]; var b = right[i];
            if (a == b) continue;
            if (a is >= 'A' and <= 'Z') a = (char)(a + ('a' - 'A'));
            if (b is >= 'A' and <= 'Z') b = (char)(b + ('a' - 'A'));
            if (a != b) return false;
        }
        return true;
    }

    private static InvalidOperationException Invalid() => new("conflict-path-collision");
}
