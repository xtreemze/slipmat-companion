using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;
using Slipmat.MaintenanceRunner;

internal static class MaintenanceCompletionTests
{
    public static async Task Run(CloudMigrationJob job)
    {
        var intent = job.Maintenance! with { Direction = CloudMaintenanceDirection.Promote, Phase = CloudMaintenancePhase.Stable, MountIdentity = "mount" };
        var initial = new CloudMigrationJournalSnapshot(job with { Maintenance = intent, BackupPath = null }, "revision");
        var host = new Host(); var saves = 0;
        Task<CloudMigrationJournalSnapshot> Save(CloudMigrationJournalSnapshot expected, CloudMigrationJob next, CancellationToken ct)
        { saves++; return Task.FromResult(new CloudMigrationJournalSnapshot(next, expected.Revision + "x")); }
        var active = await MaintenanceCompletion.CompleteAsync(initial, host, Save, default);
        if (active.Job.Phase != CloudMigrationPhases.ReadyToFinalize || active.Job.BackupPath != intent.Custody.BackupPath
            || active.Job.VfsCertificationState != "observing" || active.Job.VfsCertificationCode != "offline-mount-ready" || saves != 1)
            throw new Exception("promotion completion wrong");
        var rollback = await MaintenanceCompletion.BeginRollbackAsync(active, host, Save, default);
        if (rollback.Job.Maintenance != intent with { Direction = CloudMaintenanceDirection.Rollback, Phase = CloudMaintenancePhase.Prepared })
            throw new Exception("rollback changed custody");
        var stableRollback = rollback with { Job = rollback.Job with { Maintenance = rollback.Job.Maintenance! with { Phase = CloudMaintenancePhase.Stable } } };
        host.Observation = new(true, true, CloudMaintenancePathState.Expected, CloudMaintenancePathState.Missing, CloudMaintenanceMountState.Missing, true, true);
        var finished = await MaintenanceCompletion.CompleteAsync(stableRollback, host, Save, default);
        if (finished.Job.Phase != CloudMigrationPhases.RolledBack || finished.Job.BackupPath != null || finished.Job.Maintenance?.Phase != CloudMaintenancePhase.Stable)
            throw new Exception("rollback completion wrong");
        host.Observation = host.Observation with { Source = CloudMaintenancePathState.Other };
        var count = saves;
        try { await MaintenanceCompletion.CompleteAsync(stableRollback, host, Save, default); throw new Exception("changed source accepted"); }
        catch (InvalidOperationException ex) when (ex.Message == "maintenance-not-stable") { }
        try { await MaintenanceCompletion.BeginRollbackAsync(active with { Job = active.Job with { BackupPath = "/foreign" } }, host, Save, default); throw new Exception("wrong backup accepted"); }
        catch (InvalidOperationException ex) when (ex.Message == "maintenance-not-stable") { }
        if (saves != count) throw new Exception("blocked operation saved");
        host.Observation = new(true, true, CloudMaintenancePathState.MountPoint, CloudMaintenancePathState.Expected, CloudMaintenanceMountState.Expected, true, true);
        try { await MaintenanceCompletion.CompleteAsync(initial, host, (_, _, _) => throw new InvalidOperationException("migration-journal-revision-changed"), default); throw new Exception("stale CAS accepted"); }
        catch (InvalidOperationException ex) when (ex.Message == "migration-journal-revision-changed") { }
    }
    private sealed class Host : IMaintenanceHost
    {
        public CloudMaintenanceObservation Observation = new(true, true, CloudMaintenancePathState.MountPoint, CloudMaintenancePathState.Expected, CloudMaintenanceMountState.Expected, true, true);
        public Task<CloudMaintenanceObservation> ObserveAsync(MaintenanceSnapshot snapshot, CancellationToken ct) => Task.FromResult(Observation);
        public Task<string?> ExecuteGuardedAsync(MaintenanceSnapshot snapshot, CloudMaintenanceAction action, CancellationToken ct) => throw new Exception("unexpected effect");
    }
}
