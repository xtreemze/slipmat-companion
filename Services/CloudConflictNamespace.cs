namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>A single observed namespace entry, including empty directories.</summary>
public sealed record CloudConflictNamespaceEntry(string Path, bool IsDirectory);

/// <summary>Exact spellings of a file on each side of a matched namespace.</summary>
public sealed record CloudConflictNamespacePair(string? LocalPath, string? RemotePath);

/// <summary>Validate namespace identity before reading payloads or planning mutations.</summary>
public static class CloudConflictNamespace
{
    /// <summary>Match only uniquely observed paths whose differences are ASCII letter case.</summary>
    public static IReadOnlyList<CloudConflictNamespacePair> Match(
        IEnumerable<CloudConflictNamespaceEntry> local, IEnumerable<CloudConflictNamespaceEntry> remote,
        bool providerCaseInsensitive)
    {
        var localEntries = Build(local);
        var remoteEntries = Build(remote);
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
