namespace Jellyfin.Plugin.AudioGateway.Models;

/// <summary>Immutable job and namespace custody, admitted only by the server worker.</summary>
/// <param name="JobId">The job id.</param>
/// <param name="ProfileId">The profile id.</param>
/// <param name="LibraryName">The library name.</param>
/// <param name="LocalPath">The local path.</param>
/// <param name="RemoteName">The remote name.</param>
/// <param name="ActiveRoot">The active root.</param>
/// <param name="ArchiveRoot">The archive root.</param>
/// <param name="SourceManifestSha256">The source manifest sha256.</param>
/// <param name="AttemptId">The attempt id.</param>
public sealed record CloudConflictRecoveryBinding(string JobId, string ProfileId, string LibraryName, string LocalPath,
    string RemoteName, string ActiveRoot, string ArchiveRoot, string SourceManifestSha256, string AttemptId);

/// <summary>Job-owned immutable inventory sidecar; Count is the number of entries represented.</summary>
/// <param name="Path">The path.</param>
/// <param name="Sha256">The sha256.</param>
/// <param name="Count">The count.</param>
public sealed record CloudConflictManifestDescriptor(string Path, string Sha256, int Count);

/// <summary>Content type observed from the hashed bytes, not inferred from a filename.</summary>
public enum CloudConflictContentKind
{
    /// <summary>Unknown.</summary>
    Unknown,
    /// <summary>Xml Metadata.</summary>
    XmlMetadata,
    /// <summary>Jpeg.</summary>
    Jpeg,
    /// <summary>Png.</summary>
    Png,
    /// <summary>Audio.</summary>
    Audio,
}
/// <summary>Server-observed content digest and identity for a stable object.</summary>
/// <param name="Size">The size.</param>
/// <param name="Sha256">The sha256.</param>
/// <param name="Identity">The identity.</param>
/// <param name="Kind">The kind.</param>
public sealed record CloudConflictObject(long Size, string Sha256, string Identity, CloudConflictContentKind Kind = CloudConflictContentKind.Unknown);
/// <summary>Local and remote observations for one canonical relative path.</summary>
/// <param name="RelativePath">The relative path.</param>
/// <param name="Local">The local.</param>
/// <param name="Remote">The remote.</param>
public sealed record CloudConflictCandidate(string RelativePath, CloudConflictObject? Local, CloudConflictObject? Remote);

/// <summary>Complete server-produced observations; never accept this structure from an HTTP caller.</summary>
/// <param name="Binding">The binding.</param>
/// <param name="Manifest">The manifest.</param>
/// <param name="Entries">The entries.</param>
public sealed record CloudConflictInventory(CloudConflictRecoveryBinding Binding, CloudConflictManifestDescriptor Manifest,
    IReadOnlyList<CloudConflictCandidate> Entries);

/// <summary>Approved active-root operations after preservation.</summary>
public enum CloudConflictOperation
{
    /// <summary>Replace With Local.</summary>
    ReplaceWithLocal,
    /// <summary>Remove Remote Only.</summary>
    RemoveRemoteOnly,
}
/// <summary>One conflict and its separate preservation destination.</summary>
/// <param name="RelativePath">The relative path.</param>
/// <param name="ArchivePath">The archive path.</param>
/// <param name="Operation">The operation.</param>
/// <param name="Local">The local.</param>
/// <param name="Remote">The remote.</param>
public sealed record CloudConflictPlanItem(string RelativePath, string ArchivePath, CloudConflictOperation Operation,
    CloudConflictObject? Local, CloudConflictObject Remote);
/// <summary>Job-bound deterministic conflict plan with a content fingerprint.</summary>
/// <param name="Binding">The binding.</param>
/// <param name="Manifest">The manifest.</param>
/// <param name="Items">The items.</param>
/// <param name="DeferredLocalOnly">The deferred local only.</param>
/// <param name="Fingerprint">The fingerprint.</param>
public sealed record CloudConflictRecoveryPlan(CloudConflictRecoveryBinding Binding, CloudConflictManifestDescriptor Manifest,
    IReadOnlyList<CloudConflictPlanItem> Items, int DeferredLocalOnly, string Fingerprint);

/// <summary>Write-ahead stages persisted in the canonical job.</summary>
public enum CloudConflictItemStage
{
    /// <summary>Planned.</summary>
    Planned,
    /// <summary>Archive Intent.</summary>
    ArchiveIntent,
    /// <summary>Archived.</summary>
    Archived,
    /// <summary>Mutation Intent.</summary>
    MutationIntent,
    /// <summary>Resolved.</summary>
    Resolved,
}
/// <summary>Durable progress and receipts for one planned object.</summary>
/// <param name="Stage">The stage.</param>
/// <param name="ArchiveSha256">The archive sha256.</param>
/// <param name="MutationReceipt">The mutation receipt.</param>
public sealed record CloudConflictItemState(CloudConflictItemStage Stage, string? ArchiveSha256 = null, CloudConflictMutationReceipt? MutationReceipt = null);
/// <summary>Journaled progress tied to the exact conflict plan.</summary>
/// <param name="PlanFingerprint">The plan fingerprint.</param>
/// <param name="Items">The items.</param>
public sealed record CloudConflictRecoveryState(string PlanFingerprint, IReadOnlyList<CloudConflictItemState> Items)
{
    /// <summary>Create a state with no effects attempted.</summary>
    public static CloudConflictRecoveryState Create(CloudConflictRecoveryPlan plan)
        => new(plan.Fingerprint, plan.Items.Select(_ => new CloudConflictItemState(CloudConflictItemStage.Planned)).ToArray());

    /// <summary>Build a replacement state for the journal CAS.</summary>
    public CloudConflictRecoveryState WithStage(int index, CloudConflictItemStage stage, string? archiveSha256 = null, CloudConflictMutationReceipt? mutationReceipt = null)
    {
        var items = Items.ToArray();
        items[index] = new CloudConflictItemState(stage, archiveSha256, mutationReceipt);
        return this with { Items = items };
    }
}

/// <summary>Adapter-verified effect receipt, bound to this plan and exact operation.</summary>
/// <param name="PlanFingerprint">The plan fingerprint.</param>
/// <param name="RelativePath">The relative path.</param>
/// <param name="Operation">The operation.</param>
/// <param name="TargetSha256">The target sha256.</param>
/// <param name="Proof">The proof.</param>
public sealed record CloudConflictMutationReceipt(string PlanFingerprint, string RelativePath, CloudConflictOperation Operation,
    string? TargetSha256, string Proof);

/// <summary>Live content observation from the trusted server adapter, never caller supplied.</summary>
/// <param name="Local">The local.</param>
/// <param name="Active">The active.</param>
/// <param name="ArchiveSha256">The archive sha256.</param>
/// <param name="MutationReceipt">The mutation receipt.</param>
public sealed record CloudConflictLiveItem(CloudConflictObject? Local, CloudConflictObject? Active, string? ArchiveSha256, CloudConflictMutationReceipt? MutationReceipt = null);
/// <summary>Server-established protection for a mutation boundary.</summary>
public enum CloudConflictProtectionKind
{
    /// <summary>None.</summary>
    None,
    /// <summary>Conditional Version.</summary>
    ConditionalVersion,
    /// <summary>Exclusive Writer Lease.</summary>
    ExclusiveWriterLease,
}
/// <summary>Obtained and checked by the host adapter at the operation boundary; never accepted from an API caller.</summary>
/// <param name="Kind">The kind.</param>
/// <param name="Proof">The proof.</param>
public sealed record CloudConflictProtection(CloudConflictProtectionKind Kind, string Proof);
/// <summary>Fresh server-side observations for the whole plan.</summary>
/// <param name="Binding">The binding.</param>
/// <param name="PlanFingerprint">The plan fingerprint.</param>
/// <param name="Items">The items.</param>
/// <param name="Protection">The protection.</param>
public sealed record CloudConflictRecoveryObservation(CloudConflictRecoveryBinding Binding, string PlanFingerprint,
    IReadOnlyList<CloudConflictLiveItem> Items, CloudConflictProtection Protection)
{
    /// <summary>Construct original observations for deterministic tests.</summary>
    public static CloudConflictRecoveryObservation Original(CloudConflictRecoveryPlan plan) => new(plan.Binding, plan.Fingerprint,
        plan.Items.Select(item => new CloudConflictLiveItem(item.Local, item.Remote, null)).ToArray(), new(CloudConflictProtectionKind.None, ""));
}
/// <summary>Journal or host step selected by the transaction.</summary>
public enum CloudConflictAction
{
    /// <summary>Blocked.</summary>
    Blocked,
    /// <summary>Persist Archive Intent.</summary>
    PersistArchiveIntent,
    /// <summary>Archive.</summary>
    Archive,
    /// <summary>Persist Archive Receipt.</summary>
    PersistArchiveReceipt,
    /// <summary>Persist Mutation Intent.</summary>
    PersistMutationIntent,
    /// <summary>Mutate.</summary>
    Mutate,
    /// <summary>Persist Resolved.</summary>
    PersistResolved,
    /// <summary>Complete.</summary>
    Complete,
}
/// <summary>Next action and, when applicable, its plan item index.</summary>
/// <param name="Action">The action.</param>
/// <param name="ItemIndex">The item index.</param>
public sealed record CloudConflictDecision(CloudConflictAction Action, int? ItemIndex);
