using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;

namespace Slipmat.MaintenanceRunner;

/// <summary>Whole-job transitions after independently observing the stable host state.</summary>
public static class MaintenanceCompletion
{
    public static async Task<CloudMigrationJournalSnapshot> CompleteAsync(CloudMigrationJournalSnapshot snapshot, IMaintenanceHost host,
        Func<CloudMigrationJournalSnapshot, CloudMigrationJob, CancellationToken, Task<CloudMigrationJournalSnapshot>> compareExchange, CancellationToken ct)
    {
        var current = new MaintenanceSnapshot(snapshot.Job, snapshot.Revision);
        var intent = snapshot.Job.Maintenance ?? throw new InvalidOperationException("maintenance-intent-missing");
        await RequireStableAsync(current, host, ct).ConfigureAwait(false);
        CloudMigrationJob replacement;
        if (intent.Direction == CloudMaintenanceDirection.Promote && snapshot.Job.Phase == CloudMigrationPhases.ReadyForCutover)
            replacement = snapshot.Job with
            {
                Phase = CloudMigrationPhases.ReadyToFinalize,
                BackupPath = intent.Custody.BackupPath,
                Code = "vfs-active-local-backup-retained-uncertified",
                VfsCertificationState = "observing",
                VfsCertificationCode = "offline-mount-ready",
                VfsCertifiedAt = null,
                UpdatedAt = DateTimeOffset.UtcNow
            };
        else if (intent.Direction == CloudMaintenanceDirection.Rollback && snapshot.Job.Phase == CloudMigrationPhases.ReadyToFinalize)
            replacement = snapshot.Job with
            {
                Phase = CloudMigrationPhases.RolledBack,
                BackupPath = null,
                Code = "rollback-complete",
                VfsCertificationState = "uncertified",
                VfsCertificationCode = null,
                VfsCertifiedAt = null,
                UpdatedAt = DateTimeOffset.UtcNow
            };
        else if ((intent.Direction == CloudMaintenanceDirection.Promote && snapshot.Job.Phase == CloudMigrationPhases.ReadyToFinalize)
            || (intent.Direction == CloudMaintenanceDirection.Rollback && snapshot.Job.Phase == CloudMigrationPhases.RolledBack))
            return snapshot;
        else throw new InvalidOperationException("maintenance-completion-phase-invalid");
        return await compareExchange(snapshot, replacement, ct).ConfigureAwait(false);
    }

    public static async Task<CloudMigrationJournalSnapshot> BeginRollbackAsync(CloudMigrationJournalSnapshot snapshot, IMaintenanceHost host,
        Func<CloudMigrationJournalSnapshot, CloudMigrationJob, CancellationToken, Task<CloudMigrationJournalSnapshot>> compareExchange, CancellationToken ct)
    {
        var intent = snapshot.Job.Maintenance ?? throw new InvalidOperationException("maintenance-intent-missing");
        if (snapshot.Job.Phase != CloudMigrationPhases.ReadyToFinalize || intent.Direction != CloudMaintenanceDirection.Promote
            || intent.Phase != CloudMaintenancePhase.Stable || string.IsNullOrWhiteSpace(intent.MountIdentity))
            throw new InvalidOperationException("rollback-not-ready");
        await RequireStableAsync(new MaintenanceSnapshot(snapshot.Job, snapshot.Revision), host, ct).ConfigureAwait(false);
        var rollback = intent with { Direction = CloudMaintenanceDirection.Rollback, Phase = CloudMaintenancePhase.Prepared };
        return await compareExchange(snapshot, snapshot.Job with { Maintenance = rollback, UpdatedAt = DateTimeOffset.UtcNow }, ct).ConfigureAwait(false);
    }

    private static async Task RequireStableAsync(MaintenanceSnapshot snapshot, IMaintenanceHost host, CancellationToken ct)
    {
        var intent = snapshot.Intent;
        if (intent.Phase != CloudMaintenancePhase.Stable || !LeasedOperations.MatchesAdmittedJob(snapshot, intent))
            throw new InvalidOperationException("maintenance-not-stable");
        var observation = await host.ObserveAsync(snapshot, ct).ConfigureAwait(false);
        if (CloudMaintenanceTransaction.Decide(intent, observation).Action != CloudMaintenanceAction.Complete)
            throw new InvalidOperationException("maintenance-not-stable");
        ct.ThrowIfCancellationRequested();
    }
}
