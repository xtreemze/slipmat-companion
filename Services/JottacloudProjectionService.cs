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

/// <summary>
/// Provides the current persisted plugin configuration without moving provider
/// credentials into the plugin. Jottacloud authentication remains owned by jottad.
/// </summary>
public interface IJottacloudProjectionConfigurationSource
{
    PluginConfiguration GetCurrent();
}

public sealed class PluginJottacloudProjectionConfigurationSource
    : IJottacloudProjectionConfigurationSource
{
    public PluginConfiguration GetCurrent()
        => Plugin.Instance?.Configuration ?? new PluginConfiguration();
}

/// <summary>
/// Coordinates safe, unidirectional cloud-to-local materialization and Jellyfin
/// library projection. It never logs in, deletes remote data, or owns playback.
/// </summary>
public sealed class JottacloudProjectionService
{
    private const int MarkerVersion = 2;
    private const string MarkerFileName = ".slipmat-jottacloud-projection.json";

    private readonly IJottacloudProjectionConfigurationSource _configurationSource;
    private readonly IJottacloudCliProcessRunner _runner;
    private readonly IJottacloudLibraryProjection _libraryProjection;
    private readonly ILogger<JottacloudProjectionService> _logger;
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);

    public JottacloudProjectionService(
        IJottacloudProjectionConfigurationSource configurationSource,
        IJottacloudCliProcessRunner runner,
        IJottacloudLibraryProjection libraryProjection,
        ILogger<JottacloudProjectionService> logger)
    {
        _configurationSource = configurationSource;
        _runner = runner;
        _libraryProjection = libraryProjection;
        _logger = logger;
    }

    public async Task<JottacloudProjectionStatusResponse> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var config = _configurationSource.GetCurrent();
        if (!config.JottacloudProjectionEnabled)
        {
            return DisabledStatus(config);
        }

        if (!TryResolveConfiguration(config, out var resolved, out var configurationCode))
        {
            return new JottacloudProjectionStatusResponse(
                Configured: false,
                Enabled: true,
                Health: "configuration-error",
                Code: configurationCode,
                CliVersion: null,
                RemotePath: NormalizeOptional(config.JottacloudRemotePath),
                ProjectionPath: null,
                ProjectionPathManaged: string.IsNullOrWhiteSpace(config.JottacloudProjectionPath),
                DownloadQueueKnown: false,
                DownloadQueueEntries: 0,
                ProjectionPathReady: false,
                ProjectionHasFiles: false,
                LibraryReady: false,
                LibraryName: NormalizeOptional(config.JottacloudLibraryName),
                CollectionType: NormalizeOptional(config.JottacloudCollectionType));
        }

        var cli = CreateCli(resolved);
        var cliStatus = await cli.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (cliStatus.Health == JottacloudCliHealth.Ready
            && string.IsNullOrWhiteSpace(cliStatus.AccountFingerprint))
        {
            cliStatus = new JottacloudCliStatus(
                JottacloudCliHealth.Degraded,
                cliStatus.CliVersion,
                "account-identity-unavailable",
                null);
        }

        var queue = cliStatus.Health == JottacloudCliHealth.Ready
            ? await cli.GetDownloadQueueAsync(cancellationToken).ConfigureAwait(false)
            : new JottacloudCliDownloadQueue(false, 0, "health-blocked");

        var root = cliStatus.Health == JottacloudCliHealth.Ready
            && !string.IsNullOrWhiteSpace(cliStatus.AccountFingerprint)
            ? InspectProjectionRoot(
                resolved.RemotePath,
                cliStatus.AccountFingerprint,
                resolved.ProjectionPath,
                createIfMissing: false)
            : new ProjectionRootState(
                Ready: false,
                HasFiles: false,
                RefreshPendingScan: false,
                Code: "health-blocked");

        var library = root.Ready && root.HasFiles
            ? _libraryProjection.Inspect(resolved.LibraryName, resolved.ProjectionPath)
            : new JottacloudLibraryEnsureResult(false, false, "library-not-ready");

        return new JottacloudProjectionStatusResponse(
            Configured: true,
            Enabled: true,
            Health: HealthName(cliStatus.Health),
            Code: cliStatus.Code,
            CliVersion: cliStatus.CliVersion,
            RemotePath: resolved.RemotePath,
            ProjectionPath: resolved.ProjectionPath,
            ProjectionPathManaged: resolved.ProjectionPathManaged,
            DownloadQueueKnown: queue.Known,
            DownloadQueueEntries: queue.QueueEntryCount,
            ProjectionPathReady: root.Ready,
            ProjectionHasFiles: root.HasFiles,
            LibraryReady: library.Ready,
            LibraryName: resolved.LibraryName,
            CollectionType: resolved.CollectionType.ToString());
    }

    public async Task<JottacloudCliDirectoryListing> BrowseAsync(
        string remotePath,
        bool includeDetails,
        CancellationToken cancellationToken = default)
    {
        var cli = new JottacloudCliHost(runner: _runner);
        var status = await cli.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.Health != JottacloudCliHealth.Ready)
        {
            return new JottacloudCliDirectoryListing(
                Success: false,
                Lines: Array.Empty<string>(),
                ErrorCode: status.Code,
                OutputTruncated: false);
        }

        return await cli
            .ListAsync(remotePath, includeDetails, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Performs one reconciliation iteration. A completed prior materialization is
    /// made visible to Jellyfin before another merge refresh is queued. Jellyfin
    /// scans only after the daemon reports a clear download queue; retained failed
    /// entries therefore block scanning until the operator resolves them.
    /// </summary>
    public async Task<JottacloudProjectionReconcileResponse> ReconcileAsync(
        CancellationToken cancellationToken = default)
    {
        await _reconcileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var config = _configurationSource.GetCurrent();
            if (!config.JottacloudProjectionEnabled)
            {
                return Reconcile("disabled", "projection-disabled");
            }

            if (!TryResolveConfiguration(config, out var resolved, out var configurationCode))
            {
                return Reconcile("blocked", configurationCode);
            }

            var cli = CreateCli(resolved);
            var cliStatus = await cli.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (cliStatus.Health != JottacloudCliHealth.Ready
                || string.IsNullOrWhiteSpace(cliStatus.AccountFingerprint))
            {
                return Reconcile(
                    "blocked",
                    cliStatus.Health == JottacloudCliHealth.Ready
                        ? "account-identity-unavailable"
                        : cliStatus.Code);
            }

            var queue = await cli.GetDownloadQueueAsync(cancellationToken).ConfigureAwait(false);
            if (!queue.Known)
            {
                return Reconcile("blocked", queue.Code);
            }

            if (!queue.IsClear)
            {
                return Reconcile("in-progress", "download-queue-not-clear");
            }

            var root = InspectProjectionRoot(
                resolved.RemotePath,
                cliStatus.AccountFingerprint!,
                resolved.ProjectionPath,
                createIfMissing: true);
            if (!root.Ready)
            {
                return Reconcile("blocked", root.Code);
            }

            var libraryReady = false;
            var libraryScanQueued = false;

            if (root.RefreshPendingScan)
            {
                if (!root.HasFiles)
                {
                    WriteProjectionMarker(
                        resolved.ProjectionPath,
                        resolved.RemotePath,
                        cliStatus.AccountFingerprint!,
                        refreshPendingScan: false);

                    return Reconcile(
                        "idle",
                        "projection-empty");
                }

                var library = _libraryProjection.Inspect(
                    resolved.LibraryName,
                    resolved.ProjectionPath);

                if (!library.Ready && config.JottacloudAutoCreateLibrary)
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
                        libraryReady: false,
                        libraryScanQueued: false);
                }

                if (_libraryProjection.IsScanRunning)
                {
                    return Reconcile(
                        "in-progress",
                        "library-scan-active",
                        libraryReady: true,
                        libraryScanQueued: false);
                }

                _libraryProjection.QueueScan();
                WriteProjectionMarker(
                    resolved.ProjectionPath,
                    resolved.RemotePath,
                    cliStatus.AccountFingerprint!,
                    refreshPendingScan: false);

                return Reconcile(
                    "scan-queued",
                    "projection-scan-queued",
                    libraryReady: true,
                    libraryScanQueued: true);
            }

            if (_libraryProjection.IsScanRunning)
            {
                return Reconcile(
                    "in-progress",
                    "library-scan-active",
                    libraryReady: root.HasFiles
                        && _libraryProjection
                            .Inspect(resolved.LibraryName, resolved.ProjectionPath)
                            .Ready);
            }

            if (root.HasFiles)
            {
                libraryReady = _libraryProjection
                    .Inspect(resolved.LibraryName, resolved.ProjectionPath)
                    .Ready;
            }

            var download = await cli
                .StartManagedDownloadAsync(
                    resolved.RemotePath,
                    resolved.ProjectionPath,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!download.StartSucceeded)
            {
                return Reconcile(
                    "failed",
                    "download-cli-unavailable",
                    libraryReady: libraryReady,
                    libraryScanQueued: libraryScanQueued);
            }

            if (download.TimedOut)
            {
                return Reconcile(
                    "failed",
                    "download-command-timeout",
                    libraryReady: libraryReady,
                    libraryScanQueued: libraryScanQueued);
            }

            if (download.ExitCode != 0)
            {
                _logger.LogWarning(
                    "Jottacloud managed download command failed with exit code {ExitCode}",
                    download.ExitCode);

                return Reconcile(
                    "failed",
                    "download-command-failed",
                    libraryReady: libraryReady,
                    libraryScanQueued: libraryScanQueued);
            }

            WriteProjectionMarker(
                resolved.ProjectionPath,
                resolved.RemotePath,
                cliStatus.AccountFingerprint!,
                refreshPendingScan: true);

            return Reconcile(
                "queued",
                "download-queued",
                downloadQueued: true,
                libraryReady: libraryReady,
                libraryScanQueued: libraryScanQueued);
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    private JottacloudCliHost CreateCli(ResolvedConfiguration configuration)
        => new(runner: _runner);

    private static JottacloudProjectionStatusResponse DisabledStatus(
        PluginConfiguration config)
        => new(
            Configured: false,
            Enabled: false,
            Health: "disabled",
            Code: "projection-disabled",
            CliVersion: null,
            RemotePath: NormalizeOptional(config.JottacloudRemotePath),
            ProjectionPath: null,
            ProjectionPathManaged: string.IsNullOrWhiteSpace(config.JottacloudProjectionPath),
            DownloadQueueKnown: false,
            DownloadQueueEntries: 0,
            ProjectionPathReady: false,
            ProjectionHasFiles: false,
            LibraryReady: false,
            LibraryName: NormalizeOptional(config.JottacloudLibraryName),
            CollectionType: NormalizeOptional(config.JottacloudCollectionType));

    private static JottacloudProjectionReconcileResponse Reconcile(
        string status,
        string code,
        bool downloadQueued = false,
        bool libraryReady = false,
        bool libraryScanQueued = false)
        => new(
            Status: status,
            Code: code,
            DownloadQueued: downloadQueued,
            LibraryReady: libraryReady,
            LibraryScanQueued: libraryScanQueued);

    private static bool TryResolveConfiguration(
        PluginConfiguration config,
        out ResolvedConfiguration resolved,
        out string code)
    {
        resolved = default!;

        var remotePath = NormalizeOptional(config.JottacloudRemotePath);
        var projectionPathOverride = NormalizeOptional(config.JottacloudProjectionPath);
        var libraryName = NormalizeOptional(config.JottacloudLibraryName);
        var collectionTypeText = NormalizeOptional(config.JottacloudCollectionType);

        if (remotePath is null)
        {
            code = "remote-path-required";
            return false;
        }

        if (remotePath.Length > 4096
            || remotePath.IndexOfAny(['\0', '\r', '\n']) >= 0
            || remotePath.StartsWith("-", StringComparison.Ordinal))
        {
            code = "remote-path-invalid";
            return false;
        }

        string projectionPath;
        try
        {
            projectionPath = projectionPathOverride
                ?? RuntimeSettings.ResolveJottacloudProjectionPath(config, remotePath);
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
            fullProjectionPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectionPath));
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
            RemotePath: remotePath,
            ProjectionPath: fullProjectionPath,
            ProjectionPathManaged: projectionPathOverride is null,
            LibraryName: libraryName,
            CollectionType: collectionType);
        code = "configured";
        return true;
    }

    private static ProjectionRootState InspectProjectionRoot(
        string remotePath,
        string accountFingerprint,
        string projectionPath,
        bool createIfMissing)
    {
        try
        {
            if (!Directory.Exists(projectionPath))
            {
                if (!createIfMissing)
                {
                    return new ProjectionRootState(false, false, false, "projection-path-missing");
                }

                Directory.CreateDirectory(projectionPath);
            }

            var markerPath = Path.Combine(projectionPath, MarkerFileName);
            if (File.Exists(markerPath))
            {
                if (!TryReadProjectionMarker(markerPath, out var marker))
                {
                    return new ProjectionRootState(
                        Ready: false,
                        HasFiles: false,
                        RefreshPendingScan: false,
                        Code: "projection-marker-invalid");
                }

                if (marker.Version != MarkerVersion
                    || !string.Equals(marker.RemotePath, remotePath, StringComparison.Ordinal)
                    || !string.Equals(
                        marker.AccountFingerprint,
                        accountFingerprint,
                        StringComparison.Ordinal))
                {
                    return new ProjectionRootState(
                        Ready: false,
                        HasFiles: false,
                        RefreshPendingScan: false,
                        Code: "projection-marker-mismatch");
                }

                return new ProjectionRootState(
                    Ready: true,
                    HasFiles: HasMaterializedFiles(projectionPath),
                    RefreshPendingScan: marker.RefreshPendingScan,
                    Code: "projection-ready");
            }

            if (Directory
                .EnumerateFileSystemEntries(projectionPath)
                .Any())
            {
                return new ProjectionRootState(false, false, false, "projection-path-not-owned");
            }

            if (!createIfMissing)
            {
                return new ProjectionRootState(false, false, false, "projection-marker-missing");
            }

            WriteProjectionMarker(
                projectionPath,
                remotePath,
                accountFingerprint,
                refreshPendingScan: false);

            return new ProjectionRootState(
                Ready: true,
                HasFiles: false,
                RefreshPendingScan: false,
                Code: "projection-ready");
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

    private static bool TryReadProjectionMarker(
        string markerPath,
        out ProjectionMarker marker)
    {
        marker = default!;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(markerPath));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("Version", out var versionElement)
                || !versionElement.TryGetInt32(out var version)
                || !root.TryGetProperty("RemotePath", out var remotePathElement)
                || remotePathElement.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("AccountFingerprint", out var accountFingerprintElement)
                || accountFingerprintElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var remotePath = remotePathElement.GetString();
            var accountFingerprint = accountFingerprintElement.GetString();
            if (string.IsNullOrWhiteSpace(remotePath)
                || string.IsNullOrWhiteSpace(accountFingerprint)
                || accountFingerprint.Length != 64)
            {
                return false;
            }

            var refreshPendingScan = root.TryGetProperty(
                    "RefreshPendingScan",
                    out var pendingElement)
                && pendingElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                && pendingElement.GetBoolean();

            marker = new ProjectionMarker(
                version,
                remotePath,
                accountFingerprint,
                refreshPendingScan);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void WriteProjectionMarker(
        string projectionPath,
        string remotePath,
        string accountFingerprint,
        bool refreshPendingScan)
    {
        var markerPath = Path.Combine(projectionPath, MarkerFileName);
        var tempPath = markerPath + ".tmp";

        var markerJson = JsonSerializer.Serialize(
            new ProjectionMarker(
                MarkerVersion,
                remotePath,
                accountFingerprint,
                refreshPendingScan));

        File.WriteAllText(tempPath, markerJson);
        File.Move(tempPath, markerPath, overwrite: true);
    }

    private static bool HasMaterializedFiles(string projectionPath)
        => Directory
            .EnumerateFileSystemEntries(projectionPath)
            .Any(path =>
            {
                var fileName = Path.GetFileName(path);
                return !string.Equals(
                        fileName,
                        MarkerFileName,
                        StringComparison.Ordinal)
                    && !string.Equals(
                        fileName,
                        MarkerFileName + ".tmp",
                        StringComparison.Ordinal);
            });

    private static string HealthName(JottacloudCliHealth health)
        => health switch
        {
            JottacloudCliHealth.Unavailable => "unavailable",
            JottacloudCliHealth.DaemonUnavailable => "daemon-unavailable",
            JottacloudCliHealth.AuthenticationRequired => "authentication-required",
            JottacloudCliHealth.TimedOut => "timed-out",
            JottacloudCliHealth.Ready => "ready",
            _ => "degraded",
        };

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private sealed record ResolvedConfiguration(
        string RemotePath,
        string ProjectionPath,
        bool ProjectionPathManaged,
        string LibraryName,
        CollectionTypeOptions CollectionType);

    private sealed record ProjectionMarker(
        int Version,
        string RemotePath,
        string AccountFingerprint,
        bool RefreshPendingScan);

    private sealed record ProjectionRootState(
        bool Ready,
        bool HasFiles,
        bool RefreshPendingScan,
        string Code);
}
