using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;

namespace Slipmat.MaintenanceRunner;

/// <summary>Maps the shared full-job journal lease to transaction intent snapshots.</summary>
public sealed class JournalLeaseAdapter(CloudMigrationJournalLease lease) : IMaintenanceLease
{
    private CloudMigrationJournalSnapshot? _snapshot;

    public async Task<MaintenanceSnapshot?> ReadAsync(string jobId, CancellationToken cancellationToken)
    {
        _snapshot = await lease.ReadAsync(jobId, cancellationToken).ConfigureAwait(false);
        return _snapshot?.Job.Maintenance is { } intent ? new(intent, _snapshot.Revision) : null;
    }

    public async Task<MaintenanceSnapshot?> CompareExchangeAsync(MaintenanceSnapshot expected, CloudMaintenanceIntent replacement, CancellationToken cancellationToken)
    {
        if (_snapshot is null || _snapshot.Revision != expected.Revision || _snapshot.Job.Maintenance != expected.Intent)
            return null;
        try
        {
            _snapshot = await lease.CompareExchangeAsync(_snapshot, _snapshot.Job with { Maintenance = replacement }, cancellationToken).ConfigureAwait(false);
            return new(replacement, _snapshot.Revision);
        }
        catch (InvalidOperationException ex) when (ex.Message == "migration-journal-revision-changed")
        {
            _snapshot = null;
            return null;
        }
    }
}
