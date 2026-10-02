using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Configuration;
using Jellyfin.Plugin.AudioGateway.Models;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AudioGateway.Services;

public interface ICloudProjectionConfigurationSource
{
    PluginConfiguration GetCurrent();
}

public sealed class PluginCloudProjectionConfigurationSource
    : ICloudProjectionConfigurationSource
{
    public PluginConfiguration GetCurrent()
        => Plugin.Instance?.Configuration ?? new PluginConfiguration();
}

/// <summary>
/// Coordinates safe, unidirectional rclone remote-to-local materialization and
/// Jellyfin library projection. rclone remains the cloud-provider/auth adapter.
/// </summary>
public sealed class CloudProjectionService
{
    private const int MarkerVersion = 1;
    private const string MarkerFileName = ".slipmat-rclone-cloud-projection.json";

    private readonly ICloudProjectionConfigurationSource _configurationSource;
    private readonly IRcloneProcessRunner _runner;
    private readonly ICloudLibraryProjection _libraryProjection;
    private readonly ILogger<CloudProjectionService> _logger;
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);
    private int _syncInProgress;

    public CloudProjectionService(
        ICloudProjectionConfigurationSource configurationSource,
        IRcloneProcessRunner runner,
        ICloudLibraryProjection libraryProjection,
        ILogger<CloudProjectionService> logger)
    {
        _configurationSource = configurationSource;
        _runner = runner;
        _libraryProjection = libraryProjection;
        _logger = logger;
    }

    public bool SyncInProgress => Volatile.Read(ref _syncInProgress) != 0;

    public async Task<CloudProjectionStatusResponse> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var config = _configurationSource.GetCurrent();
        var cli = new RcloneCliHost(runner: _runner);
        var cliStatus = await cli.GetStatusAsync(cancellationToken).ConfigureAwait(false);

        if (!config.CloudProjectionEnabled)
        {
            return StatusForDisabled(config, cliStatus);
        }

        if (!TryResolveConfiguration(config, out var resolved, out var configurationCode))
        {
            return new CloudProjectionStatusResponse(
                Configured: false,
                Enabled: true,
                Health: HealthName(cliStatus.Health),
                Code: configurationCode,
                RcloneVersion: cliStatus.Version,
                RemoteCount: cliStatus.Remotes.Count,
                SelectedRemote: NormalizeOptional(config.CloudRemoteName),
                SelectedRemoteType: null,
                RemotePath: NormalizeOptional(config.CloudRemotePath),
                ProjectionPath: null,
                ProjectionPathManaged: string.IsNullOrWhiteSpace(config.CloudProjectionPath),
                SyncInProgress: SyncInProgress,
                ProjectionPathReady: false,
                ProjectionHasFiles: false,
                LibraryReady: false,
                LibraryName: NormalizeOptional(config.CloudLibraryName),
                CollectionType: NormalizeOptional(config.CloudCollectionType));
        }

        var remote = FindRemote(cliStatus, resolved.RemoteName);
        if (cliStatus.Health != RcloneCliHealth.Ready || remote is null)
        {
            return new CloudProjectionStatusResponse(
                Configured: true,
                Enabled: true,
                Health: HealthName(cliStatus.Health),
                Code: remote is null && cliStatus.Health == RcloneCliHealth.Ready
                    ? "selected-remote-missing"
                    : cliStatus.Code,
                RcloneVersion: cliStatus.Version,
                RemoteCount: cliStatus.Remotes.Count,
                SelectedRemote: resolved.RemoteName,
                SelectedRemoteType: remote?.Type,
                RemotePath: resolved.RemotePath,
                ProjectionPath: resolved.ProjectionPath,
                ProjectionPathManaged: resolved.ProjectionPathManaged,
                SyncInProgress: SyncInProgress,
                ProjectionPathReady: false,
                ProjectionHasFiles: false,
                LibraryReady: false,
                LibraryName: resolved.LibraryName,
                CollectionType: resolved.CollectionType.ToString());
        }

        var root = InspectProjectionRoot(
            resolved,
            remote.Type,
            createIfMissing: false);

        var library = root.Ready && root.HasFiles
            ? _libraryProjection.Inspect(resolved.LibraryName, resolved.ProjectionPath)
            : new CloudLibraryEnsureResult(false, false, "library-not-ready");

        return new CloudProjectionStatusResponse(
            Configured: true,
            Enabled: true,
            Health: HealthName(cliStatus.Health),
            Code: cliStatus.Code,
            RcloneVersion: cliStatus.Version,
            RemoteCount: cliStatus.Remotes.Count,
            SelectedRemote: resolved.RemoteName,
            SelectedRemoteType: remote.Type,
            RemotePath: resolved.RemotePath,
            ProjectionPath: resolved.ProjectionPath,
            ProjectionPathManaged: resolved.ProjectionPathManaged,
            SyncInProgress: SyncInProgress,
            ProjectionPathReady: root.Ready,
            ProjectionHasFiles: root.HasFiles,
            LibraryReady: library.Ready,
            LibraryName: resolved.LibraryName,
            CollectionType: resolved.CollectionType.ToString());
    }

    public async Task<CloudRemoteListResponse> ListRemotesAsync(
        CancellationToken cancellationToken = default)
    {
        var status = await new RcloneCliHost(runner: _runner)
            .GetStatusAsync(cancellationToken)
            .ConfigureAwait(false);

        return new CloudRemoteListResponse(status.Version, status.Remotes);
    }

    public async Task<RcloneDirectoryListing> BrowseAsync(
        string remoteName,
        string? remotePath,
        CancellationToken cancellationToken = default)
    {
        var cli = new RcloneCliHost(runner: _runner);
        var status = await cli.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.Health != RcloneCliHealth.Ready)
        {
            return new RcloneDirectoryListing(
                false,
                Array.Empty<CloudBrowserEntry>(),
                status.Code,
                false);
        }

        var normalizedRemote = RcloneCliHost.NormalizeRemoteName(remoteName);
        if (FindRemote(status, normalizedRemote) is null)
        {
            return new RcloneDirectoryListing(
                false,
                Array.Empty<CloudBrowserEntry>(),
                "remote-not-configured",
                false);
        }

        return await cli
            .ListAsync(normalizedRemote, remotePath, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<string> CreateDirectoryAsync(
        string remoteName,
        string remotePath,
        CancellationToken cancellationToken = default)
    {
        var cli = new RcloneCliHost(runner: _runner);
        var status = await cli.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.Health != RcloneCliHealth.Ready)
        {
            return status.Code;
        }

        var normalizedRemote = RcloneCliHost.NormalizeRemoteName(remoteName);
        if (FindRemote(status, normalizedRemote) is null)
        {
            return "remote-not-configured";
        }

        var result = await cli
            .CreateDirectoryAsync(normalizedRemote, remotePath, cancellationToken)
            .ConfigureAwait(false);

        if (!result.StartSucceeded)
        {
            return "rclone-unavailable";
        }

        if (result.TimedOut)
        {
            return "mkdir-timeout";
        }

        return result.ExitCode == 0 ? "created" : "mkdir-failed";
    }

    public async Task<CloudProjectionReconcileResponse> ReconcileAsync(
        CancellationToken cancellationToken = default)
    {
        await _reconcileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var config = _configurationSource.GetCurrent();
            if (!config.CloudProjectionEnabled)
            {
                return Reconcile("disabled", "projection-disabled");
            }

            if (!TryResolveConfiguration(config, out var resolved, out var configurationCode))
            {
                return Reconcile("blocked", configurationCode);
            }

            var cli = new RcloneCliHost(runner: _runner);
            var cliStatus = await cli.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (cliStatus.Health != RcloneCliHealth.Ready)
            {
                return Reconcile("blocked", cliStatus.Code);
            }

            var remote = FindRemote(cliStatus, resolved.RemoteName);
            if (remote is null)
            {
                return Reconcile("blocked", "selected-remote-missing");
            }

            var root = InspectProjectionRoot(resolved, remote.Type, createIfMissing: true);
            if (!root.Ready)
            {
                return Reconcile("blocked", root.Code);
            }

            Interlocked.Exchange(ref _syncInProgress, 1);
            RcloneCommandResult copy;
            try
            {
                copy = await cli
                    .CopyToLocalAsync(
                        resolved.RemoteName,
                        resolved.RemotePath,
                        resolved.ProjectionPath,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref _syncInProgress, 0);
            }

            if (!copy.StartSucceeded)
            {
                return Reconcile("failed", "rclone-unavailable");
            }

            if (copy.TimedOut)
            {
                return Reconcile("failed", "copy-timeout");
            }

            if (copy.ExitCode != 0)
            {
                _logger.LogWarning(
                    "rclone cloud copy failed with exit code {ExitCode}",
                    copy.ExitCode);
                return Reconcile("failed", "copy-failed");
            }

            root = InspectProjectionRoot(resolved, remote.Type, createIfMissing: false);
            if (!root.Ready)
            {
                return Reconcile("blocked", root.Code, copyCompleted: true);
            }

            if (!root.HasFiles)
            {
                return Reconcile("idle", "projection-empty", copyCompleted: true);
            }

            var library = _libraryProjection.Inspect(
                resolved.LibraryName,
                resolved.ProjectionPath);

            if (!library.Ready && config.CloudAutoCreateLibrary)
            {
                library = await _libraryProjection
                    .EnsureAsync(
                        resolved.LibraryName,
                        resolved.CollectionType,
                        resolved.ProjectionPath,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!library.Ready)
            {
                return Reconcile(
                    "blocked",
                    library.Code,
                    copyCompleted: true);
            }

            if (_libraryProjection.IsScanRunning)
            {
                return Reconcile(
                    "completed",
                    "copy-complete-library-scan-active",
                    copyCompleted: true,
                    libraryReady: true);
            }

            _libraryProjection.QueueScan();
            return Reconcile(
                "scan-queued",
                "copy-complete-scan-queued",
                copyCompleted: true,
                libraryReady: true,
                libraryScanQueued: true);
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    private static RcloneRemoteDescriptor? FindRemote(
        RcloneCliStatus status,
        string remoteName)
        => status.Remotes.FirstOrDefault(
            remote => string.Equals(
                remote.Name,
                remoteName,
                StringComparison.OrdinalIgnoreCase));

    private static CloudProjectionStatusResponse StatusForDisabled(
        PluginConfiguration config,
        RcloneCliStatus cliStatus)
        => new(
            Configured: false,
            Enabled: false,
            Health: HealthName(cliStatus.Health),
            Code: cliStatus.Code,
            RcloneVersion: cliStatus.Version,
            RemoteCount: cliStatus.Remotes.Count,
            SelectedRemote: NormalizeOptional(config.CloudRemoteName),
            SelectedRemoteType: null,
            RemotePath: NormalizeOptional(config.CloudRemotePath),
            ProjectionPath: null,
            ProjectionPathManaged: string.IsNullOrWhiteSpace(config.CloudProjectionPath),
            SyncInProgress: false,
            ProjectionPathReady: false,
            ProjectionHasFiles: false,
            LibraryReady: false,
            LibraryName: NormalizeOptional(config.CloudLibraryName),
            CollectionType: NormalizeOptional(config.CloudCollectionType));

    private static CloudProjectionReconcileResponse Reconcile(
        string status,
        string code,
        bool copyCompleted = false,
        bool libraryReady = false,
        bool libraryScanQueued = false)
        => new(
            status,
            code,
            copyCompleted,
            libraryReady,
            libraryScanQueued);

    private static bool TryResolveConfiguration(
        PluginConfiguration config,
        out ResolvedConfiguration resolved,
        out string code)
    {
        resolved = default!;

        string remoteName;
        string remotePath;
        try
        {
            remoteName = RcloneCliHost.NormalizeRemoteName(config.CloudRemoteName);
            remotePath = RcloneCliHost.NormalizeRemotePath(config.CloudRemotePath);
        }
        catch (ArgumentException)
        {
            code = "remote-invalid";
            return false;
        }

        var projectionPathOverride = NormalizeOptional(config.CloudProjectionPath);
        var libraryName = NormalizeOptional(config.CloudLibraryName);
        var collectionTypeText = NormalizeOptional(config.CloudCollectionType);

        string projectionPath;
        try
        {
            projectionPath = projectionPathOverride
                ?? RuntimeSettings.ResolveCloudProjectionPath(
                    config,
                    remoteName,
                    remotePath);
        }
        catch (Exception)
        {
            code = "projection-path-invalid";
            return false;
        }

        if (projectionPath.Length > 4096)
        {
            code = "projection-path-too-long";
            return false;
        }

        if (!Path.IsPathFullyQualified(projectionPath))
        {
            code = "projection-path-not-absolute";
            return false;
        }

        string fullProjectionPath;
        try
        {
            fullProjectionPath = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(projectionPath));
        }
        catch (Exception)
        {
            code = "projection-path-invalid";
            return false;
        }

        var rootPath = Path.TrimEndingDirectorySeparator(
            Path.GetPathRoot(fullProjectionPath) ?? string.Empty);
        if (string.Equals(
                fullProjectionPath,
                rootPath,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            code = "projection-path-root-forbidden";
            return false;
        }

        if (libraryName is null)
        {
            code = "library-name-required";
            return false;
        }

        if (libraryName.Length > 256)
        {
            code = "library-name-too-long";
            return false;
        }

        if (!Enum.TryParse<CollectionTypeOptions>(
                collectionTypeText,
                ignoreCase: true,
                out var collectionType))
        {
            code = "collection-type-invalid";
            return false;
        }

        resolved = new ResolvedConfiguration(
            remoteName,
            remotePath,
            fullProjectionPath,
            projectionPathOverride is null,
            libraryName,
            collectionType);
        code = "configured";
        return true;
    }

    private static ProjectionRootState InspectProjectionRoot(
        ResolvedConfiguration resolved,
        string? remoteType,
        bool createIfMissing)
    {
        try
        {
            if (!Directory.Exists(resolved.ProjectionPath))
            {
                if (!createIfMissing)
                {
                    return new ProjectionRootState(false, false, "projection-path-missing");
                }

                Directory.CreateDirectory(resolved.ProjectionPath);
            }

            var markerPath = Path.Combine(resolved.ProjectionPath, MarkerFileName);
            if (File.Exists(markerPath))
            {
                if (!TryReadProjectionMarker(markerPath, out var marker))
                {
                    return new ProjectionRootState(false, false, "projection-marker-invalid");
                }

                if (marker.Version != MarkerVersion
                    || !string.Equals(
                        marker.RemoteName,
                        resolved.RemoteName,
                        StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(
                        marker.RemoteType ?? string.Empty,
                        remoteType ?? string.Empty,
                        StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(
                        marker.RemotePath,
                        resolved.RemotePath,
                        StringComparison.Ordinal))
                {
                    return new ProjectionRootState(false, false, "projection-marker-mismatch");
                }

                return new ProjectionRootState(
                    true,
                    HasMaterializedFiles(resolved.ProjectionPath),
                    "projection-ready");
            }

            if (Directory.EnumerateFileSystemEntries(resolved.ProjectionPath).Any())
            {
                return new ProjectionRootState(false, false, "projection-path-not-owned");
            }

            if (!createIfMissing)
            {
                return new ProjectionRootState(false, false, "projection-marker-missing");
            }

            WriteProjectionMarker(
                resolved.ProjectionPath,
                new ProjectionMarker(
                    MarkerVersion,
                    resolved.RemoteName,
                    remoteType,
                    resolved.RemotePath));

            return new ProjectionRootState(true, false, "projection-ready");
        }
        catch (UnauthorizedAccessException)
        {
            return new ProjectionRootState(false, false, "projection-path-permission-denied");
        }
        catch (IOException)
        {
            return new ProjectionRootState(false, false, "projection-path-io-error");
        }
    }

    private static bool TryReadProjectionMarker(
        string markerPath,
        out ProjectionMarker marker)
    {
        marker = default!;
        try
        {
            var json = File.ReadAllText(markerPath);
            var parsed = JsonSerializer.Deserialize<ProjectionMarker>(json);
            if (parsed is null
                || string.IsNullOrWhiteSpace(parsed.RemoteName))
            {
                return false;
            }

            marker = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void WriteProjectionMarker(
        string projectionPath,
        ProjectionMarker marker)
    {
        var markerPath = Path.Combine(projectionPath, MarkerFileName);
        var tempPath = string.Concat(markerPath, ".tmp");
        File.WriteAllText(tempPath, JsonSerializer.Serialize(marker));
        File.Move(tempPath, markerPath, overwrite: true);
    }

    private static bool HasMaterializedFiles(string projectionPath)
        => Directory
            .EnumerateFileSystemEntries(projectionPath)
            .Any(path => !string.Equals(
                Path.GetFileName(path),
                MarkerFileName,
                StringComparison.Ordinal)
                && !string.Equals(
                    Path.GetFileName(path),
                    string.Concat(MarkerFileName, ".tmp"),
                    StringComparison.Ordinal));

    private static string HealthName(RcloneCliHealth health)
        => health switch
        {
            RcloneCliHealth.Unavailable => "unavailable",
            RcloneCliHealth.TimedOut => "timed-out",
            RcloneCliHealth.Ready => "ready",
            _ => "degraded",
        };

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private sealed record ResolvedConfiguration(
        string RemoteName,
        string RemotePath,
        string ProjectionPath,
        bool ProjectionPathManaged,
        string LibraryName,
        CollectionTypeOptions CollectionType);

    private sealed record ProjectionMarker(
        int Version,
        string RemoteName,
        string? RemoteType,
        string RemotePath);

    private sealed record ProjectionRootState(
        bool Ready,
        bool HasFiles,
        string Code);
}
