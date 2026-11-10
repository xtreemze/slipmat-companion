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

    public async Task<CloudMaintenanceIntent?> ReadAsync(string jobId, CancellationToken cancellationToken)
    {
        _snapshot = await lease.ReadAsync(jobId, cancellationToken).ConfigureAwait(false);
        return _snapshot?.Intent;
    }

    public Task<CloudMaintenanceObservation> ObserveAsync(CloudMaintenanceIntent intent, CancellationToken cancellationToken)
    {
        RequireCurrent(intent);
        return host.ObserveAsync(_snapshot!, cancellationToken);
    }

    public async Task<bool> CompareExchangeAsync(CloudMaintenanceIntent expected, CloudMaintenanceIntent replacement, CancellationToken cancellationToken)
    {
        RequireCurrent(expected);
        if (replacement.JobId != expected.JobId || replacement.Custody != expected.Custody || replacement.LocalIdentity != expected.LocalIdentity || replacement.Direction != expected.Direction)
            return false;
        var next = await lease.CompareExchangeAsync(_snapshot!, replacement, cancellationToken).ConfigureAwait(false);
        if (next is null || next.Intent != replacement || string.IsNullOrEmpty(next.Revision))
        {
            _snapshot = null;
            return false;
        }
        _snapshot = next;
        return true;
    }

    public async Task<string?> ExecuteAsync(CloudMaintenanceIntent intent, CloudMaintenanceAction action, CancellationToken cancellationToken)
    {
        RequireCurrent(intent);
        var latest = await lease.ReadAsync(intent.JobId, cancellationToken).ConfigureAwait(false);
        if (latest is null || latest.Revision != _snapshot!.Revision) throw new InvalidOperationException("journal-changed");
        var observation = await host.ObserveAsync(_snapshot, cancellationToken).ConfigureAwait(false);
        if (CloudMaintenanceTransaction.Decide(intent, observation).Action != action || action is CloudMaintenanceAction.Blocked or CloudMaintenanceAction.PersistPhase or CloudMaintenanceAction.Complete)
            throw new InvalidOperationException("maintenance-action-rejected");
        return await host.ExecuteGuardedAsync(_snapshot, action, cancellationToken).ConfigureAwait(false);
    }

    private void RequireCurrent(CloudMaintenanceIntent intent)
    {
        if (_snapshot is null || _snapshot.Intent != intent || string.IsNullOrEmpty(_snapshot.Revision))
            throw new InvalidOperationException("journal-snapshot-stale");
    }
}
