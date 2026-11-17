using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public sealed class CloudConflictPreparedPlanTests
{
    private const string LocalSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string RemoteSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string WrongSha = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    [Fact]
    public void PreparedSnapshotCannotBeChangedThroughOriginalMutableItemArray()
    {
        var original = Plan();
        var prepared = CloudConflictPreparedPlan.Create(original);
        var frozen = prepared.Plan;
        Assert.NotSame(original, frozen);
        Assert.NotSame(original.Binding, frozen.Binding);
        Assert.NotSame(original.Manifest, frozen.Manifest);
        Assert.NotSame(original.Items[0], frozen.Items[0]);
        Assert.False(frozen.Items is CloudConflictPlanItem[]);

        var mutable = Assert.IsType<CloudConflictPlanItem[]>(original.Items);
        mutable[0] = mutable[0] with { Remote = mutable[0].Remote with { Sha256 = WrongSha } };
        Assert.Equal(RemoteSha, frozen.Items[0].Remote.Sha256);
        var state = CloudConflictRecoveryState.Create(frozen);
        var live = CloudConflictRecoveryObservation.Original(frozen);
        Assert.Equal(CloudConflictAction.PersistArchiveIntent,
            CloudConflictRecoveryTransaction.Decide(prepared, state, live).Action);
        Assert.Equal(CloudConflictAction.Blocked,
            CloudConflictRecoveryTransaction.Decide(original, state, live).Action);
    }

    [Fact]
    public void PreparedFactoryRejectsWrongPlanFingerprint()
    {
        var original = Plan();
        Assert.Throws<InvalidOperationException>(() => CloudConflictPreparedPlan.Create(original with { Fingerprint = WrongSha }));
    }

    [Fact]
    public void PreparedDecisionRetainsJournalArchiveReceiptAndProtectionChecks()
    {
        var prepared = CloudConflictPreparedPlan.Create(Plan());
        var plan = prepared.Plan;
        var item = Assert.Single(plan.Items);
        var state = CloudConflictRecoveryState.Create(plan);
        var live = CloudConflictRecoveryObservation.Original(plan);
        Assert.Equal(CloudConflictAction.Blocked,
            CloudConflictRecoveryTransaction.Decide(prepared, state with { PlanFingerprint = WrongSha }, live).Action);
        Assert.Equal(CloudConflictAction.Blocked,
            CloudConflictRecoveryTransaction.Decide(prepared, state, live with { PlanFingerprint = WrongSha }).Action);
        Assert.Equal(CloudConflictAction.PersistArchiveIntent,
            CloudConflictRecoveryTransaction.Decide(prepared, state, live).Action);

        state = state.WithStage(0, CloudConflictItemStage.ArchiveIntent);
        Assert.Equal(CloudConflictAction.Archive, CloudConflictRecoveryTransaction.Decide(prepared, state, live).Action);
        live = live with { Items = [live.Items[0] with { ArchiveSha256 = WrongSha }] };
        Assert.Equal(CloudConflictAction.Blocked, CloudConflictRecoveryTransaction.Decide(prepared, state, live).Action);
        live = live with { Items = [live.Items[0] with { ArchiveSha256 = RemoteSha }] };
        Assert.Equal(CloudConflictAction.PersistArchiveReceipt,
            CloudConflictRecoveryTransaction.Decide(prepared, state, live).Action);

        state = state.WithStage(0, CloudConflictItemStage.Archived, RemoteSha);
        Assert.Equal(CloudConflictAction.Blocked, CloudConflictRecoveryTransaction.Decide(prepared, state, live).Action);
        live = live with { Protection = new(CloudConflictProtectionKind.ExclusiveWriterLease, "server-lease") };
        Assert.Equal(CloudConflictAction.PersistMutationIntent,
            CloudConflictRecoveryTransaction.Decide(prepared, state, live).Action);
        state = state.WithStage(0, CloudConflictItemStage.MutationIntent, RemoteSha);
        Assert.Equal(CloudConflictAction.Mutate, CloudConflictRecoveryTransaction.Decide(prepared, state, live).Action);
        live = live with { Items = [live.Items[0] with { Active = item.Local }] };
        Assert.Equal(CloudConflictAction.Blocked, CloudConflictRecoveryTransaction.Decide(prepared, state, live).Action);
        var receipt = new CloudConflictMutationReceipt(plan.Fingerprint, item.RelativePath, item.Operation, LocalSha, "host-proof");
        live = live with { Items = [live.Items[0] with { MutationReceipt = receipt }] };
        Assert.Equal(CloudConflictAction.PersistResolved,
            CloudConflictRecoveryTransaction.Decide(prepared, state, live).Action);
        state = state.WithStage(0, CloudConflictItemStage.Resolved, RemoteSha, receipt);
        Assert.Equal(CloudConflictAction.Complete, CloudConflictRecoveryTransaction.Decide(prepared, state, live).Action);
    }

    private static CloudConflictRecoveryPlan Plan()
    {
        var binding = new CloudConflictRecoveryBinding(Guid.NewGuid().ToString("N"), "profile", "Music",
            "/tmp/music", "cloud", "Music", "Archive/attempt", LocalSha, Guid.NewGuid().ToString("N"));
        var inventory = new CloudConflictInventory(binding, new("/tmp/inventory.json", RemoteSha, 1),
            [new("album.nfo", new(12, LocalSha, "local-source", CloudConflictContentKind.XmlMetadata),
                new(10, RemoteSha, "remote-source", CloudConflictContentKind.XmlMetadata))]);
        return CloudConflictRecoveryPlanner.Create(binding, inventory);
    }
}
