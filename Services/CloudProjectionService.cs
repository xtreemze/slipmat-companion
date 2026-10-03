using System;
using System.Collections.Generic;
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
/// Coordinates a bounded, read-only rclone VFS mount and Jellyfin library projection.
/// Cloud media remains remote-authoritative; local bytes are only a disposable cache.
/// </summary>
public sealed class CloudProjectionService
{
    private const int MarkerVersion = 2;
    private const string LegacyMarkerFileName = ".slipmat-rclone-cloud-projection.json";
    private const string MountMarkerSuffix = ".slipmat-rclone-cloud-mount.json";

    private readonly ICloudProjectionConfigurationSource _configurationSource;
    private readonly IRcloneProcessRunner _runner;
    private readonly ICloudLibraryProjection _libraryProjection;
    private readonly ILogger<CloudProjectionService> _logger;
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);
    private int _mountInProgress;

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

    public bool MountInProgress => Volatile.Read(ref _mountInProgress) != 0;

    public Task<CloudProjectionStatusResponse> GetStatusAsync(
        CancellationToken cancellationToken = default)
        => GetStatusAsync(null, cancellationToken);

    public async Task<CloudProjectionStatusResponse> GetStatusAsync(
        string? projectionId,
        CancellationToken cancellationToken = default)
    {
        var rootConfig = _configurationSource.GetCurrent();
        var cli = new RcloneCliHost(runner: _runner);
        var cliStatus = await cli.GetStatusAsync(cancellationToken).ConfigureAwait(false);

        if (!TrySelectProjectionConfiguration(
                rootConfig,
                projectionId,
                out var config,
                out var selectionCode))
        {
            return new CloudProjectionStatusResponse(
                Configured: false,
                Enabled: false,
                Health: "degraded",
                Code: selectionCode,
                RcloneVersion: cliStatus.Version,
                RemoteCount: cliStatus.Remotes.Count,
                SelectedRemote: null,
                SelectedRemoteType: null,
                RemotePath: null,
                ProjectionPath: null,
                ProjectionPathManaged: true,
                MountInProgress: MountInProgress,
                ProjectionPathReady: false,
                ProjectionHasFiles: false,
                LibraryReady: false,
                LibraryName: null,
                CollectionType: null);
        }

        if (!config.CloudProjectionEnabled)
        {
            var disabledStatus = StatusForDisabled(config, cliStatus);
            var selectedRemoteName = NormalizeOptional(config.CloudRemoteName);
            if (cliStatus.Health != RcloneCliHealth.Ready || selectedRemoteName is null)
            {
                return disabledStatus;
            }

            var selectedRemote = FindRemote(cliStatus, selectedRemoteName);
            if (selectedRemote is null)
            {
                return disabledStatus with
                {
                    Health = "degraded",
                    Code = "selected-remote-missing",
                };
            }

            var disabledRemoteProbe = await cli
                .ProbeAsync(
                    selectedRemoteName,
                    NormalizeOptional(config.CloudRemotePath),
                    cancellationToken)
                .ConfigureAwait(false);

            return disabledRemoteProbe.Success
                ? disabledStatus with
                {
                    SelectedRemoteType = selectedRemote.Type,
                }
                : disabledStatus with
                {
                    Health = "degraded",
                    Code = disabledRemoteProbe.ErrorCode ?? "remote-unavailable",
                    SelectedRemoteType = selectedRemote.Type,
                };
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
                MountInProgress: MountInProgress,
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
                MountInProgress: MountInProgress,
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

        var remoteProbe = await cli
            .ProbeAsync(resolved.RemoteName, resolved.RemotePath, cancellationToken)
            .ConfigureAwait(false);

        return new CloudProjectionStatusResponse(
            Configured: true,
            Enabled: true,
            Health: remoteProbe.Success ? HealthName(cliStatus.Health) : "degraded",
            Code: remoteProbe.Success
                ? cliStatus.Code
                : remoteProbe.ErrorCode ?? "remote-unavailable",
            RcloneVersion: cliStatus.Version,
            RemoteCount: cliStatus.Remotes.Count,
            SelectedRemote: resolved.RemoteName,
            SelectedRemoteType: remote.Type,
            RemotePath: resolved.RemotePath,
            ProjectionPath: resolved.ProjectionPath,
            ProjectionPathManaged: resolved.ProjectionPathManaged,
            MountInProgress: MountInProgress,
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

        return result.ExitCode == 0
            ? "created"
            : RcloneCliHost.ClassifyRemoteFailure(result) ?? "mkdir-failed";
    }

    public Task<CloudProjectionReconcileResponse> ReconcileAsync(
        CancellationToken cancellationToken = default)
        => ReconcileAsync(null, cancellationToken);

    public IReadOnlyList<string> GetProjectionIds()
        => RuntimeSettings.GetCloudLibraries(_configurationSource.GetCurrent())
            .Select(profile => profile.Id)
            .ToArray();

    public async Task<CloudProjectionReconcileResponse> ReconcileAsync(
        string? projectionId,
        CancellationToken cancellationToken = default)
    {
        await _reconcileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var rootConfig = _configurationSource.GetCurrent();
            if (!TrySelectProjectionConfiguration(
                    rootConfig,
                    projectionId,
                    out var config,
                    out var selectionCode))
            {
                return Reconcile("blocked", selectionCode);
            }

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

            var remoteProbe = await cli
                .ProbeAsync(resolved.RemoteName, resolved.RemotePath, cancellationToken)
                .ConfigureAwait(false);
            if (!remoteProbe.Success)
            {
                return Reconcile("failed", remoteProbe.ErrorCode ?? "remote-unavailable");
            }

            var root = InspectProjectionRoot(resolved, remote.Type, createIfMissing: true);
            if (!root.Ready)
            {
                return Reconcile("blocked", root.Code);
            }

            var mountReady = root.MarkerPresent && root.HasFiles;
            if (!mountReady)
            {
                Interlocked.Exchange(ref _mountInProgress, 1);
                RcloneCommandResult mount;
                try
                {
                    mount = await cli
                        .MountReadOnlyAsync(
                            resolved.RemoteName,
                            resolved.RemotePath,
                            resolved.ProjectionPath,
                            resolved.CachePath,
                            resolved.CacheMaxSizeGiB,
                            resolved.CacheMaxAgeHours,
                            resolved.CacheMinFreeSpaceGiB,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Exchange(ref _mountInProgress, 0);
                }

                if (!mount.StartSucceeded)
                {
                    return Reconcile("failed", "rclone-unavailable");
                }

                if (mount.TimedOut)
                {
                    return Reconcile("failed", "mount-timeout");
                }

                var mountFailure = mount.ExitCode == 0
                    ? null
                    : RcloneCliHost.ClassifyMountFailure(mount) ?? "mount-failed";
                if (mountFailure is not null && mountFailure != "mount-already-active")
                {
                    _logger.LogWarning(
                        "rclone cloud mount failed with exit code {ExitCode}",
                        mount.ExitCode);
                    return Reconcile("failed", mountFailure);
                }

                WriteProjectionMarker(
                    GetMountMarkerPath(resolved.ProjectionPath),
                    new ProjectionMarker(
                        MarkerVersion,
                        resolved.RemoteName,
                        remote.Type,
                        resolved.RemotePath));
                mountReady = true;
            }

            root = InspectProjectionRoot(resolved, remote.Type, createIfMissing: false);
            if (!root.Ready)
            {
                return Reconcile("blocked", root.Code, mountReady: mountReady);
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
                    mountReady: mountReady);
            }

            if (_libraryProjection.IsScanRunning)
            {
                return Reconcile(
                    "completed",
                    "mount-ready-library-scan-active",
                    mountReady: mountReady,
                    libraryReady: true);
            }

            _libraryProjection.QueueScan();
            return Reconcile(
                "scan-queued",
                "mount-ready-scan-queued",
                mountReady: mountReady,
                libraryReady: true,
                libraryScanQueued: true);
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    private static bool TrySelectProjectionConfiguration(
        PluginConfiguration rootConfig,
        string? projectionId,
        out PluginConfiguration projectionConfig,
        out string code)
    {
        var profiles = RuntimeSettings.GetCloudLibraries(rootConfig);
        if (profiles.Count == 0)
        {
            projectionConfig = rootConfig;
            if (projectionId is null
                || string.Equals(projectionId, "legacy", StringComparison.OrdinalIgnoreCase))
            {
                code = "configured";
                return true;
            }

            code = "projection-not-found";
            return false;
        }

        if (profiles
            .GroupBy(profile => profile.Id, StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() > 1))
        {
            projectionConfig = new PluginConfiguration();
            code = "projection-id-conflict";
            return false;
        }

        CloudLibraryProfile? selected;
        if (string.IsNullOrWhiteSpace(projectionId))
        {
            selected = profiles[0];
        }
        else
        {
            var matches = profiles
                .Where(profile => string.Equals(
                    profile.Id,
                    projectionId.Trim(),
                    StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToArray();
            if (matches.Length == 0)
            {
                projectionConfig = new PluginConfiguration();
                code = "projection-not-found";
                return false;
            }

            if (matches.Length > 1)
            {
                projectionConfig = new PluginConfiguration();
                code = "projection-id-conflict";
                return false;
            }

            selected = matches[0];
        }

        if (HasStorageConflict(rootConfig, selected, profiles))
        {
            projectionConfig = new PluginConfiguration();
            code = "projection-storage-conflict";
            return false;
        }

        projectionConfig = RuntimeSettings.ProfileAsLegacyConfiguration(selected);
        code = "configured";
        return true;
    }

    private static bool HasStorageConflict(
        PluginConfiguration rootConfig,
        CloudLibraryProfile selected,
        IReadOnlyList<CloudLibraryProfile> profiles)
    {
        if (!TryResolveProfileStorage(selected, out var selectedMount, out var selectedCache))
        {
            return false;
        }

        foreach (var other in profiles)
        {
            if (ReferenceEquals(other, selected)
                || string.Equals(other.Id, selected.Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TryResolveProfileStorage(other, out var otherMount, out var otherCache))
            {
                continue;
            }

            if (PathsOverlap(selectedMount, otherMount)
                || PathsOverlap(selectedCache, otherCache)
                || PathsOverlap(selectedMount, otherCache)
                || PathsOverlap(selectedCache, otherMount))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryResolveProfileStorage(
        CloudLibraryProfile profile,
        out string mountPath,
        out string cachePath)
    {
        mountPath = string.Empty;
        cachePath = string.Empty;
        try
        {
            var config = RuntimeSettings.ProfileAsLegacyConfiguration(profile);
            mountPath = RuntimeSettings.ResolveCloudProjectionPath(
                config,
                profile.RemoteName,
                profile.RemotePath);
            cachePath = RuntimeSettings.ResolveCloudCachePath(
                config,
                profile.RemoteName,
                profile.RemotePath,
                Plugin.Instance?.HostApplicationPaths.DataPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool PathsOverlap(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var leftFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
        var rightFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
        if (string.Equals(leftFull, rightFull, comparison))
        {
            return true;
        }

        var leftPrefix = string.Concat(leftFull, Path.DirectorySeparatorChar);
        var rightPrefix = string.Concat(rightFull, Path.DirectorySeparatorChar);
        return leftFull.StartsWith(rightPrefix, comparison)
            || rightFull.StartsWith(leftPrefix, comparison);
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
            MountInProgress: false,
            ProjectionPathReady: false,
            ProjectionHasFiles: false,
            LibraryReady: false,
            LibraryName: NormalizeOptional(config.CloudLibraryName),
            CollectionType: NormalizeOptional(config.CloudCollectionType));

    private static CloudProjectionReconcileResponse Reconcile(
        string status,
        string code,
        bool mountReady = false,
        bool libraryReady = false,
        bool libraryScanQueued = false)
        => new(
            status,
            code,
            mountReady,
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

        if (config.CloudCacheMaxSizeGiB <= 0
            || config.CloudCacheMaxAgeHours <= 0
            || config.CloudCacheMinFreeSpaceGiB < 0)
        {
            code = "cache-policy-invalid";
            return false;
        }

        string projectionPath;
        string cachePath;
        try
        {
            projectionPath = projectionPathOverride
                ?? RuntimeSettings.ResolveCloudProjectionPath(
                    config,
                    remoteName,
                    remotePath);
            cachePath = RuntimeSettings.ResolveCloudCachePath(
                config,
                remoteName,
                remotePath,
                Plugin.Instance?.HostApplicationPaths.DataPath);
        }
        catch (Exception)
        {
            code = "projection-path-invalid";
            return false;
        }

        if (projectionPath.Length > 4096 || cachePath.Length > 4096)
        {
            code = "projection-path-too-long";
            return false;
        }

        if (!Path.IsPathFullyQualified(projectionPath)
            || !Path.IsPathFullyQualified(cachePath))
        {
            code = "projection-path-not-absolute";
            return false;
        }

        string fullProjectionPath;
        string fullCachePath;
        try
        {
            fullProjectionPath = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(projectionPath));
            fullCachePath = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(cachePath));
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

        if (string.Equals(
                fullProjectionPath,
                fullCachePath,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            code = "cache-path-conflicts-with-mount";
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
            fullCachePath,
            config.CloudCacheMaxSizeGiB,
            config.CloudCacheMaxAgeHours,
            config.CloudCacheMinFreeSpaceGiB,
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
                    return new ProjectionRootState(false, false, false, "projection-path-missing");
                }

                Directory.CreateDirectory(resolved.ProjectionPath);
            }

            var legacyMarkerPath = Path.Combine(
                resolved.ProjectionPath,
                LegacyMarkerFileName);
            if (File.Exists(legacyMarkerPath))
            {
                return new ProjectionRootState(
                    false,
                    false,
                    false,
                    "legacy-materialized-projection-present");
            }

            var markerPath = GetMountMarkerPath(resolved.ProjectionPath);
            if (File.Exists(markerPath))
            {
                if (!TryReadProjectionMarker(markerPath, out var marker))
                {
                    return new ProjectionRootState(false, false, false, "projection-marker-invalid");
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
                    return new ProjectionRootState(false, false, false, "projection-marker-mismatch");
                }

                return new ProjectionRootState(
                    true,
                    HasVisibleEntries(resolved.ProjectionPath),
                    true,
                    "projection-ready");
            }

            if (Directory.EnumerateFileSystemEntries(resolved.ProjectionPath).Any())
            {
                return new ProjectionRootState(false, false, false, "projection-path-not-owned");
            }

            return createIfMissing
                ? new ProjectionRootState(true, false, false, "projection-ready")
                : new ProjectionRootState(false, false, false, "projection-marker-missing");
        }
        catch (UnauthorizedAccessException)
        {
            return new ProjectionRootState(false, false, false, "projection-path-permission-denied");
        }
        catch (IOException)
        {
            return new ProjectionRootState(false, false, false, "projection-path-io-error");
        }
    }

    private static string GetMountMarkerPath(string projectionPath)
        => string.Concat(
            Path.TrimEndingDirectorySeparator(projectionPath),
            MountMarkerSuffix);

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
        string markerPath,
        ProjectionMarker marker)
    {
        var markerDirectory = Path.GetDirectoryName(markerPath);
        if (!string.IsNullOrWhiteSpace(markerDirectory))
        {
            Directory.CreateDirectory(markerDirectory);
        }

        var tempPath = string.Concat(markerPath, ".tmp");
        File.WriteAllText(tempPath, JsonSerializer.Serialize(marker));
        File.Move(tempPath, markerPath, overwrite: true);
    }

    private static bool HasVisibleEntries(string projectionPath)
        => Directory.EnumerateFileSystemEntries(projectionPath).Any();

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
        string CachePath,
        int CacheMaxSizeGiB,
        int CacheMaxAgeHours,
        int CacheMinFreeSpaceGiB,
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
        bool MarkerPresent,
        string Code);
}
