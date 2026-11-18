using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public sealed class CloudConflictRecoveryTests
{
    private const string A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static CloudConflictRecoveryBinding Binding() => new(Guid.NewGuid().ToString("N"), "music", "Music", "/music", "tele2", "Slipmat", "Slipmat-archive/run1", A, Guid.NewGuid().ToString("N"));
    private static CloudConflictObject Content(string digest) => new(12, digest, "stable-identity", CloudConflictContentKind.XmlMetadata);
    private static CloudConflictObject Jpeg(string digest) => Content(digest) with { Kind = CloudConflictContentKind.Jpeg };
    private static CloudConflictInventory Inventory(CloudConflictRecoveryBinding binding, params CloudConflictCandidate[] entries) =>
        new(binding, new("/private/inventory", A, entries.Length), entries);
    private static CloudConflictRecoveryPlan Plan() { var binding = Binding(); return CloudConflictRecoveryPlanner.Create(binding, Inventory(binding,
        new("a/album.nfo", Content(A), Content(B)), new("a/cover.jpg", null, Jpeg(B)), new("track.flac", Content(A), Content(A)))); }

    [Fact]
    public void PlanRetainsOnlySupportedConflictsAndPreservesRemoteOnlyArtwork()
    {
        var plan = Plan();
        Assert.Equal(2, plan.Items.Count);
        Assert.Equal(CloudConflictOperation.ReplaceWithLocal, plan.Items[0].Operation);
        Assert.Equal(CloudConflictOperation.RemoveRemoteOnly, plan.Items[1].Operation);
        Assert.Equal("Slipmat-archive/run1/a/cover.jpg", plan.Items[1].ArchivePath);
    }

    [Fact]
    public void PlanFingerprintBindsExactRemoteSpelling()
    {
        var binding = Binding();
        var plan = CloudConflictRecoveryPlanner.Create(binding, Inventory(binding,
            new CloudConflictCandidate("album.nfo", Content(A), Content(B), "ALBUM.nfo")));
        var item = Assert.Single(plan.Items);
        Assert.Equal("ALBUM.nfo", item.RemoteRelativePath);
        Assert.Equal(binding.ArchiveRoot + "/ALBUM.nfo", item.ArchivePath);
        var changed = plan with { Items = [item with { RemoteRelativePath = "Album.nfo" }] };
        Assert.NotEqual(plan.Fingerprint, CloudConflictRecoveryPlanner.Fingerprint(changed));
        Assert.Throws<InvalidOperationException>(() => CloudConflictRecoveryPlanner.Create(binding, Inventory(binding,
            new CloudConflictCandidate("ä.nfo", Content(A), Content(B), "Ä.nfo"))));
    }

    [Fact]
    public void AudioOrUnknownConflictsAndUnknownEvidenceBlockEntirePlan()
    {
        var binding = Binding();
        foreach (var item in new[] { new CloudConflictCandidate("music.flac", Content(A), Content(B)),
            new("cover.bin", Content(A), Content(B)), new("same.flac", Content(A), Content("")),
            new("missing.nfo", null, Content(A)), new("mystery.txt", Content(A), Content(B)) })
            Assert.Throws<InvalidOperationException>(() => CloudConflictRecoveryPlanner.Create(binding, Inventory(binding, new("cover.jpg", Jpeg(A), Jpeg(B)), item)));
    }

    [Fact]
    public void DisguisedAudioMismatchedAndUnknownContentBlockWholePlan()
    {
        var binding = Binding();
        var jpeg = Content(A) with { Kind = CloudConflictContentKind.Jpeg };
        foreach (var kind in new[] { CloudConflictContentKind.Audio, CloudConflictContentKind.Png, CloudConflictContentKind.Unknown })
        {
            var wrong = Content(B) with { Kind = kind };
            Assert.Throws<InvalidOperationException>(() => CloudConflictRecoveryPlanner.Create(binding,
                Inventory(binding, new CloudConflictCandidate("cover.jpg", jpeg, wrong))));
            Assert.Throws<InvalidOperationException>(() => CloudConflictRecoveryPlanner.Create(binding,
                Inventory(binding, new CloudConflictCandidate("cover.jpg", null, wrong))));
        }
    }

    [Fact]
    public void UnsafeAliasBindingsAndOverlappingArchiveFailClosed()
    {
        var binding = Binding();
        foreach (var item in new[] { new CloudConflictCandidate("../cover.jpg", Content(A), Content(B)), new("x\\cover.jpg", Content(A), Content(B)),
            new("cover.jpg/../x", Content(A), Content(B)) })
            Assert.Throws<InvalidOperationException>(() => CloudConflictRecoveryPlanner.Create(binding, Inventory(binding, item)));
        Assert.Throws<InvalidOperationException>(() => CloudConflictRecoveryPlanner.Create(binding, Inventory(binding, new("A.jpg", Jpeg(A), Jpeg(B)), new("a.jpg", Jpeg(A), Jpeg(B)))));
        Assert.Throws<InvalidOperationException>(() => CloudConflictRecoveryPlanner.Create(binding, Inventory(binding, new("dir.jpg", Jpeg(A), Jpeg(B)), new("DIR.JPG/file.jpg", Jpeg(A), Jpeg(B)))));
        Assert.Throws<InvalidOperationException>(() => CloudConflictRecoveryPlanner.Create(binding, Inventory(binding,
            new("dir.jpg", Jpeg(A), Jpeg(B)), new("dir.jpg-a", Content(A), Content(A)), new("DIR.JPG/file.jpg", Jpeg(A), Jpeg(B)))));
        Assert.Throws<InvalidOperationException>(() => CloudConflictRecoveryPlanner.Create(binding, Inventory(binding, new CloudConflictCandidate("x.jpg", Content(A), Content(A) with { Size = 13 }))));
        var nested = binding with { ArchiveRoot = "slipmat/inside" };
        Assert.Throws<InvalidOperationException>(() => CloudConflictRecoveryPlanner.Create(nested, Inventory(nested, new CloudConflictCandidate("a.jpg", Content(A), Content(B)))));
    }

    [Fact]
    public void AllArchivesMustBeVerifiedBeforeAnyActiveMutation()
    {
        var plan = Plan();
        var state = CloudConflictRecoveryState.Create(plan);
        var live = CloudConflictRecoveryObservation.Original(plan);
        Assert.Equal(CloudConflictAction.PersistArchiveIntent, CloudConflictRecoveryTransaction.Decide(plan, state, live).Action);
        state = state.WithStage(0, CloudConflictItemStage.Archived);
        Assert.NotEqual(CloudConflictAction.PersistMutationIntent, CloudConflictRecoveryTransaction.Decide(plan, state, live).Action);
    }
    [Fact]
    public void ArchiveAndMutationIntentsSurviveCrashAndRequireReceipts()
    {
        var plan = Plan();
        var state = CloudConflictRecoveryState.Create(plan);
        var live = CloudConflictRecoveryObservation.Original(plan);
        var observed = live.Items.ToArray();
        // Archive both objects, persisting intent before the effect and receipt afterwards.
        for (var index = 0; index < 2; index++)
        {
            Assert.Equal(CloudConflictAction.PersistArchiveIntent, CloudConflictRecoveryTransaction.Decide(plan, state, live).Action);
            state = state.WithStage(index, CloudConflictItemStage.ArchiveIntent);
            Assert.Equal(CloudConflictAction.Archive, CloudConflictRecoveryTransaction.Decide(plan, state, live).Action);
            observed[index] = observed[index] with { ArchiveSha256 = plan.Items[index].Remote.Sha256 };
            live = live with { Items = observed.ToArray() };
            Assert.Equal(CloudConflictAction.PersistArchiveReceipt, CloudConflictRecoveryTransaction.Decide(plan, state, live).Action);
            state = state.WithStage(index, CloudConflictItemStage.Archived, plan.Items[index].Remote.Sha256);
        }
        Assert.Equal(CloudConflictAction.Blocked, CloudConflictRecoveryTransaction.Decide(plan, state, live).Action);
        live = live with { Protection = new(CloudConflictProtectionKind.ConditionalVersion, plan.Items[0].Remote.Identity) };
        Assert.Equal(CloudConflictAction.PersistMutationIntent, CloudConflictRecoveryTransaction.Decide(plan, state, live).Action);
        state = state.WithStage(0, CloudConflictItemStage.MutationIntent, plan.Items[0].Remote.Sha256);
        Assert.Equal(CloudConflictAction.Mutate, CloudConflictRecoveryTransaction.Decide(plan, state, live).Action);
        observed[0] = observed[0] with { Active = plan.Items[0].Local };
        live = live with { Items = observed.ToArray() };
        Assert.Equal(CloudConflictAction.Blocked, CloudConflictRecoveryTransaction.Decide(plan, state, live).Action);
        observed[0] = observed[0] with { MutationReceipt = new(plan.Fingerprint, "other.nfo", plan.Items[0].Operation, plan.Items[0].Local!.Sha256, "wrong-receipt") };
        live = live with { Items = observed.ToArray() };
        Assert.Equal(CloudConflictAction.Blocked, CloudConflictRecoveryTransaction.Decide(plan, state, live).Action);
        observed[0] = observed[0] with { MutationReceipt = new(plan.Fingerprint, plan.Items[0].RelativePath, plan.Items[0].Operation, plan.Items[0].Local!.Sha256, "verified-operation-receipt") };
        live = live with { Items = observed.ToArray() };
        Assert.Equal(CloudConflictAction.PersistResolved, CloudConflictRecoveryTransaction.Decide(plan, state, live).Action);
        state = state.WithStage(0, CloudConflictItemStage.Resolved, plan.Items[0].Remote.Sha256, observed[0].MutationReceipt);
        Assert.Equal(CloudConflictAction.PersistMutationIntent, CloudConflictRecoveryTransaction.Decide(plan, state, live).Action);
        state = state.WithStage(1, CloudConflictItemStage.MutationIntent, plan.Items[1].Remote.Sha256);
        observed[1] = observed[1] with { Active = null, MutationReceipt = new(plan.Fingerprint, plan.Items[1].RelativePath, plan.Items[1].Operation, null, "verified-removal-receipt") };
        live = live with { Items = observed.ToArray() };
        Assert.Equal(CloudConflictAction.PersistResolved, CloudConflictRecoveryTransaction.Decide(plan, state, live).Action);
        state = state.WithStage(1, CloudConflictItemStage.Resolved, plan.Items[1].Remote.Sha256, observed[1].MutationReceipt);
        Assert.Equal(CloudConflictAction.Complete, CloudConflictRecoveryTransaction.Decide(plan, state, live).Action);
    }

    [Fact]
    public void MissingArchiveOrChangedSourceOrPlanBlocksEvenAfterArchiveReceipt()
    {
        var plan = Plan(); var state = CloudConflictRecoveryState.Create(plan)
            .WithStage(0, CloudConflictItemStage.Archived, plan.Items[0].Remote.Sha256)
            .WithStage(1, CloudConflictItemStage.Archived, plan.Items[1].Remote.Sha256);
        var live = CloudConflictRecoveryObservation.Original(plan);
        var items = live.Items.Select((item, index) => item with { ArchiveSha256 = plan.Items[index].Remote.Sha256 }).ToArray();
        live = live with { Items = items, Protection = new(CloudConflictProtectionKind.ExclusiveWriterLease, "server-lease") };
        Assert.Equal(CloudConflictAction.PersistMutationIntent, CloudConflictRecoveryTransaction.Decide(plan, state, live).Action);
        var missing = items.ToArray(); missing[1] = missing[1] with { ArchiveSha256 = null };
        Assert.Equal(CloudConflictAction.Blocked, CloudConflictRecoveryTransaction.Decide(plan, state, live with { Items = missing }).Action);
        var changed = items.ToArray(); changed[0] = changed[0] with { Active = Content(A) };
        Assert.Equal(CloudConflictAction.Blocked, CloudConflictRecoveryTransaction.Decide(plan, state, live with { Items = changed }).Action);
        Assert.Equal(CloudConflictAction.Blocked, CloudConflictRecoveryTransaction.Decide(plan, state, live with { PlanFingerprint = A }).Action);
    }

}
