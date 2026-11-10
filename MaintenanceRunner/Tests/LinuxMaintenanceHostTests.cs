using System.Security.Cryptography;
using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;
using Slipmat.MaintenanceRunner;

internal static class LinuxMaintenanceHostTests
{
    public static async Task Run(HostSpecification basis, CloudMaintenanceAdmission admitted, CloudMigrationJob admittedJob)
    {
        if (!OperatingSystem.IsLinux() || Environment.UserName != "root") return;

        var root = Path.Combine("/srv", "maintenance-host-observe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var localPath = Path.Combine(root, "music");
            var backupPath = Path.Combine(root, "music.backup");
            Directory.CreateDirectory(localPath);
            var parentIdentity = LinuxTrustedFile.InspectDirectory(root);
            var sourceIdentity = LinuxTrustedFile.InspectDirectory(localPath);
            var profilePath = Path.Combine(root, "profile.xml");
            var policyPath = Path.Combine(root, "policy.xml");
            var profileBytes = System.Text.Encoding.UTF8.GetBytes("<PluginConfiguration><CloudLibraries/><CloudProjectionEnabled>false</CloudProjectionEnabled></PluginConfiguration>");
            var policyBytes = System.Text.Encoding.UTF8.GetBytes("<LibraryOptions/>");
            LinuxTrustedFile.CreateNew(profilePath, profileBytes);
            LinuxTrustedFile.CreateNew(policyPath, policyBytes);
            var profileHash = Convert.ToHexString(SHA256.HashData(profileBytes));
            var policyHash = Convert.ToHexString(SHA256.HashData(policyBytes));
            var admission = admitted with
            {
                LocalPath = localPath,
                PluginConfigurationPath = profilePath,
                OptionsPath = policyPath,
                PluginConfigurationSha256 = profileHash,
                OptionsSha256 = policyHash,
            };
            var admittedIntent = admittedJob.Maintenance ?? throw new Exception("test migration job has no maintenance intent");
            var intent = admittedIntent with
            {
                Direction = CloudMaintenanceDirection.Rollback,
                Phase = CloudMaintenancePhase.ConfigIntent,
                MountIdentity = null,
                Custody = admittedIntent.Custody with { LocalPath = localPath, BackupPath = backupPath },
            };
            var job = admittedJob with
            {
                LocalPath = localPath,
                Maintenance = intent,
                MaintenanceAdmission = admission,
            };
            var snapshot = new MaintenanceSnapshot(job, "host-observation-rev1");
            var spec = basis with
            {
                Parent = parentIdentity,
                Source = sourceIdentity,
                BackupPath = backupPath,
                Profile = new(profilePath, Path.Combine(root, "profile-original.xml"), profileHash, new string('f', 64)),
                Policy = new(policyPath, Path.Combine(root, "policy-original.xml"), policyHash, new string('e', 64)),
            };
            var barrierState = new LinuxBarrierSnapshot(true, true, "inactive", 0, 0, [], spec.BootId, spec.MountNamespace, spec.MountNamespace);
            var barrier = new LinuxMaintenanceBarrier(new Query(barrierState));
            var safety = new Safety();
            var mountSystem = new MountSystem();
            var provider = new Specification(spec);
            var host = new LinuxMaintenanceHost(provider, barrier, safety, mountSystem);

            var observation = await host.ObserveAsync(snapshot, default);
            if (!observation.LeaseHeld || !observation.BindingsMatch
                || observation.Source != CloudMaintenancePathState.Expected
                || observation.Backup != CloudMaintenancePathState.Missing
                || observation.Mount != CloudMaintenanceMountState.Missing
                || !observation.ProfileMatchesTarget || !observation.PolicyMatchesTarget)
                throw new Exception("rollback host observation did not recognize the original stable configuration");
            var decision = CloudMaintenanceTransaction.Decide(intent, observation);
            if (decision.Action != CloudMaintenanceAction.PersistPhase || decision.NextPhase != CloudMaintenancePhase.Stable)
                throw new Exception("rollback did not accept restored original configuration");

            var changed = await host.ObserveAsync(snapshot with { Revision = "" }, default);
            if (changed.LeaseHeld || changed.BindingsMatch) throw new Exception("host accepted an empty journal revision");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class Query(LinuxBarrierSnapshot snapshot) : ILinuxBarrierQuery
    {
        public Task<LinuxBarrierSnapshot> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(snapshot);
    }

    private sealed class Specification(HostSpecification spec) : IHostSpecificationProvider
    {
        public HostSpecification Read(MaintenanceSnapshot snapshot) => spec;
    }

    private sealed class Safety : IMountSafetyBoundary
    {
        public Task VerifyAsync(HostSpecification spec, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<DirectoryIdentity> PrepareEmptyMountpointAsync(HostSpecification spec, CancellationToken cancellationToken)
            => throw new InvalidOperationException("unexpected mount setup");
        public Task VerifyEmptyMountpointAsync(HostSpecification spec, DirectoryIdentity expected, CancellationToken cancellationToken)
            => throw new InvalidOperationException("unexpected mountpoint validation");
        public Task VerifyServiceReadAsync(HostSpecification spec, CancellationToken cancellationToken)
            => throw new InvalidOperationException("unexpected mount read check");
    }

    private sealed class MountSystem : IMountSystemBackend
    {
        public Task<bool> UnitAbsentAsync(string unit, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<bool> UnitStoppedAsync(string unit, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<ManagedMountProcess?> GetManagedProcessAsync(string unit, HostSpecification spec, string remoteSpec, CancellationToken cancellationToken)
            => Task.FromResult<ManagedMountProcess?>(null);
        public Task<MountInstance?> InspectAsync(HostSpecification spec, CancellationToken cancellationToken) => Task.FromResult<MountInstance?>(null);
        public Task StartAsync(HostSpecification spec, string unit, string remoteSpec, CancellationToken cancellationToken)
            => throw new InvalidOperationException("unexpected mount start");
        public Task UnmountAsync(string path, CancellationToken cancellationToken)
            => throw new InvalidOperationException("unexpected unmount");
    }
}
