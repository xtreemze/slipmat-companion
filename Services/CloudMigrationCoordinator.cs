using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Configuration;
using Jellyfin.Plugin.AudioGateway.Models;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>
/// Elevated orchestration boundary for reversible Jellyfin-library migrations.
/// Long transfers run in <see cref="CloudMigrationWorker"/>; this service performs
/// bounded preflight and explicit state transitions only.
/// </summary>
public sealed class CloudMigrationCoordinator
{
    private readonly CloudMigrationStore _store;
    private readonly CloudMigrationQueue _queue;
    private readonly ICloudProjectionConfigurationSource _configurationSource;
    private readonly ILibraryManager _library;
    private readonly ICloudLibraryProjection _libraryProjection;
    private readonly CloudProjectionService _projectionService;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CloudMigrationCoordinator(
        CloudMigrationStore store,
        CloudMigrationQueue queue,
        ICloudProjectionConfigurationSource configurationSource,
        ILibraryManager library,
        ICloudLibraryProjection libraryProjection,
        CloudProjectionService projectionService)
    {
        _store = store;
        _queue = queue;
        _configurationSource = configurationSource;
        _library = library;
        _libraryProjection = libraryProjection;
        _projectionService = projectionService;
    }

    public Task<IReadOnlyList<CloudMigrationJob>> ListAsync(
        CancellationToken cancellationToken = default)
        => _store.LoadAllAsync(cancellationToken);

    public IReadOnlyList<CloudMigrationCandidate> GetCandidates(string direction)
    {
        ValidateDirection(direction);
        var config = _configurationSource.GetCurrent();
        var profiles = RuntimeSettings.GetCloudLibraries(config);
        var profileGroups = profiles
            .GroupBy(profile => profile.LibraryName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray(),
                StringComparer.OrdinalIgnoreCase);

        return _library
            .GetVirtualFolders(includeRefreshState: true)
            .OrderBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase)
            .Select(folder => CandidateFor(folder, config, profileGroups, direction))
            .ToArray();
    }

    public async Task<CloudMigrationJob> StartAsync(
        CloudMigrationStartRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateDirection(request.Direction);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var config = _configurationSource.GetCurrent();
            var profile = FindProfile(config, request.ProfileId);
            if (!string.Equals(
                    profile.LibraryName,
                    request.LibraryName?.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("profile-library-mismatch");
            }

            var folder = _library
                .GetVirtualFolders(includeRefreshState: true)
                .SingleOrDefault(candidate => string.Equals(
                    candidate.Name,
                    request.LibraryName?.Trim(),
                    StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("library-not-found");

            if (folder.Locations.Length != 1)
            {
                throw new InvalidOperationException("multiple-library-paths-not-yet-supported");
            }

            var localPath = Path.GetFullPath(folder.Locations[0]);
            var currentJobs = await _store.LoadAllAsync(cancellationToken).ConfigureAwait(false);
            CloudLibraryScanPolicy? previousScanPolicy = null;
            if (currentJobs.Any(job =>
                    string.Equals(job.ProfileId, profile.Id, StringComparison.OrdinalIgnoreCase)
                    && !IsTerminal(job.Phase)))
            {
                throw new InvalidOperationException("migration-already-active");
            }

            if (request.Direction == CloudMigrationDirections.LocalToVfs)
            {
                previousScanPolicy = _libraryProjection.CaptureScanPolicy(
                    folder.Name,
                    localPath)
                    ?? throw new InvalidOperationException("library-scan-policy-unavailable");

                if (profile.Enabled)
                {
                    throw new InvalidOperationException("profile-must-be-disabled-before-migration");
                }

                if (RuntimeSettings.IsCloudProjectionPath(config, localPath))
                {
                    throw new InvalidOperationException("library-already-vfs");
                }

                if (!Directory.Exists(localPath))
                {
                    throw new InvalidOperationException("local-source-missing");
                }

                if (ContainsReparsePoint(localPath))
                {
                    throw new InvalidOperationException("source-reparse-point-unsupported");
                }

                var listing = await _projectionService
                    .BrowseAsync(
                        profile.RemoteName,
                        profile.RemotePath,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!listing.Success)
                {
                    throw new InvalidOperationException(listing.ErrorCode ?? "remote-unavailable");
                }

                if (listing.Entries.Count != 0)
                {
                    throw new InvalidOperationException("migration-destination-not-empty");
                }
            }
            else
            {
                if (!profile.Enabled)
                {
                    throw new InvalidOperationException("profile-not-enabled");
                }

                if (!RuntimeSettings.IsCloudProjectionPath(config, localPath))
                {
                    throw new InvalidOperationException("library-not-vfs");
                }

                previousScanPolicy = currentJobs
                    .Where(job =>
                        string.Equals(
                            job.ProfileId,
                            profile.Id,
                            StringComparison.OrdinalIgnoreCase)
                        && job.Direction == CloudMigrationDirections.LocalToVfs
                        && job.PreviousLibraryScanPolicy is not null)
                    .OrderByDescending(job => job.UpdatedAt)
                    .Select(job => job.PreviousLibraryScanPolicy)
                    .FirstOrDefault();
            }

            var id = Guid.NewGuid().ToString("N");
            var now = DateTimeOffset.UtcNow;
            var stagingPath = request.Direction == CloudMigrationDirections.VfsToLocal
                ? string.Concat(localPath, ".slipmat-local-staging-", id[..12])
                : null;
            var job = new CloudMigrationJob(
                Id: id,
                ProfileId: profile.Id,
                LibraryName: folder.Name,
                Direction: request.Direction,
                Phase: CloudMigrationPhases.QueuedCopy,
                Code: "queued",
                LocalPath: localPath,
                StagingPath: stagingPath,
                BackupPath: null,
                RemoteName: profile.RemoteName,
                RemotePath: profile.RemotePath,
                PreviousProjectionPath: profile.ProjectionPath,
                PreviousEnabled: profile.Enabled,
                Verification: null,
                CreatedAt: now,
                UpdatedAt: now,
                PreviousLibraryScanPolicy: previousScanPolicy,
                ManifestVersion: manifest?.Version,
                ManifestDigestSha256: manifest?.DigestSha256,
                ManifestFileCount: manifest?.FileCount,
                ManifestTotalBytes: manifest?.TotalBytes,
                ManifestCapturedAt: manifest?.CapturedAt);

            await _store.SaveAsync(job, cancellationToken).ConfigureAwait(false);
            if (!_queue.TryEnqueue(job.Id))
            {
                var failed = job with
                {
                    Phase = CloudMigrationPhases.Failed,
                    Code = "migration-queue-full",
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
                await _store.SaveAsync(failed, cancellationToken).ConfigureAwait(false);
                return failed;
            }

            return job;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CloudMigrationBulkStartResponse> StartBulkAsync(
        CloudMigrationBulkStartRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateDirection(request.Direction);

        var started = new List<CloudMigrationJob>();
        var skipped = new List<CloudMigrationCandidate>();
        foreach (var candidate in GetCandidates(request.Direction))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!candidate.Eligible || string.IsNullOrWhiteSpace(candidate.ProfileId))
            {
                skipped.Add(candidate);
                continue;
            }

            try
            {
                started.Add(await StartAsync(
                    new CloudMigrationStartRequest(
                        candidate.ProfileId,
                        candidate.LibraryName,
                        request.Direction),
                    cancellationToken).ConfigureAwait(false));
            }
            catch (InvalidOperationException ex)
            {
                skipped.Add(candidate with
                {
                    Eligible = false,
                    Code = ex.Message,
                });
            }
        }

        return new CloudMigrationBulkStartResponse(started, skipped);
    }

    public Task<CloudMigrationJob> RequestCutoverAsync(
        string jobId,
        CancellationToken cancellationToken = default)
        => QueueTransitionAsync(
            jobId,
            expectedPhase: CloudMigrationPhases.ReadyForCutover,
            nextPhase: CloudMigrationPhases.QueuedCutover,
            code: "cutover-queued",
            cancellationToken);

    public Task<CloudMigrationJob> RequestRollbackAsync(
        string jobId,
        CancellationToken cancellationToken = default)
        => QueueTransitionAsync(
            jobId,
            expectedPhase: CloudMigrationPhases.ReadyToFinalize,
            nextPhase: CloudMigrationPhases.QueuedRollback,
            code: "rollback-queued",
            cancellationToken);

    public async Task<CloudMigrationJob> RequestFinalizeAsync(
        string jobId,
        CloudMigrationFinalizeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var job = await _store.GetAsync(jobId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("migration-not-found");

        if (job.Phase != CloudMigrationPhases.ReadyToFinalize)
        {
            throw new InvalidOperationException("migration-not-ready-to-finalize");
        }

        if (string.IsNullOrWhiteSpace(job.BackupPath)
            || !PathsEqual(job.BackupPath, request.ConfirmationPath))
        {
            throw new InvalidOperationException("finalize-confirmation-mismatch");
        }

        if (job.Direction == CloudMigrationDirections.LocalToVfs
            && !string.Equals(job.VfsCertificationState, "passed", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("vfs-certification-required");
        }

        var queued = job with
        {
            Phase = CloudMigrationPhases.Finalizing,
            Code = "finalize-queued",
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await _store.SaveAsync(queued, cancellationToken).ConfigureAwait(false);
        if (!_queue.TryEnqueue(queued.Id))
        {
            throw new InvalidOperationException("migration-queue-full");
        }

        return queued;
    }

    private async Task<CloudMigrationJob> QueueTransitionAsync(
        string jobId,
        string expectedPhase,
        string nextPhase,
        string code,
        CancellationToken cancellationToken)
    {
        var job = await _store.GetAsync(jobId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("migration-not-found");
        if (job.Phase != expectedPhase)
        {
            throw new InvalidOperationException("migration-invalid-phase");
        }

        var queued = job with
        {
            Phase = nextPhase,
            Code = code,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await _store.SaveAsync(queued, cancellationToken).ConfigureAwait(false);
        if (!_queue.TryEnqueue(queued.Id))
        {
            throw new InvalidOperationException("migration-queue-full");
        }

        return queued;
    }

    private static CloudMigrationCandidate CandidateFor(
        MediaBrowser.Model.Entities.VirtualFolderInfo folder,
        PluginConfiguration config,
        IReadOnlyDictionary<string, CloudLibraryProfile[]> profileGroups,
        string direction)
    {
        if (folder.Locations.Length != 1)
        {
            return new CloudMigrationCandidate(
                folder.Name,
                folder.CollectionType?.ToString(),
                folder.Locations.FirstOrDefault() ?? string.Empty,
                null,
                false,
                "multiple-library-paths-not-yet-supported");
        }

        var path = folder.Locations[0];
        if (!profileGroups.TryGetValue(folder.Name, out var profiles)
            || profiles.Length == 0)
        {
            return new CloudMigrationCandidate(
                folder.Name,
                folder.CollectionType?.ToString(),
                path,
                null,
                false,
                "matching-vfs-profile-required");
        }

        if (profiles.Length != 1)
        {
            return new CloudMigrationCandidate(
                folder.Name,
                folder.CollectionType?.ToString(),
                path,
                null,
                false,
                "matching-vfs-profile-ambiguous");
        }

        var profile = profiles[0];
        if (direction == CloudMigrationDirections.LocalToVfs)
        {
            if (profile.Enabled)
            {
                return new CloudMigrationCandidate(
                    folder.Name,
                    folder.CollectionType?.ToString(),
                    path,
                    profile.Id,
                    false,
                    "profile-must-be-disabled-before-migration");
            }

            if (!Directory.Exists(path))
            {
                return new CloudMigrationCandidate(
                    folder.Name,
                    folder.CollectionType?.ToString(),
                    path,
                    profile.Id,
                    false,
                    "local-source-missing");
            }

            return new CloudMigrationCandidate(
                folder.Name,
                folder.CollectionType?.ToString(),
                path,
                profile.Id,
                true,
                "eligible");
        }

        var isVfs = profile.Enabled
            && RuntimeSettings.IsCloudProjectionPath(config, path);
        return new CloudMigrationCandidate(
            folder.Name,
            folder.CollectionType?.ToString(),
            path,
            profile.Id,
            isVfs,
            isVfs ? "eligible" : "library-not-vfs");
    }

    private static CloudLibraryProfile FindProfile(
        PluginConfiguration config,
        string profileId)
    {
        var matches = RuntimeSettings
            .GetCloudLibraries(config)
            .Where(profile => string.Equals(
                profile.Id,
                profileId?.Trim(),
                StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException("projection-not-found"),
            _ => throw new InvalidOperationException("projection-id-conflict"),
        };
    }

    private static void ValidateDirection(string direction)
    {
        if (direction is not CloudMigrationDirections.LocalToVfs
            and not CloudMigrationDirections.VfsToLocal)
        {
            throw new ArgumentException("Unsupported migration direction.", nameof(direction));
        }
    }

    private static bool ContainsReparsePoint(string root)
    {
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(root));

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
            }
        }

        return false;
    }

    private static bool IsTerminal(string phase)
        => phase is CloudMigrationPhases.Completed
            or CloudMigrationPhases.RolledBack
            or CloudMigrationPhases.Failed;

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }
}
