using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;

namespace Slipmat.MaintenanceRunner;

/// <summary>Read-only startup admission. Incomplete transactions require an offline runner.</summary>
public static class StartupGuard
{
    public static async Task<bool> IsStableAsync(MaintenanceSnapshot snapshot, HostSpecification spec,
        string specDigest, IMaintenanceHost host, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(host);
        var intent = snapshot.Job.Maintenance;
        if (intent is null)
        {
            var admission = snapshot.Job.MaintenanceAdmission;
            if (admission is null || !HostSpecification.IsValidHash(specDigest)) return false;
            var custody = new CloudMaintenanceCustody(admission.LibraryId.ToString(), admission.LibraryName,
                admission.LocalPath, spec.BackupPath, admission.ProfileId, admission.RemoteName,
                admission.RemotePath, admission.ManifestDigestSha256, admission.Verification, specDigest);
            // Treat the untouched state as a completed rollback solely for the read-only planner.
            intent = new CloudMaintenanceIntent(snapshot.Job.Id, CloudMaintenanceDirection.Rollback,
                CloudMaintenancePhase.Stable, "startup-local-observation", null, custody);
            snapshot = snapshot with { Job = snapshot.Job with { Maintenance = intent, Phase = CloudMigrationPhases.RolledBack, BackupPath = null } };
        }
        else if (!((intent.Direction == CloudMaintenanceDirection.Promote
                    && snapshot.Job.Phase == CloudMigrationPhases.ReadyToFinalize
                    && snapshot.Job.BackupPath == intent.Custody.BackupPath)
                || (intent.Direction == CloudMaintenanceDirection.Rollback
                    && snapshot.Job.Phase == CloudMigrationPhases.RolledBack && snapshot.Job.BackupPath is null)))
        {
            return false;
        }
        if (intent.Phase != CloudMaintenancePhase.Stable || !LeasedOperations.MatchesAdmittedJob(snapshot, intent))
            return false;
        var observation = await host.ObserveAsync(snapshot, cancellationToken).ConfigureAwait(false);
        return CloudMaintenanceTransaction.Decide(intent, observation).Action == CloudMaintenanceAction.Complete;
    }
}
