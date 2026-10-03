using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.AudioGateway.Services;

public sealed record CloudLibraryEnsureResult(
    bool Ready,
    bool Created,
    string Code);

/// <summary>
/// Narrow Jellyfin-library projection boundary for a ready read-only cloud mount.
/// </summary>
public interface ICloudLibraryProjection
{
    bool IsScanRunning { get; }

    CloudLibraryEnsureResult Inspect(string libraryName, string projectionPath);

    Task<CloudLibraryEnsureResult> EnsureAsync(
        string libraryName,
        CollectionTypeOptions collectionType,
        string projectionPath,
        CancellationToken cancellationToken);

    void QueueScan();
}

/// <summary>
/// Projects one read-only cloud mount into Jellyfin's supported virtual-folder API.
/// It never deletes libraries or remote/local media.
/// </summary>
public sealed class JellyfinCloudLibraryProjection : ICloudLibraryProjection
{
    private readonly ILibraryManager _libraryManager;

    public JellyfinCloudLibraryProjection(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    public bool IsScanRunning => _libraryManager.IsScanRunning;

    public CloudLibraryEnsureResult Inspect(string libraryName, string projectionPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryName);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectionPath);

        var normalizedPath = NormalizePath(projectionPath);
        var libraries = _libraryManager.GetVirtualFolders(includeRefreshState: true);

        var pathOwner = libraries.FirstOrDefault(library =>
            library.Locations.Any(location =>
                PathsEqual(location, normalizedPath)));

        if (pathOwner is not null)
        {
            return new CloudLibraryEnsureResult(
                Ready: true,
                Created: false,
                Code: "library-ready");
        }

        var nameOwner = libraries.FirstOrDefault(library =>
            string.Equals(library.Name, libraryName, StringComparison.OrdinalIgnoreCase));

        if (nameOwner is not null)
        {
            return new CloudLibraryEnsureResult(
                Ready: false,
                Created: false,
                Code: "library-name-conflict");
        }

        return new CloudLibraryEnsureResult(
            Ready: false,
            Created: false,
            Code: "library-missing");
    }

    public async Task<CloudLibraryEnsureResult> EnsureAsync(
        string libraryName,
        CollectionTypeOptions collectionType,
        string projectionPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var existing = Inspect(libraryName, projectionPath);
        if (existing.Ready || existing.Code == "library-name-conflict")
        {
            return existing;
        }

        var normalizedPath = NormalizePath(projectionPath);
        var options = new LibraryOptions
        {
            // Remote namespace changes are reconciled by the companion's scheduled scan.
            // Do not rely on filesystem watcher semantics across a VFS mount.
            EnableRealtimeMonitor = false,
            PathInfos = [new MediaPathInfo(normalizedPath)],
        };

        await _libraryManager
            .AddVirtualFolder(libraryName, collectionType, options, refreshLibrary: false)
            .ConfigureAwait(false);

        return new CloudLibraryEnsureResult(
            Ready: true,
            Created: true,
            Code: "library-created");
    }

    public void QueueScan()
    {
        _libraryManager.QueueLibraryScan();
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                NormalizePath(left),
                NormalizePath(right),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string NormalizePath(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
