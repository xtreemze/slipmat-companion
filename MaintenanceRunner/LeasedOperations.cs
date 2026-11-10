using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;

namespace Slipmat.MaintenanceRunner;

/// <summary>A complete authoritative job snapshot whose revision covers the entire job.</summary>
public sealed record MaintenanceSnapshot(CloudMigrationJob Job, string Revision)
{
    public CloudMaintenanceIntent Intent => Job.Maintenance
        ?? throw new InvalidOperationException("maintenance-intent-missing");
}

/// <summary>Journal operations under an exclusively held cross-process lease.</summary>
public interface IMaintenanceLease
{
    Task<MaintenanceSnapshot?> ReadAsync(string jobId, CancellationToken cancellationToken);
    Task<MaintenanceSnapshot?> CompareExchangeAsync(MaintenanceSnapshot expected, CloudMaintenanceIntent replacement, CancellationToken cancellationToken);
}

/// <summary>Host implementation must check the systemd barrier and identities at each effect.</summary>
public interface IMaintenanceHost
{
    Task<CloudMaintenanceObservation> ObserveAsync(MaintenanceSnapshot snapshot, CancellationToken cancellationToken);
    Task<string?> ExecuteGuardedAsync(MaintenanceSnapshot snapshot, CloudMaintenanceAction action, CancellationToken cancellationToken);
}

/// <summary>Connects the pure transaction loop to one held journal lease and host boundary.</summary>
public sealed class LeasedOperations(IMaintenanceLease lease, IMaintenanceHost host) : ICloudMaintenanceOperations
{
    private MaintenanceSnapshot? _snapshot;
    private PendingMountCas? _pendingMountCas;

    private sealed record PendingMountCas(string Revision, CloudMaintenanceIntent Intent, string MountIdentity);

    public async Task<CloudMaintenanceIntent?> ReadAsync(string jobId, CancellationToken cancellationToken)
    {
        _pendingMountCas = null;
        _snapshot = await lease.ReadAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (_snapshot is null) return null;
        var intent = _snapshot.Job.Maintenance;
        if (intent is null) return null;
        if (intent.JobId != jobId || !MatchesAdmittedJob(_snapshot, intent))
        {
            _snapshot = null;
            throw new InvalidOperationException("maintenance-job-binding-mismatch");
        }
        return intent;
    }

    public async Task<CloudMaintenanceObservation> ObserveAsync(CloudMaintenanceIntent intent, CancellationToken cancellationToken)
    {
        await EnsureUnchangedAsync(intent, cancellationToken).ConfigureAwait(false);
        return await host.ObserveAsync(_snapshot!, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> CompareExchangeAsync(CloudMaintenanceIntent expected, CloudMaintenanceIntent replacement, CancellationToken cancellationToken)
    {
        RequireCurrent(expected);
        if (replacement.JobId != expected.JobId || replacement.Custody != expected.Custody || replacement.LocalIdentity != expected.LocalIdentity || replacement.Direction != expected.Direction)
            return false;
        var current = _snapshot!;
        var isPhaseAdvance = replacement.Phase != expected.Phase && replacement.MountIdentity == expected.MountIdentity;
        var pendingMountCas = _pendingMountCas;
        var isVerifiedMountIdentity = pendingMountCas is not null
            && pendingMountCas.Revision == current.Revision
            && pendingMountCas.Intent == expected
            && !string.IsNullOrWhiteSpace(pendingMountCas.MountIdentity)
            && replacement == expected with { MountIdentity = pendingMountCas.MountIdentity };

        if (isPhaseAdvance)
        {
            _pendingMountCas = null;
            await EnsureUnchangedAsync(expected, cancellationToken).ConfigureAwait(false);
            var observation = await host.ObserveAsync(current, cancellationToken).ConfigureAwait(false);
            await EnsureUnchangedAsync(expected, cancellationToken).ConfigureAwait(false);
            var decision = CloudMaintenanceTransaction.Decide(expected, observation);
            if (decision.Action != CloudMaintenanceAction.PersistPhase || decision.NextPhase != replacement.Phase)
                return false;
        }
        else if (isVerifiedMountIdentity)
        {
            _pendingMountCas = null;
        }
        else
        {
            _pendingMountCas = null;
            return false;
        }

        var next = await lease.CompareExchangeAsync(current, replacement, cancellationToken).ConfigureAwait(false);
        var expectedJob = current.Job with { Maintenance = replacement };
        if (next is null || next.Job != expectedJob || next.Intent != replacement
            || string.IsNullOrEmpty(next.Revision) || next.Revision == current.Revision
            || !MatchesAdmittedJob(next, replacement))
        {
            _snapshot = null;
            return false;
        }
        _snapshot = next;
        return true;
    }

    public async Task<string?> ExecuteAsync(CloudMaintenanceIntent intent, CloudMaintenanceAction action, CancellationToken cancellationToken)
    {
        await EnsureUnchangedAsync(intent, cancellationToken).ConfigureAwait(false);
        var observation = await host.ObserveAsync(_snapshot!, cancellationToken).ConfigureAwait(false);
        if (CloudMaintenanceTransaction.Decide(intent, observation).Action != action || action is CloudMaintenanceAction.Blocked or CloudMaintenanceAction.PersistPhase or CloudMaintenanceAction.Complete)
            throw new InvalidOperationException("maintenance-action-rejected");
        await EnsureUnchangedAsync(intent, cancellationToken).ConfigureAwait(false);
        var current = _snapshot!;
        var result = await host.ExecuteGuardedAsync(current, action, cancellationToken).ConfigureAwait(false);
        _pendingMountCas = action == CloudMaintenanceAction.Mount && !string.IsNullOrWhiteSpace(result)
            ? new PendingMountCas(current.Revision, intent, result)
            : null;
        return result;
    }

    private void RequireCurrent(CloudMaintenanceIntent intent)
    {
        if (_snapshot is null || string.IsNullOrEmpty(_snapshot.Revision)
            || !MatchesAdmittedJob(_snapshot, intent))
            throw new InvalidOperationException("journal-snapshot-stale");
    }

    private async Task EnsureUnchangedAsync(CloudMaintenanceIntent intent, CancellationToken cancellationToken)
    {
        RequireCurrent(intent);
        var latest = await lease.ReadAsync(intent.JobId, cancellationToken).ConfigureAwait(false);
        if (latest is null || latest.Revision != _snapshot!.Revision)
            throw new InvalidOperationException("journal-changed");
        if (!MatchesAdmittedJob(latest, intent))
            throw new InvalidOperationException("maintenance-job-binding-mismatch");
    }

    private static bool MatchesAdmittedJob(MaintenanceSnapshot snapshot, CloudMaintenanceIntent intent)
    {
        var job = snapshot.Job;
        var admission = job.MaintenanceAdmission;
        var custody = intent.Custody;
        if (job.Id != intent.JobId || job.Maintenance != intent || admission is null || custody is null
            || !Guid.TryParse(custody.LibraryId, out var libraryId)) return false;
        return libraryId == admission.LibraryId
            && custody.LibraryName == admission.LibraryName
            && custody.LocalPath == admission.LocalPath
            && custody.ProfileId == admission.ProfileId
            && custody.RemoteName == admission.RemoteName
            && custody.RemotePath == admission.RemotePath
            && string.Equals(custody.ManifestDigest, admission.ManifestDigestSha256, StringComparison.OrdinalIgnoreCase)
            && custody.Verification == admission.Verification
            && job.Direction == CloudMigrationDirections.LocalToVfs
            && job.Phase == CloudMigrationPhases.ReadyForCutover
            && job.ProfileId == admission.ProfileId
            && job.LibraryName == admission.LibraryName
            && job.LocalPath == admission.LocalPath
            && job.RemoteName == admission.RemoteName
            && job.RemotePath == admission.RemotePath
            && job.PreviousProjectionPath == admission.PreviousProjectionPath
            && job.PreviousEnabled == admission.PreviousEnabled
            && string.Equals(job.ManifestDigestSha256, admission.ManifestDigestSha256, StringComparison.OrdinalIgnoreCase)
            && job.Verification == admission.Verification;
    }
}
