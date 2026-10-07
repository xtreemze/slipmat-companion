using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Configuration;
using Jellyfin.Plugin.AudioGateway.Models;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>
/// Serialized long-running migration executor. Transfer operations are additive and
/// restartable; authority changes occur only after explicit cutover requests.
/// </summary>
public sealed class CloudMigrationWorker : BackgroundService
{
    private const string VerificationContract =
        "rclone-check-download-content";

    private readonly CloudMigrationStore _store;
    private readonly CloudMigrationQueue _queue;
    private readonly IRcloneProcessRunner _runner;
    private readonly ICloudProjectionConfigurationStore _configurationStore;
    private readonly CloudProjectionService _projectionService;
    private readonly ICloudLibraryProjection _libraryProjection;
    private readonly ILibraryManager _library;
    private readonly ILogger<CloudMigrationWorker> _logger;

    public CloudMigrationWorker(
        CloudMigrationStore store,
        CloudMigrationQueue queue,
        IRcloneProcessRunner runner,
        ICloudProjectionConfigurationStore configurationStore,
        CloudProjectionService projectionService,
        ICloudLibraryProjection libraryProjection,
        ILibraryManager library,
        ILogger<CloudMigrationWorker> logger)
    {
        _store = store;
        _queue = queue;
        _runner = runner;
        _configurationStore = configurationStore;
        _projectionService = projectionService;
        _libraryProjection = libraryProjection;
        _library = library;
        _logger = logger;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        var jobs = await _store.LoadAllAsync(cancellationToken).ConfigureAwait(false);
        foreach (var job in jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (job.Phase)
            {
                case CloudMigrationPhases.QueuedCopy:
                    _queue.TryEnqueue(job.Id);
                    break;
                case CloudMigrationPhases.Copying:
                case CloudMigrationPhases.Verifying:
                    await SaveAsync(
                        job with
                        {
                            Phase = CloudMigrationPhases.QueuedCopy,
                            Code = "resuming-copy-after-restart",
                        },
                        cancellationToken).ConfigureAwait(false);
                    _queue.TryEnqueue(job.Id);
                    break;
                case CloudMigrationPhases.QueuedCutover:
                    _queue.TryEnqueue(job.Id);
                    break;
                case CloudMigrationPhases.FinalCopying:
                case CloudMigrationPhases.FinalVerifying:
                    await SaveAsync(
                        job with
                        {
                            Phase = CloudMigrationPhases.QueuedCutover,
                            Code = "resuming-final-verification-after-restart",
                        },
                        cancellationToken).ConfigureAwait(false);
                    _queue.TryEnqueue(job.Id);
                    break;
                case CloudMigrationPhases.CuttingOver:
                    await RecoverInterruptedCutoverAsync(job, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case CloudMigrationPhases.QueuedRollback:
                case CloudMigrationPhases.RollingBack:
                    await SaveAsync(
                        job with
                        {
                            Phase = CloudMigrationPhases.QueuedRollback,
                            Code = "resuming-rollback-after-restart",
                        },
                        cancellationToken).ConfigureAwait(false);
                    _queue.TryEnqueue(job.Id);
                    break;
                case CloudMigrationPhases.Finalizing:
                    _queue.TryEnqueue(job.Id);
                    break;
            }
        }

        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var jobId in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                var job = await _store.GetAsync(jobId, stoppingToken).ConfigureAwait(false);
                if (job is null)
                {
                    continue;
                }

                switch (job.Phase)
                {
                    case CloudMigrationPhases.QueuedCopy:
                        await ExecuteInitialCopyAsync(job, stoppingToken).ConfigureAwait(false);
                        break;
                    case CloudMigrationPhases.QueuedCutover:
                        await ExecuteCutoverAsync(job, stoppingToken).ConfigureAwait(false);
                        break;
                    case CloudMigrationPhases.QueuedRollback:
                        await ExecuteRollbackAsync(job, stoppingToken).ConfigureAwait(false);
                        break;
                    case CloudMigrationPhases.Finalizing:
                        await ExecuteFinalizeAsync(job, stoppingToken).ConfigureAwait(false);
                        break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cloud migration job {JobId} failed unexpectedly", jobId);
                var job = await _store.GetAsync(jobId, CancellationToken.None).ConfigureAwait(false);
                if (job is not null)
                {
                    await SaveAsync(
                        job with
                        {
                            Phase = CloudMigrationPhases.Failed,
                            Code = "unexpected-worker-failure",
                        },
                        CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                _queue.Complete(jobId);
            }
        }
    }

    private async Task ExecuteInitialCopyAsync(
        CloudMigrationJob job,
        CancellationToken cancellationToken)
    {
        var cli = new RcloneCliHost(runner: _runner);
        var copying = await SaveAsync(
            job with
            {
                Phase = CloudMigrationPhases.Copying,
                Code = "copying",
            },
            cancellationToken).ConfigureAwait(false);

        RcloneCommandResult copy;
        string verifyPath;
        if (job.Direction == CloudMigrationDirections.LocalToVfs)
        {
            verifyPath = job.LocalPath;
            copy = await cli.CopyLocalToRemoteAsync(
                    job.LocalPath,
                    job.RemoteName,
                    job.RemotePath,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            verifyPath = job.StagingPath
                ?? throw new InvalidOperationException("Reverse migration staging path is missing.");
            copy = await cli.CopyRemoteToLocalAsync(
                    job.RemoteName,
                    job.RemotePath,
                    verifyPath,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var copyError = TransferFailure(copy, "copy");
        if (copyError is not null)
        {
            await FailAsync(copying, copyError, cancellationToken).ConfigureAwait(false);
            return;
        }

        var verifying = await SaveAsync(
            copying with
            {
                Phase = CloudMigrationPhases.Verifying,
                Code = "verifying",
            },
            cancellationToken).ConfigureAwait(false);
        var check = await cli.CheckLocalAndRemoteByDownloadAsync(
                verifyPath,
                job.RemoteName,
                job.RemotePath,
                cancellationToken)
            .ConfigureAwait(false);
        var checkError = TransferFailure(check, "verification");
        if (checkError is not null)
        {
            await FailAsync(verifying, checkError, cancellationToken).ConfigureAwait(false);
            return;
        }

        await SaveAsync(
            verifying with
            {
                Phase = CloudMigrationPhases.ReadyForCutover,
                Code = "verified-ready-for-cutover",
                Verification = VerificationContract,
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task ExecuteCutoverAsync(
        CloudMigrationJob job,
        CancellationToken cancellationToken)
    {
        if (_library.IsScanRunning)
        {
            await SaveAsync(
                job with
                {
                    Phase = CloudMigrationPhases.ReadyForCutover,
                    Code = "jellyfin-scan-active-retry-cutover",
                },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var cli = new RcloneCliHost(runner: _runner);
        var finalCopying = await SaveAsync(
            job with
            {
                Phase = CloudMigrationPhases.FinalCopying,
                Code = "final-incremental-copy",
            },
            cancellationToken).ConfigureAwait(false);

        var verifyPath = job.Direction == CloudMigrationDirections.LocalToVfs
            ? job.LocalPath
            : job.StagingPath
                ?? throw new InvalidOperationException("Reverse migration staging path is missing.");

        var copy = job.Direction == CloudMigrationDirections.LocalToVfs
            ? await cli.CopyLocalToRemoteAsync(
                    job.LocalPath,
                    job.RemoteName,
                    job.RemotePath,
                    cancellationToken)
                .ConfigureAwait(false)
            : await cli.CopyRemoteToLocalAsync(
                    job.RemoteName,
                    job.RemotePath,
                    verifyPath,
                    cancellationToken)
                .ConfigureAwait(false);

        var copyError = TransferFailure(copy, "final-copy");
        if (copyError is not null)
        {
            await SaveAsync(
                finalCopying with
                {
                    Phase = CloudMigrationPhases.ReadyForCutover,
                    Code = copyError,
                },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var finalVerifying = await SaveAsync(
            finalCopying with
            {
                Phase = CloudMigrationPhases.FinalVerifying,
                Code = "final-verification",
            },
            cancellationToken).ConfigureAwait(false);
        var check = await cli.CheckLocalAndRemoteByDownloadAsync(
                verifyPath,
                job.RemoteName,
                job.RemotePath,
                cancellationToken)
            .ConfigureAwait(false);
        var checkError = TransferFailure(check, "final-verification");
        if (checkError is not null)
        {
            await SaveAsync(
                finalVerifying with
                {
                    Phase = CloudMigrationPhases.ReadyForCutover,
                    Code = checkError,
                },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (job.Direction == CloudMigrationDirections.LocalToVfs)
        {
            var currentManifest = await CloudMigrationManifestBuilder
                .CaptureAsync(job.LocalPath, cancellationToken)
                .ConfigureAwait(false);
            if (!ManifestMatches(job, currentManifest))
            {
                await SaveAsync(
                    finalVerifying with
                    {
                        Phase = CloudMigrationPhases.ReadyForCutover,
                        Code = "source-changed-after-inventory",
                        Verification = null,
                    },
                    cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        if (_library.IsScanRunning)
        {
            await SaveAsync(
                finalVerifying with
                {
                    Phase = CloudMigrationPhases.ReadyForCutover,
                    Code = "jellyfin-scan-started-retry-cutover",
                    Verification = VerificationContract,
                },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (job.Direction == CloudMigrationDirections.LocalToVfs)
        {
            await CutoverLocalToVfsAsync(
                finalVerifying with { Verification = VerificationContract },
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await CutoverVfsToLocalAsync(
                finalVerifying with { Verification = VerificationContract },
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CutoverLocalToVfsAsync(
        CloudMigrationJob job,
        CancellationToken cancellationToken)
    {
        var backupPath = string.Concat(
            job.LocalPath,
            ".slipmat-rollback-",
            job.Id[..12]);
        if (Directory.Exists(backupPath) || File.Exists(backupPath))
        {
            await SaveAsync(
                job with
                {
                    Phase = CloudMigrationPhases.ReadyForCutover,
                    Code = "rollback-backup-path-exists",
                },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var cutting = await SaveAsync(
            job with
            {
                Phase = CloudMigrationPhases.CuttingOver,
                Code = "cutting-over-to-vfs",
                BackupPath = backupPath,
            },
            cancellationToken).ConfigureAwait(false);

        try
        {
            Directory.Move(job.LocalPath, backupPath);
            SetProfileState(
                job.ProfileId,
                enabled: true,
                projectionPath: job.LocalPath);

            var result = await _projectionService
                .ReconcileAsync(job.ProfileId, cancellationToken)
                .ConfigureAwait(false);
            if (!result.MountReady || !result.LibraryReady)
            {
                await RestoreLocalSourceAfterFailedCutoverAsync(
                    cutting,
                    string.Concat("cutover-", result.Code),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            await SaveAsync(
                cutting with
                {
                    Phase = CloudMigrationPhases.ReadyToFinalize,
                    Code = "vfs-active-local-backup-retained-uncertified",
                    VfsCertificationState = "observing",
                    VfsCertificationCode = "mount-and-library-ready",
                    VfsCertifiedAt = null,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "VFS cutover failed for migration {JobId}", job.Id);
            await RestoreLocalSourceAfterFailedCutoverAsync(
                cutting,
                "cutover-local-filesystem-failed",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CutoverVfsToLocalAsync(
        CloudMigrationJob job,
        CancellationToken cancellationToken)
    {
        var stagingPath = job.StagingPath
            ?? throw new InvalidOperationException("Reverse migration staging path is missing.");

        var cutting = await SaveAsync(
            job with
            {
                Phase = CloudMigrationPhases.CuttingOver,
                Code = "cutting-over-to-local",
            },
            cancellationToken).ConfigureAwait(false);

        try
        {
            SetProfileState(job.ProfileId, enabled: false, projectionPath: job.LocalPath);
            var unmountCode = await _projectionService
                .UnmountAsync(job.ProfileId, cancellationToken)
                .ConfigureAwait(false);
            if (unmountCode != "unmounted")
            {
                SetProfileState(job.ProfileId, enabled: true, projectionPath: job.LocalPath);
                await SaveAsync(
                    cutting with
                    {
                        Phase = CloudMigrationPhases.ReadyForCutover,
                        Code = unmountCode,
                    },
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!TryDeleteEmptyDirectory(job.LocalPath))
            {
                SetProfileState(job.ProfileId, enabled: true, projectionPath: job.LocalPath);
                await _projectionService
                    .ReconcileAsync(job.ProfileId, cancellationToken)
                    .ConfigureAwait(false);
                await SaveAsync(
                    cutting with
                    {
                        Phase = CloudMigrationPhases.ReadyForCutover,
                        Code = "unmounted-path-not-empty",
                    },
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            Directory.Move(stagingPath, job.LocalPath);
            if (!RestorePreviousLibraryScanPolicy(job))
            {
                await SaveAsync(
                    cutting with
                    {
                        Phase = CloudMigrationPhases.Failed,
                        Code = "local-restored-scan-policy-restore-failed",
                    },
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            _library.QueueLibraryScan();

            await SaveAsync(
                cutting with
                {
                    Phase = CloudMigrationPhases.Completed,
                    Code = "local-restored-cloud-retained",
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Local restoration failed for migration {JobId}", job.Id);
            if (!Directory.Exists(job.LocalPath) && Directory.Exists(stagingPath))
            {
                try
                {
                    Directory.Move(stagingPath, job.LocalPath);
                }
                catch
                {
                }
            }

            await SaveAsync(
                cutting with
                {
                    Phase = CloudMigrationPhases.Failed,
                    Code = "local-cutover-failed-cloud-retained",
                },
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ExecuteRollbackAsync(
        CloudMigrationJob job,
        CancellationToken cancellationToken)
    {
        if (job.Direction != CloudMigrationDirections.LocalToVfs
            || string.IsNullOrWhiteSpace(job.BackupPath)
            || !Directory.Exists(job.BackupPath))
        {
            await SaveAsync(
                job with
                {
                    Phase = CloudMigrationPhases.ReadyToFinalize,
                    Code = "rollback-backup-missing",
                },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var rolling = await SaveAsync(
            job with
            {
                Phase = CloudMigrationPhases.RollingBack,
                Code = "rolling-back",
            },
            cancellationToken).ConfigureAwait(false);

        SetProfileState(job.ProfileId, enabled: false, projectionPath: job.LocalPath);
        var unmountCode = await _projectionService
            .UnmountAsync(job.ProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (unmountCode != "unmounted")
        {
            await SaveAsync(
                rolling with
                {
                    Phase = CloudMigrationPhases.ReadyToFinalize,
                    Code = unmountCode,
                },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!TryDeleteEmptyDirectory(job.LocalPath))
        {
            await SaveAsync(
                rolling with
                {
                    Phase = CloudMigrationPhases.ReadyToFinalize,
                    Code = "unmounted-path-not-empty",
                },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            Directory.Move(job.BackupPath, job.LocalPath);
            RestoreProfileState(job);
            if (!RestorePreviousLibraryScanPolicy(job))
            {
                await SaveAsync(
                    rolling with
                    {
                        Phase = CloudMigrationPhases.Failed,
                        Code = "local-restored-scan-policy-restore-failed",
                    },
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            _library.QueueLibraryScan();
            await SaveAsync(
                rolling with
                {
                    Phase = CloudMigrationPhases.RolledBack,
                    Code = "local-source-restored-cloud-retained",
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Rollback restore failed for migration {JobId}", job.Id);
            await SaveAsync(
                rolling with
                {
                    Phase = CloudMigrationPhases.ReadyToFinalize,
                    Code = "rollback-restore-failed",
                },
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ExecuteFinalizeAsync(
        CloudMigrationJob job,
        CancellationToken cancellationToken)
    {
        if (job.Direction != CloudMigrationDirections.LocalToVfs
            || string.IsNullOrWhiteSpace(job.BackupPath))
        {
            await FailAsync(job, "finalize-backup-missing", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        try
        {
            if (Directory.Exists(job.BackupPath))
            {
                Directory.Delete(job.BackupPath, recursive: true);
            }

            await SaveAsync(
                job with
                {
                    Phase = CloudMigrationPhases.Completed,
                    Code = "cloud-vfs-authoritative-local-backup-removed",
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Final local cleanup failed for migration {JobId}", job.Id);
            await SaveAsync(
                job with
                {
                    Phase = CloudMigrationPhases.ReadyToFinalize,
                    Code = "finalize-local-delete-failed",
                },
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RecoverInterruptedCutoverAsync(
        CloudMigrationJob job,
        CancellationToken cancellationToken)
    {
        if (job.Direction == CloudMigrationDirections.LocalToVfs)
        {
            if (!string.IsNullOrWhiteSpace(job.BackupPath)
                && Directory.Exists(job.BackupPath))
            {
                var profile = FindProfile(job.ProfileId);
                if (profile.Enabled
                    && PathsEqual(profile.ProjectionPath, job.LocalPath))
                {
                    var result = await _projectionService
                        .ReconcileAsync(job.ProfileId, cancellationToken)
                        .ConfigureAwait(false);
                    if (result.MountReady && result.LibraryReady)
                    {
                        await SaveAsync(
                            job with
                            {
                                Phase = CloudMigrationPhases.ReadyToFinalize,
                                Code = "recovered-vfs-cutover-after-restart",
                            },
                            cancellationToken).ConfigureAwait(false);
                        return;
                    }
                }

                await RestoreLocalSourceAfterFailedCutoverAsync(
                    job,
                    "recovered-local-source-after-restart",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            await SaveAsync(
                job with
                {
                    Phase = CloudMigrationPhases.ReadyForCutover,
                    Code = "cutover-not-started-after-restart",
                },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var reverseProfile = FindProfile(job.ProfileId);
        if (reverseProfile.Enabled)
        {
            await SaveAsync(
                job with
                {
                    Phase = CloudMigrationPhases.ReadyForCutover,
                    Code = "reverse-cutover-not-started-after-restart",
                },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var staging = job.StagingPath;
        if (!string.IsNullOrWhiteSpace(staging) && Directory.Exists(staging))
        {
            var unmountCode = await _projectionService
                .UnmountAsync(job.ProfileId, cancellationToken)
                .ConfigureAwait(false);
            if (unmountCode == "unmounted"
                && TryDeleteEmptyDirectory(job.LocalPath))
            {
                Directory.Move(staging, job.LocalPath);
                if (!RestorePreviousLibraryScanPolicy(job))
                {
                    await SaveAsync(
                        job with
                        {
                            Phase = CloudMigrationPhases.Failed,
                            Code = "local-restored-scan-policy-restore-failed",
                        },
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

                _library.QueueLibraryScan();
                await SaveAsync(
                    job with
                    {
                        Phase = CloudMigrationPhases.Completed,
                        Code = "recovered-local-cutover-after-restart",
                    },
                    cancellationToken).ConfigureAwait(false);
                return;
            }
        }
        else if (Directory.Exists(job.LocalPath))
        {
            if (!RestorePreviousLibraryScanPolicy(job))
            {
                await SaveAsync(
                    job with
                    {
                        Phase = CloudMigrationPhases.Failed,
                        Code = "local-restored-scan-policy-restore-failed",
                    },
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            await SaveAsync(
                job with
                {
                    Phase = CloudMigrationPhases.Completed,
                    Code = "recovered-local-cutover-after-restart",
                },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await SaveAsync(
            job with
            {
                Phase = CloudMigrationPhases.Failed,
                Code = "reverse-cutover-recovery-required",
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task RestoreLocalSourceAfterFailedCutoverAsync(
        CloudMigrationJob job,
        string code,
        CancellationToken cancellationToken)
    {
        SetProfileState(job.ProfileId, enabled: false, projectionPath: job.LocalPath);
        try
        {
            await _projectionService
                .UnmountAsync(job.ProfileId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
        }

        try
        {
            if (Directory.Exists(job.LocalPath))
            {
                if (Directory.EnumerateFileSystemEntries(job.LocalPath).Any())
                {
                    await SaveAsync(
                        job with
                        {
                            Phase = CloudMigrationPhases.Failed,
                            Code = "cutover-recovery-mount-still-active",
                        },
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

                Directory.Delete(job.LocalPath);
            }

            if (!string.IsNullOrWhiteSpace(job.BackupPath)
                && Directory.Exists(job.BackupPath))
            {
                Directory.Move(job.BackupPath, job.LocalPath);
            }

            RestoreProfileState(job);
            if (!RestorePreviousLibraryScanPolicy(job))
            {
                code = "cutover-recovery-scan-policy-restore-failed";
            }

            await SaveAsync(
                job with
                {
                    Phase = CloudMigrationPhases.Failed,
                    Code = code,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Automatic cutover recovery failed for migration {JobId}", job.Id);
            await SaveAsync(
                job with
                {
                    Phase = CloudMigrationPhases.Failed,
                    Code = "cutover-recovery-required",
                },
                cancellationToken).ConfigureAwait(false);
        }
    }

    private void SetProfileState(
        string profileId,
        bool enabled,
        string projectionPath)
    {
        var config = _configurationStore.GetCurrent();
        var profiles = RuntimeSettings.GetCloudLibraries(config).ToList();
        var profile = profiles.Single(candidate => string.Equals(
            candidate.Id,
            profileId,
            StringComparison.OrdinalIgnoreCase));
        profile.Enabled = enabled;
        profile.ProjectionPath = projectionPath;
        RuntimeSettings.ApplyCloudLibraries(config, profiles);
        _configurationStore.Save(config);
    }

    private void RestoreProfileState(CloudMigrationJob job)
        => SetProfileState(
            job.ProfileId,
            job.PreviousEnabled,
            job.PreviousProjectionPath);

    private CloudLibraryProfile FindProfile(string profileId)
        => RuntimeSettings
            .GetCloudLibraries(_configurationStore.GetCurrent())
            .Single(profile => string.Equals(
                profile.Id,
                profileId,
                StringComparison.OrdinalIgnoreCase));

    private bool RestorePreviousLibraryScanPolicy(CloudMigrationJob job)
    {
        if (job.PreviousLibraryScanPolicy is null)
        {
            return true;
        }

        return _libraryProjection.RestoreScanPolicy(
            job.LibraryName,
            job.LocalPath,
            job.PreviousLibraryScanPolicy);
    }

    private async Task<CloudMigrationJob> SaveAsync(
        CloudMigrationJob job,
        CancellationToken cancellationToken)
    {
        var updated = job with { UpdatedAt = DateTimeOffset.UtcNow };
        await _store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    private async Task FailAsync(
        CloudMigrationJob job,
        string code,
        CancellationToken cancellationToken)
        => await SaveAsync(
            job with
            {
                Phase = CloudMigrationPhases.Failed,
                Code = code,
            },
            cancellationToken).ConfigureAwait(false);

    private static bool ManifestMatches(
        CloudMigrationJob job,
        CloudMigrationManifest manifest)
        => job.ManifestVersion == manifest.Version
            && job.ManifestFileCount == manifest.FileCount
            && job.ManifestTotalBytes == manifest.TotalBytes
            && !string.IsNullOrWhiteSpace(job.ManifestDigestSha256)
            && string.Equals(
                job.ManifestDigestSha256,
                manifest.DigestSha256,
                StringComparison.OrdinalIgnoreCase);

    private static string? TransferFailure(
        RcloneCommandResult result,
        string operation)
    {
        if (!result.StartSucceeded)
        {
            return "rclone-unavailable";
        }

        if (result.TimedOut)
        {
            return string.Concat(operation, "-timeout");
        }

        if (result.ExitCode != 0)
        {
            return RcloneCliHost.ClassifyRemoteFailure(result)
                ?? string.Concat(operation, "-failed");
        }

        return null;
    }

    private static bool TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return true;
            }

            if (Directory.EnumerateFileSystemEntries(path).Any())
            {
                return false;
            }

            Directory.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

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
