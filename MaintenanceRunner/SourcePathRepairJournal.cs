using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;

namespace Slipmat.MaintenanceRunner;

/// <summary>Projects source repair state from the existing whole-job lease.</summary>
internal sealed class SourcePathRepairJournal(CloudMigrationJournalLease lease)
{
    internal async Task<CloudSourcePathRepairSnapshot?> ReadAsync(string jobId, CancellationToken cancellationToken)
        => Project(await lease.ReadAsync(jobId, cancellationToken).ConfigureAwait(false));

    internal async Task<CloudSourcePathRepairSnapshot?> CompareExchangeAsync(
        CloudSourcePathRepairSnapshot expected, CloudSourcePathRepairState replacement,
        CancellationToken cancellationToken)
    {
        var current = await lease.ReadAsync(expected.State.Plan.Binding.JobId, cancellationToken).ConfigureAwait(false);
        var repair = Project(current);
        if (repair is null || repair.Revision != expected.Revision
            || repair.State.Plan.Fingerprint != expected.State.Plan.Fingerprint
            || repair.State.Plan.Fingerprint != replacement.Plan.Fingerprint
            || !CloudSourcePathRepairTransaction.ValidState(replacement))
            return null;

        try
        {
            return Project(await lease.CompareExchangeAsync(current, current!.Job with
            {
                SourcePathRepair = replacement,
            }, cancellationToken).ConfigureAwait(false));
        }
        catch (InvalidOperationException ex) when (ex.Message == "migration-journal-revision-changed")
        {
            return null;
        }
    }

    private static CloudSourcePathRepairSnapshot? Project(CloudMigrationJournalSnapshot? snapshot)
    {
        if (snapshot?.Job is not { SourcePathRepair: { } repair } job
            || !CloudSourcePathRepairTransaction.ValidState(repair)
            || job.Direction != CloudMigrationDirections.LocalToVfs
            || job.Maintenance is not null || job.MaintenanceAdmission is not null
            || job.ConflictRecovery is { Status: not ("blocked" or "completed") })
            return null;

        return MatchesJob(repair.Plan.Binding, job) ? new(snapshot.Revision, repair) : null;
    }

    internal static bool MatchesJob(CloudSourcePathRepairBinding binding, CloudMigrationJob job)
        => binding.JobId == job.Id && binding.ProfileId == job.ProfileId && binding.LibraryName == job.LibraryName
            && binding.LocalRoot == job.LocalPath && binding.RemoteName == job.RemoteName && binding.RemotePath == job.RemotePath;
}
