using Jellyfin.Plugin.AudioGateway.Models;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>
/// Server-owned, immutable plan snapshot validated once after journal admission.
/// This is a decision optimization, not a writer lease or provider proof.
/// </summary>
internal sealed class CloudConflictPreparedPlan
{
    private CloudConflictPreparedPlan(CloudConflictRecoveryPlan plan) => Plan = plan;

    internal CloudConflictRecoveryPlan Plan { get; }

    internal static CloudConflictPreparedPlan Create(CloudConflictRecoveryPlan source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Binding is null || source.Manifest is null || source.Items is null
            || source.Items.Count > CloudConflictRecoveryPlanner.MaxConflicts)
            throw new InvalidOperationException("conflict-plan-invalid");

        var items = new CloudConflictPlanItem[source.Items.Count];
        for (var index = 0; index < items.Length; index++)
        {
            var item = source.Items[index];
            if (item is null || item.Remote is null)
                throw new InvalidOperationException("conflict-plan-invalid");
            items[index] = item with
            {
                Local = item.Local is null ? null : item.Local with { },
                Remote = item.Remote with { },
            };
        }
        var frozen = source with
        {
            Binding = source.Binding with { },
            Manifest = source.Manifest with { },
            Items = Array.AsReadOnly(items),
        };
        if (!CloudConflictRecoveryPlanner.Digest(frozen.Fingerprint)
            || !CloudConflictRecoveryPlanner.EqualDigest(frozen.Fingerprint,
                CloudConflictRecoveryPlanner.Fingerprint(frozen)))
            throw new InvalidOperationException("conflict-plan-invalid");
        return new(frozen);
    }
}
