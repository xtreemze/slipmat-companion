using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>Verified live virtual-folder binding; paths are not caller-selected.</summary>
public sealed record CloudMaintenanceLibraryBinding(Guid LibraryId, string LibraryName, string LocalPath, string OptionsPath);

/// <summary>Resolve Jellyfin's actual collection item and its options file.</summary>
public static class CloudMaintenanceAdmissionResolver
{
    /// <summary>Reject ambiguous library names, paths, missing item IDs and non-collection items.</summary>
    public static CloudMaintenanceLibraryBinding Resolve(ILibraryManager library, string libraryName, string localPath)
    {
        ArgumentNullException.ThrowIfNull(library);
        if (string.IsNullOrWhiteSpace(libraryName) || string.IsNullOrWhiteSpace(localPath)) throw Invalid();
        var allFolders = library.GetVirtualFolders(includeRefreshState: true);
        var folders = allFolders
            .Where(folder => string.Equals(folder.Name, libraryName, StringComparison.Ordinal)).ToArray();
        if (folders.Length != 1 || folders[0].Locations is not { Length: 1 }
            || !Guid.TryParse(folders[0].ItemId, out var id) || id == Guid.Empty) throw Invalid();
        var folder = folders[0];
        var item = library.GetItemById<CollectionFolder>(id);
        if (item is null || item.Id != id || string.IsNullOrWhiteSpace(item.Path)) throw Invalid();
        try
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var directory = item.Path;
            if (!Path.IsPathFullyQualified(localPath) || !Path.IsPathFullyQualified(directory)
                || !string.Equals(localPath, Path.GetFullPath(localPath), comparison)
                || !string.Equals(localPath, Path.TrimEndingDirectorySeparator(localPath), comparison)
                || !string.Equals(directory, Path.GetFullPath(directory), comparison)
                || !string.Equals(directory, Path.TrimEndingDirectorySeparator(directory), comparison)
                || !string.Equals(folder.Locations[0], localPath, comparison)
                || !string.Equals(Path.GetFileName(directory), folder.Name, comparison)
                || allFolders.Any(other => !ReferenceEquals(other, folder)
                    && other.Locations?.Any(path => Overlaps(path, localPath, comparison)) == true)) throw Invalid();
            return new(id, folder.Name, localPath, Path.Combine(directory, "options.xml"));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw Invalid();
        }
    }

    private static bool Overlaps(string path, string localPath, StringComparison comparison)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return true;
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return string.Equals(normalized, localPath, comparison)
            || normalized.StartsWith(localPath + Path.DirectorySeparatorChar, comparison)
            || localPath.StartsWith(normalized + Path.DirectorySeparatorChar, comparison)
            || normalized == Path.GetPathRoot(normalized);
    }

    private static InvalidOperationException Invalid() => new("maintenance-library-binding-invalid");
}
