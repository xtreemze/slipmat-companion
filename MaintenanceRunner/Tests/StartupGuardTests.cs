using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;
using Slipmat.MaintenanceRunner;

internal static class StartupGuardTests
{
    public static async Task Run(HostSpecification spec, CloudMigrationJob job)
    {
        var host = new SpyHost();
        var pending = new MaintenanceSnapshot(job, "revision");
        if (await StartupGuard.IsStableAsync(pending, spec, new string('a', 64), host, default))
            throw new Exception("incomplete startup admitted");
        if (host.Observations != 0 || host.Effects != 0) throw new Exception("incomplete startup accessed host");

        var stableIntent = job.Maintenance! with { Direction = CloudMaintenanceDirection.Rollback, Phase = CloudMaintenancePhase.Stable };
        var stable = pending with { Job = job with { Maintenance = stableIntent, Phase = CloudMigrationPhases.RolledBack, BackupPath = null } };
        host.Observation = new(true, true, CloudMaintenancePathState.Expected, CloudMaintenancePathState.Missing, CloudMaintenanceMountState.Missing, true, true);
        if (!await StartupGuard.IsStableAsync(stable, spec, new string('a', 64), host, default))
            throw new Exception("stable local startup rejected");
        var mountedIntent = stableIntent with { Direction = CloudMaintenanceDirection.Promote, MountIdentity = "recorded-mount" };
        var unfinalized = pending with { Job = job with { Maintenance = mountedIntent, Phase = CloudMigrationPhases.ReadyForCutover, BackupPath = null } };
        var observedBefore = host.Observations;
        if (await StartupGuard.IsStableAsync(unfinalized, spec, new string('a', 64), host, default)
            || host.Observations != observedBefore || host.Effects != 0)
            throw new Exception("unfinalized promotion startup accepted or mutated");
        var mounted = pending with { Job = job with { Maintenance = mountedIntent, Phase = CloudMigrationPhases.ReadyToFinalize, BackupPath = mountedIntent.Custody.BackupPath } };
        host.Observation = new(true, true, CloudMaintenancePathState.MountPoint, CloudMaintenancePathState.Expected, CloudMaintenanceMountState.Expected, true, true);
        if (!await StartupGuard.IsStableAsync(mounted, spec, new string('a', 64), host, default))
            throw new Exception("stable mounted startup rejected");
        host.Observation = host.Observation with { Mount = CloudMaintenanceMountState.Other };
        if (await StartupGuard.IsStableAsync(mounted, spec, new string('a', 64), host, default))
            throw new Exception("changed mount accepted");
        host.Observation = new(true, true, CloudMaintenancePathState.Expected, CloudMaintenancePathState.Missing, CloudMaintenanceMountState.Missing, true, true);
        host.Observation = host.Observation with { PolicyMatchesTarget = false };
        if (await StartupGuard.IsStableAsync(stable, spec, new string('a', 64), host, default))
            throw new Exception("configuration drift accepted");
        host.Observation = host.Observation with { PolicyMatchesTarget = true };
        var noIntent = pending with { Job = job with { Maintenance = null } };
        if (!await StartupGuard.IsStableAsync(noIntent, spec, new string('a', 64), host, default))
            throw new Exception("untouched local startup rejected");
        host.Observation = host.Observation with { Source = CloudMaintenancePathState.Other };
        if (await StartupGuard.IsStableAsync(noIntent, spec, new string('a', 64), host, default))
            throw new Exception("changed local source accepted");
        if (host.Effects != 0) throw new Exception("startup performed an effect");
    }

    private sealed class SpyHost : IMaintenanceHost
    {
        public int Observations;
        public int Effects;
        public CloudMaintenanceObservation Observation = new(false, false, CloudMaintenancePathState.Other, CloudMaintenancePathState.Other, CloudMaintenanceMountState.Other, false, false);
        public Task<CloudMaintenanceObservation> ObserveAsync(MaintenanceSnapshot snapshot, CancellationToken ct) { Observations++; return Task.FromResult(Observation); }
        public Task<string?> ExecuteGuardedAsync(MaintenanceSnapshot snapshot, CloudMaintenanceAction action, CancellationToken ct) { Effects++; throw new Exception("startup mutated host"); }
    }
}
