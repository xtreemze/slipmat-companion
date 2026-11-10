using System.Security.Cryptography;
using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;

namespace Slipmat.MaintenanceRunner;

/// <summary>Loads the immutable root-controlled specification bound by the journal intent.</summary>
public interface IHostSpecificationProvider
{
    HostSpecification Read(MaintenanceSnapshot snapshot);
}

/// <summary>Production specification reader; rejects replacement, symlinks and untrusted owners.</summary>
public sealed class LinuxHostSpecificationProvider(string path) : IHostSpecificationProvider
{
    public HostSpecification Read(MaintenanceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var admission = snapshot.Job.MaintenanceAdmission
            ?? throw new InvalidOperationException("maintenance-admission-missing");
        return HostSpecification.Parse(LinuxTrustedFile.Read(path), snapshot.Intent, admission);
    }
}

/// <summary>Linux host effects guarded by the stopped-service barrier and exact custody observations.</summary>
public sealed class LinuxMaintenanceHost(
    IHostSpecificationProvider specifications,
    LinuxMaintenanceBarrier barrier,
    IMountSafetyBoundary mountSafety,
    IMountSystemBackend mountSystem) : IMaintenanceHost
{
    private readonly LinuxMountSupervisor mountSupervisor = new(mountSafety, mountSystem);

    public async Task<CloudMaintenanceObservation> ObserveAsync(MaintenanceSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("linux-required");
        var intent = snapshot.Intent;
        var spec = specifications.Read(snapshot);
        await barrier.VerifyMaintenanceAsync(spec, cancellationToken).ConfigureAwait(false);

        var bindingsMatch = !string.IsNullOrWhiteSpace(snapshot.Revision)
            && LeasedOperations.MatchesAdmittedJob(snapshot, intent);
        if (!bindingsMatch)
            return new(false, false, CloudMaintenancePathState.Other, CloudMaintenancePathState.Other,
                CloudMaintenanceMountState.Other, false, false);

        var mount = await ObserveMountAsync(spec, intent, cancellationToken).ConfigureAwait(false);
        var backup = PathState(LinuxTrustedFile.TryInspectDirectory(spec.BackupPath), spec.Source);
        var source = await ObserveSourceAsync(spec, intent, mount, backup, cancellationToken).ConfigureAwait(false);
        var profileTargetHash = intent.Direction == CloudMaintenanceDirection.Promote ? spec.Profile.AfterHash : spec.Profile.BeforeHash;
        var policyTargetHash = intent.Direction == CloudMaintenanceDirection.Promote ? spec.Policy.AfterHash : spec.Policy.BeforeHash;
        var profileMatches = MatchesHash(spec.Profile.Path, profileTargetHash);
        var policyMatches = MatchesHash(spec.Policy.Path, policyTargetHash);

        await barrier.VerifyMaintenanceAsync(spec, cancellationToken).ConfigureAwait(false);
        var finalMount = await ObserveMountAsync(spec, intent, cancellationToken).ConfigureAwait(false);
        if (finalMount != mount)
        {
            mount = CloudMaintenanceMountState.Other;
            source = CloudMaintenancePathState.Other;
        }
        return new(true, true, source, backup, mount, profileMatches, policyMatches);
    }

    public async Task<string?> ExecuteGuardedAsync(MaintenanceSnapshot snapshot, CloudMaintenanceAction action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("linux-required");
        var intent = snapshot.Intent;
        var admission = snapshot.Job.MaintenanceAdmission
            ?? throw new InvalidOperationException("maintenance-admission-missing");
        var spec = specifications.Read(snapshot);
        var observation = await ObserveAsync(snapshot, cancellationToken).ConfigureAwait(false);
        if (!LeasedOperations.MatchesAdmittedJob(snapshot, intent)
            || CloudMaintenanceTransaction.Decide(intent, observation).Action != action
            || action is CloudMaintenanceAction.Blocked or CloudMaintenanceAction.PersistPhase or CloudMaintenanceAction.Complete)
            throw new InvalidOperationException("maintenance-action-rejected");

        await barrier.VerifyMaintenanceAsync(spec, cancellationToken).ConfigureAwait(false);
        var remoteSpec = admission.RemoteName + ":" + admission.RemotePath;
        switch (action)
        {
            case CloudMaintenanceAction.MoveSourceToBackup:
                await CustodyProcess.RenameAsync(spec, Path.GetFileName(spec.Source.Path), Path.GetFileName(spec.BackupPath), cancellationToken).ConfigureAwait(false);
                await barrier.VerifyMaintenanceAsync(spec, cancellationToken).ConfigureAwait(false);
                return null;

            case CloudMaintenanceAction.Mount:
            {
                var remoteType = await RemoteTypeResolver.ResolveAsync(spec, admission.RemoteName, cancellationToken).ConfigureAwait(false);
                await barrier.VerifyMaintenanceAsync(spec, cancellationToken).ConfigureAwait(false);
                ProjectionMarker.Ensure(spec, admission.RemoteName, remoteType, admission.RemotePath);
                await barrier.VerifyMaintenanceAsync(spec, cancellationToken).ConfigureAwait(false);
                return await mountSupervisor.MountAsync(spec, remoteSpec, cancellationToken).ConfigureAwait(false);
            }

            case CloudMaintenanceAction.Unmount:
                if (string.IsNullOrWhiteSpace(intent.MountIdentity)) throw new InvalidOperationException("mount-identity-missing");
                await mountSupervisor.UnmountAsync(spec, remoteSpec, intent.MountIdentity, cancellationToken).ConfigureAwait(false);
                return null;

            case CloudMaintenanceAction.RestoreBackup:
            {
                var remoteType = await RemoteTypeResolver.ResolveAsync(spec, admission.RemoteName, cancellationToken).ConfigureAwait(false);
                await barrier.VerifyMaintenanceAsync(spec, cancellationToken).ConfigureAwait(false);
                await CustodyProcess.RemoveEmptyProjectionAsync(spec, admission.RemoteName, remoteType, admission.RemotePath, cancellationToken).ConfigureAwait(false);
                await barrier.VerifyMaintenanceAsync(spec, cancellationToken).ConfigureAwait(false);
                await CustodyProcess.RenameAsync(spec, Path.GetFileName(spec.BackupPath), Path.GetFileName(spec.Source.Path), cancellationToken).ConfigureAwait(false);
                await barrier.VerifyMaintenanceAsync(spec, cancellationToken).ConfigureAwait(false);
                return null;
            }

            case CloudMaintenanceAction.ApplyConfiguration:
                await ApplyConfigurationAsync(spec, admission, intent.Direction == CloudMaintenanceDirection.Promote, cancellationToken).ConfigureAwait(false);
                return null;

            default:
                throw new InvalidOperationException("maintenance-action-rejected");
        }
    }

    private async Task<CloudMaintenancePathState> ObserveSourceAsync(
        HostSpecification spec,
        CloudMaintenanceIntent intent,
        CloudMaintenanceMountState mount,
        CloudMaintenancePathState backup,
        CancellationToken cancellationToken)
    {
        if (mount == CloudMaintenanceMountState.Expected) return CloudMaintenancePathState.MountPoint;
        if (mount == CloudMaintenanceMountState.Other) return CloudMaintenancePathState.Other;

        var identity = LinuxTrustedFile.TryInspectDirectory(spec.Source.Path);
        if (identity is null) return CloudMaintenancePathState.Missing;
        if (identity == spec.Source) return CloudMaintenancePathState.Expected;
        if (!string.IsNullOrWhiteSpace(intent.MountIdentity))
        {
            try
            {
                var token = MountIdentityCodec.Decode(intent.MountIdentity, spec.BootId, spec.MountNamespace,
                    intent.Custody.RemoteName + ":" + intent.Custody.RemotePath, spec.Source.Path);
                return identity == token.MountPointIdentity
                    ? CloudMaintenancePathState.MountPoint
                    : CloudMaintenancePathState.Other;
            }
            catch (InvalidOperationException)
            {
                return CloudMaintenancePathState.Other;
            }
        }

        if (backup == CloudMaintenancePathState.Expected)
        {
            try
            {
                await mountSafety.VerifyEmptyMountpointAsync(spec, identity, cancellationToken).ConfigureAwait(false);
                return CloudMaintenancePathState.Missing;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                return CloudMaintenancePathState.Other;
            }
        }
        return CloudMaintenancePathState.Other;
    }

    private async Task<CloudMaintenanceMountState> ObserveMountAsync(
        HostSpecification spec,
        CloudMaintenanceIntent intent,
        CancellationToken cancellationToken)
    {
        var actual = await mountSystem.InspectAsync(spec, cancellationToken).ConfigureAwait(false);
        if (actual is null) return CloudMaintenanceMountState.Missing;
        if (string.IsNullOrWhiteSpace(intent.MountIdentity)) return CloudMaintenanceMountState.Other;
        MountInstance expected;
        try
        {
            expected = MountIdentityCodec.Decode(intent.MountIdentity, spec.BootId, spec.MountNamespace,
                intent.Custody.RemoteName + ":" + intent.Custody.RemotePath, spec.Source.Path);
        }
        catch (InvalidOperationException)
        {
            return CloudMaintenanceMountState.Other;
        }

        var expectedUnit = $"slipmat-vfs-{Guid.ParseExact(spec.JobId, "N"):N}.service";
        if (expected.Unit != expectedUnit) return CloudMaintenanceMountState.Other;

        var expectedMount = expected with { Unit = null, ProcessId = 0, ProcessStartTicks = 0, Cgroup = null, MountPointIdentity = null };
        var expectedOwner = new ManagedMountProcess(expected.Unit!, expected.ProcessId, expected.ProcessStartTicks, expected.Cgroup!);
        var owner = await mountSystem.GetManagedProcessAsync(expected.Unit!, spec,
            intent.Custody.RemoteName + ":" + intent.Custody.RemotePath, cancellationToken).ConfigureAwait(false);
        var final = await mountSystem.InspectAsync(spec, cancellationToken).ConfigureAwait(false);
        return actual == expectedMount && owner == expectedOwner && final == actual
            ? CloudMaintenanceMountState.Expected
            : CloudMaintenanceMountState.Other;
    }

    private static CloudMaintenancePathState PathState(DirectoryIdentity? actual, DirectoryIdentity expected)
        => actual is null ? CloudMaintenancePathState.Missing
            : actual.Device == expected.Device && actual.Inode == expected.Inode && actual.MountId == expected.MountId
                ? CloudMaintenancePathState.Expected
            : CloudMaintenancePathState.Other;

    private static bool MatchesHash(string path, string expected)
    {
        var actual = Convert.ToHexString(SHA256.HashData(LinuxTrustedFile.Read(path, 4 * 1024 * 1024)));
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    private async Task ApplyConfigurationAsync(
        HostSpecification spec,
        CloudMaintenanceAdmission admission,
        bool promote,
        CancellationToken cancellationToken)
    {
        var originalProfile = LinuxTrustedFile.Read(spec.Profile.OriginalSnapshotPath, 4 * 1024 * 1024);
        var originalPolicy = LinuxTrustedFile.Read(spec.Policy.OriginalSnapshotPath, 4 * 1024 * 1024);
        byte[] profileBytes;
        byte[] policyBytes;
        if (promote)
        {
            var plans = HostConfigurationPlans.Derive(spec, admission, originalProfile, originalPolicy);
            profileBytes = plans.Profile.AfterBytes;
            policyBytes = plans.Policy.AfterBytes;
        }
        else
        {
            if (!MatchesBytes(originalProfile, spec.Profile.BeforeHash) || !MatchesBytes(originalPolicy, spec.Policy.BeforeHash))
                throw new InvalidOperationException("configuration-snapshot-mismatch");
            profileBytes = originalProfile;
            policyBytes = originalPolicy;
        }

        await barrier.VerifyMaintenanceAsync(spec, cancellationToken).ConfigureAwait(false);
        await CustodyProcess.ReplaceConfigurationAsync(spec, spec.Profile, profileBytes, promote, cancellationToken).ConfigureAwait(false);
        await barrier.VerifyMaintenanceAsync(spec, cancellationToken).ConfigureAwait(false);
        await CustodyProcess.ReplaceConfigurationAsync(spec, spec.Policy, policyBytes, promote, cancellationToken).ConfigureAwait(false);
        await barrier.VerifyMaintenanceAsync(spec, cancellationToken).ConfigureAwait(false);
    }

    private static bool MatchesBytes(byte[] bytes, string hash)
        => string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), hash, StringComparison.OrdinalIgnoreCase);
}
