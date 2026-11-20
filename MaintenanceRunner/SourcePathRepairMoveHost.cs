using System.Collections.Immutable;
using Jellyfin.Plugin.AudioGateway.Models;

namespace Slipmat.MaintenanceRunner;

/// <summary>Observes and applies only the exact moves authorized by the journal.</summary>
internal sealed class SourcePathRepairMoveHost(SourcePathRepairJournal journal, LinuxMaintenanceBarrier barrier, string specificationPath)
{
    internal async Task<ImmutableArray<CloudSourcePathRepairPathObservation>> ObserveAsync(
        CloudSourcePathRepairSnapshot expected, CancellationToken cancellationToken)
    {
        var (snapshot, specification) = await ReadBoundAsync(expected, cancellationToken).ConfigureAwait(false);
        await VerifyBarrierAsync(specification, cancellationToken).ConfigureAwait(false);
        var observations = await ObservePathsAsync(snapshot.State.Plan, specification, cancellationToken).ConfigureAwait(false);
        await VerifyBarrierAsync(specification, cancellationToken).ConfigureAwait(false);
        return observations;
    }

    internal async Task ExecuteAsync(CloudSourcePathRepairSnapshot expected, CloudSourcePathRepairDecision decision,
        CancellationToken cancellationToken)
    {
        var (snapshot, specification) = await ReadBoundAsync(expected, cancellationToken).ConfigureAwait(false);
        var state = snapshot.State;
        var reverse = decision.Action == CloudSourcePathRepairAction.Restore;
        if ((reverse && (state.Phase != CloudSourcePathRepairPhase.RestoreIntent || !state.RollbackRequested))
            || (!reverse && (decision.Action != CloudSourcePathRepairAction.Move
                            || state.Phase != CloudSourcePathRepairPhase.MoveIntent || state.RollbackRequested))
            || decision.MoveIndex != (reverse ? state.Cursor - 1 : state.Cursor)
            || state.Preservation is null)
            throw new InvalidOperationException("source-repair-move-intent-required");

        var archive = new SourcePathRepairArchiveHost(journal, barrier, specificationPath);
        await archive.VerifyArchiveAsync(snapshot, cancellationToken).ConfigureAwait(false);
        var paths = await ObservePathsAsync(state.Plan, specification, cancellationToken).ConfigureAwait(false);
        for (var i = 0; i < paths.Length; i++)
        {
            var moved = i < state.Cursor;
            if (paths[i].Source != (moved ? CloudSourcePathRepairPathState.Missing : CloudSourcePathRepairPathState.Expected)
                || paths[i].Destination != (moved ? CloudSourcePathRepairPathState.Expected : CloudSourcePathRepairPathState.Missing))
                throw new InvalidOperationException("source-repair-move-prefix-changed");
        }
        await ReadBoundAsync(snapshot, cancellationToken).ConfigureAwait(false);
        await VerifyBarrierAsync(specification, cancellationToken).ConfigureAwait(false);
        await CustodyProcess.MoveAsync(specification, state.Plan, decision.MoveIndex!.Value, execute: true, reverse,
            cancellationToken).ConfigureAwait(false);
        await VerifyBarrierAsync(specification, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(CloudSourcePathRepairSnapshot, SourcePathRepairHostSpecification)> ReadBoundAsync(
        CloudSourcePathRepairSnapshot expected, CancellationToken cancellationToken)
    {
        var snapshot = await journal.ReadAsync(expected.State.Plan.Binding.JobId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null || snapshot.Revision != expected.Revision
            || snapshot.State.Plan.Fingerprint != expected.State.Plan.Fingerprint)
            throw new InvalidOperationException("source-repair-move-journal-changed");
        var plan = snapshot.State.Plan;
        var root = LinuxTrustedFile.InspectDirectory(plan.Binding.LocalRoot);
        if (new CloudSourcePathRepairIdentity(root.Device, root.Inode, root.MountId) != plan.Binding.RootIdentity)
            throw new InvalidOperationException("source-repair-move-root-changed");
        return (snapshot, SourcePathRepairHostSpecification.Read(specificationPath, plan));
    }

    private Task VerifyBarrierAsync(SourcePathRepairHostSpecification specification, CancellationToken cancellationToken)
        => barrier.VerifyMaintenanceAsync("jellyfin.service", specification.BootId, specification.MountNamespace, cancellationToken);

    private static async Task<ImmutableArray<CloudSourcePathRepairPathObservation>> ObservePathsAsync(
        CloudSourcePathRepairPlan plan, SourcePathRepairHostSpecification specification, CancellationToken cancellationToken)
    {
        var observations = ImmutableArray.CreateBuilder<CloudSourcePathRepairPathObservation>(plan.Moves.Length);
        for (var i = 0; i < plan.Moves.Length; i++)
        {
            var observed = await CustodyProcess.MoveAsync(specification, plan, i, execute: false, reverse: false,
                cancellationToken).ConfigureAwait(false);
            observations.Add(observed ?? throw new InvalidOperationException("source-repair-move-result-invalid"));
        }
        return observations.MoveToImmutable();
    }
}
