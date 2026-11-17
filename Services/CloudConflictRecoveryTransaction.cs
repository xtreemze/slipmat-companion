using Jellyfin.Plugin.AudioGateway.Models;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>One fail-closed transition; caller holds the job lease and CAS-persists intent before effects.</summary>
public static class CloudConflictRecoveryTransaction
{
    /// <summary>Choose a single action from durable state and fresh host evidence.</summary>
    public static CloudConflictDecision Decide(CloudConflictRecoveryPlan plan, CloudConflictRecoveryState state,
        CloudConflictRecoveryObservation observation)
        => DecideCore(plan, state, observation, validatePlanFingerprint: true);

    /// <summary>Use a server-validated immutable plan for repeated journal decisions.</summary>
    internal static CloudConflictDecision Decide(CloudConflictPreparedPlan prepared, CloudConflictRecoveryState state,
        CloudConflictRecoveryObservation observation)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        return DecideCore(prepared.Plan, state, observation, validatePlanFingerprint: false);
    }

    private static CloudConflictDecision DecideCore(CloudConflictRecoveryPlan plan, CloudConflictRecoveryState state,
        CloudConflictRecoveryObservation observation, bool validatePlanFingerprint)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(observation);
        if (plan.Items is null || plan.Items.Count > CloudConflictRecoveryPlanner.MaxConflicts
            || state.Items is null || observation.Items is null || state.Items.Count != plan.Items.Count
            || observation.Items.Count != plan.Items.Count || plan.Binding != observation.Binding
            || !CloudConflictRecoveryPlanner.Digest(plan.Fingerprint)
            || (validatePlanFingerprint && !CloudConflictRecoveryPlanner.EqualDigest(
                plan.Fingerprint, CloudConflictRecoveryPlanner.Fingerprint(plan)))
            || !CloudConflictRecoveryPlanner.EqualDigest(plan.Fingerprint, state.PlanFingerprint)
            || !CloudConflictRecoveryPlanner.EqualDigest(plan.Fingerprint, observation.PlanFingerprint)
            || state.Items.Any(item => item is null) || observation.Items.Any(item => item is null)
            || plan.Items.Any(item => item is null || item.Remote is null)) return Block();

        var archivePhase = state.Items.Any(item => item.Stage is CloudConflictItemStage.Planned or CloudConflictItemStage.ArchiveIntent);
        if (archivePhase && state.Items.Any(item => item.Stage is CloudConflictItemStage.MutationIntent or CloudConflictItemStage.Resolved)) return Block();
        for (var i = 0; i < plan.Items.Count; i++)
        {
            var item = plan.Items[i]; var current = state.Items[i]; var live = observation.Items[i];
            if (current is null || live is null || !Equals(live.Local, item.Local)) return Block();
            if (current.Stage is CloudConflictItemStage.Archived or CloudConflictItemStage.MutationIntent or CloudConflictItemStage.Resolved)
            {
                if (!CloudConflictRecoveryPlanner.EqualDigest(current.ArchiveSha256, item.Remote.Sha256)
                    || !CloudConflictRecoveryPlanner.EqualDigest(live.ArchiveSha256, item.Remote.Sha256)) return Block();
            }
            if (current.Stage == CloudConflictItemStage.Resolved)
            {
                if (!ValidReceipt(current.MutationReceipt, plan, item) || !IsTarget(item, live.Active)) return Block();
            }
            else if (current.Stage != CloudConflictItemStage.MutationIntent && !Equals(live.Active, item.Remote)) return Block();
        }
        if (archivePhase)
        {
            for (var i = 0; i < plan.Items.Count; i++)
            {
                var item = plan.Items[i]; var current = state.Items[i]; var live = observation.Items[i];
                if (current.Stage == CloudConflictItemStage.Planned) return Act(CloudConflictAction.PersistArchiveIntent, i);
                if (current.Stage != CloudConflictItemStage.ArchiveIntent) continue;
                if (live.ArchiveSha256 is null) return Act(CloudConflictAction.Archive, i);
                return CloudConflictRecoveryPlanner.EqualDigest(live.ArchiveSha256, item.Remote.Sha256)
                    ? Act(CloudConflictAction.PersistArchiveReceipt, i) : Block();
            }
            return Block();
        }
        for (var i = 0; i < plan.Items.Count; i++)
        {
            var item = plan.Items[i]; var current = state.Items[i]; var live = observation.Items[i];
            if (current.Stage == CloudConflictItemStage.Resolved) continue;
            if (current.Stage == CloudConflictItemStage.Archived)
                return Protected(observation.Protection, item) ? Act(CloudConflictAction.PersistMutationIntent, i) : Block();
            if (current.Stage != CloudConflictItemStage.MutationIntent) return Block();
            if (Equals(live.Active, item.Remote))
                return Protected(observation.Protection, item) ? Act(CloudConflictAction.Mutate, i) : Block();
            // A changed object is adopted only with a provider/host receipt proving this exact operation.
            if (IsTarget(item, live.Active) && ValidReceipt(live.MutationReceipt, plan, item))
                return Act(CloudConflictAction.PersistResolved, i);
            return Block();
        }
        return new(CloudConflictAction.Complete, null);
    }

    private static bool IsTarget(CloudConflictPlanItem item, CloudConflictObject? active)
        => item.Operation == CloudConflictOperation.RemoveRemoteOnly ? active is null
            : item.Local is not null && active is not null && active.Size == item.Local.Size
                && CloudConflictRecoveryPlanner.EqualDigest(active.Sha256, item.Local.Sha256);
    private static bool ValidReceipt(CloudConflictMutationReceipt? receipt, CloudConflictRecoveryPlan plan, CloudConflictPlanItem item)
        => receipt is not null && CloudConflictRecoveryPlanner.EqualDigest(receipt.PlanFingerprint, plan.Fingerprint)
            && receipt.RelativePath == item.RelativePath && receipt.Operation == item.Operation
            && (item.Operation == CloudConflictOperation.RemoveRemoteOnly ? receipt.TargetSha256 is null
                : CloudConflictRecoveryPlanner.EqualDigest(receipt.TargetSha256, item.Local?.Sha256))
            && !string.IsNullOrWhiteSpace(receipt.Proof);
    private static bool Protected(CloudConflictProtection? protection, CloudConflictPlanItem item)
        => protection is not null && !string.IsNullOrWhiteSpace(protection.Proof)
            && (protection.Kind == CloudConflictProtectionKind.ExclusiveWriterLease
                || protection.Kind == CloudConflictProtectionKind.ConditionalVersion && protection.Proof == item.Remote.Identity);
    private static CloudConflictDecision Block() => new(CloudConflictAction.Blocked, null);
    private static CloudConflictDecision Act(CloudConflictAction action, int index) => new(action, index);
}
