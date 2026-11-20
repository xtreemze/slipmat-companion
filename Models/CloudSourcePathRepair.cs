using System.Text.Json.Serialization;
using System.Collections.Immutable;

namespace Jellyfin.Plugin.AudioGateway.Models;

/// <summary>Identity of an exact entry on the admitted mount, observed through no-follow descriptors.</summary>
public readonly record struct CloudSourcePathRepairIdentity(ulong Device, ulong Inode, long MountId);

/// <summary>Fresh file metadata and complete original file-and-directory namespace compiled for this repair.</summary>
public sealed record CloudSourcePathRepairSource(int ManifestVersion, string ManifestSha256, long FileCount,
    long TotalBytes, string NamespaceSha256);

/// <summary>Immutable authority tying a repair to a single existing local-source migration and host specification.</summary>
public sealed record CloudSourcePathRepairBinding(string JobId, Guid RepairId, Guid LibraryId, string LibraryName,
    string ProfileId, string LocalRoot, string RemoteName, string RemotePath, CloudSourcePathRepairIdentity RootIdentity,
    CloudSourcePathRepairSource Source, string HostSpecificationSha256);

/// <summary>Kind the host must verify at both sides of an exact move.</summary>
public enum CloudSourcePathRepairKind
{
    /// <summary>Missing or unrecognized kind.</summary>
    Unknown,
    /// <summary>Regular file.</summary>
    File,
    /// <summary>Directory, including all its descendants.</summary>
    Directory,
}

/// <summary>One ordered same-parent no-replace rename on the bound local mount.</summary>
public sealed record CloudSourcePathRepairMove(CloudSourcePathRepairKind Kind, string Source, string Destination,
    CloudSourcePathRepairIdentity Identity);

/// <summary>
/// Immutable, versioned repair plan. The preservation manifest binds all reviewed payloads and directory
/// identities. The host specification binds the reserved exclusive archive location and host exclusion.
/// </summary>
public sealed record CloudSourcePathRepairPlan(int SchemaVersion, CloudSourcePathRepairBinding Binding,
    ImmutableArray<CloudSourcePathRepairMove> Moves, string PreservationManifestSha256,
    string TargetNamespaceSha256, string Fingerprint);

/// <summary>Durable repair phases. Intent phases precede any effect they authorize.</summary>
public enum CloudSourcePathRepairPhase
{
    /// <summary>Missing or unrecognized phase.</summary>
    Unknown,
    /// <summary>Admitted plan; no preservation or move effect permitted yet.</summary>
    Prepared,
    /// <summary>Preservation is authorized at the exclusive archive.</summary>
    PreservationIntent,
    /// <summary>A verified archive is journaled and the committed forward prefix is stable.</summary>
    Moving,
    /// <summary>The next forward move is authorized.</summary>
    MoveIntent,
    /// <summary>The full repaired namespace and preserved content must be verified.</summary>
    VerifyRepairedIntent,
    /// <summary>Verified repaired custody was durably recorded.</summary>
    Repaired,
    /// <summary>The committed prefix awaiting reverse-order restore is observed.</summary>
    Restoring,
    /// <summary>Restoring the last moved entry is authorized.</summary>
    RestoreIntent,
    /// <summary>The original namespace and preserved content must be verified.</summary>
    VerifyOriginalIntent,
    /// <summary>Verified original custody was durably recorded.</summary>
    RolledBack,
}

/// <summary>Identity and content evidence for the complete verified preservation copy.</summary>
public sealed record CloudSourcePathRepairPreservation(string PlanFingerprint, string ManifestSha256, string ArchiveIdentity);

/// <summary>Which complete namespace and preserved payloads the host has verified.</summary>
public enum CloudSourcePathRepairVerificationKind
{
    /// <summary>Missing or unrecognized verification.</summary>
    Unknown,
    /// <summary>Expected original namespace and content are present.</summary>
    Original,
    /// <summary>Expected repaired namespace and content are present.</summary>
    Repaired,
}

/// <summary>Plan-bound proof of complete local namespace and content verification.</summary>
public sealed record CloudSourcePathRepairVerification(string PlanFingerprint, CloudSourcePathRepairVerificationKind Kind,
    string NamespaceSha256, string PreservationManifestSha256);

/// <summary>
/// Repair intent for the canonical migration job. Cursor counts the committed forward prefix;
/// a pending move or restore may already have changed the physical prefix by one.
/// </summary>
public sealed record CloudSourcePathRepairState(CloudSourcePathRepairPlan Plan, CloudSourcePathRepairPhase Phase,
    int Cursor = 0, bool RollbackRequested = false, CloudSourcePathRepairPreservation? Preservation = null,
    CloudSourcePathRepairVerification? Verification = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReservedArchiveIdentity = null);

/// <summary>Repair state and the revision of the entire authoritative migration job.</summary>
public sealed record CloudSourcePathRepairSnapshot(string Revision, CloudSourcePathRepairState State);

/// <summary>Result of an exact, no-follow observation against the planned entry identity and kind.</summary>
public enum CloudSourcePathRepairPathState
{
    /// <summary>Missing observation.</summary>
    Unknown,
    /// <summary>Verified absence in the admitted parent directory.</summary>
    Missing,
    /// <summary>Expected entry and identity.</summary>
    Expected,
    /// <summary>Foreign object or unsafe observation.</summary>
    Foreign,
}

/// <summary>Exact source and destination observations for one move, in plan order.</summary>
public sealed record CloudSourcePathRepairPathObservation(CloudSourcePathRepairPathState Source,
    CloudSourcePathRepairPathState Destination);

/// <summary>State of the separately bound preservation archive.</summary>
public enum CloudSourcePathRepairArchiveState
{
    /// <summary>Missing observation.</summary>
    Unknown,
    /// <summary>Verified absence.</summary>
    Missing,
    /// <summary>Host attests an owned, safely resumable partial copy at its reserved location.</summary>
    Resumable,
    /// <summary>All required paths and content are verified on the admitted archive instance.</summary>
    Verified,
    /// <summary>Foreign, changed, or otherwise unrecognized state.</summary>
    Foreign,
}

/// <summary>Identity is required for an owned archive. A manifest digest is required only after full verification.</summary>
public sealed record CloudSourcePathRepairArchiveObservation(CloudSourcePathRepairArchiveState State,
    string? Identity = null, string? ManifestSha256 = null);

/// <summary>Fresh server-side custody evidence under the exclusive lease and persistent stopped-service barrier.</summary>
public sealed record CloudSourcePathRepairObservation(string JobRevision, string PlanFingerprint, bool LeaseHeld,
    bool BindingsMatch, bool StartupBarrierHeld, bool NoReplaceSupported,
    ImmutableArray<CloudSourcePathRepairPathObservation> Paths, CloudSourcePathRepairArchiveObservation Archive,
    CloudSourcePathRepairVerification? Verification = null);

/// <summary>Only these effects and authoritative journal transitions may be requested.</summary>
public enum CloudSourcePathRepairAction
{
    /// <summary>Stop with the existing custody intact.</summary>
    Blocked,
    /// <summary>Compare and durably record the next state on the same job.</summary>
    Persist,
    /// <summary>Create or resume only the bound immutable preservation copy.</summary>
    Preserve,
    /// <summary>Atomically move one verified entry without replacing a destination.</summary>
    Move,
    /// <summary>Atomically restore one verified entry without replacing a destination.</summary>
    Restore,
    /// <summary>Verify the complete repaired namespace and preservation payloads without mutation.</summary>
    VerifyRepaired,
    /// <summary>Verify the complete original namespace and preservation payloads without mutation.</summary>
    VerifyOriginal,
    /// <summary>The verified terminal state has been observed under the lease.</summary>
    Complete,
    /// <summary>Reserve an empty archive with no replacement and return its durable identity.</summary>
    ReserveArchive,
}

/// <summary>One deterministic decision. MoveIndex applies only to a move or restore effect.</summary>
public sealed record CloudSourcePathRepairDecision(CloudSourcePathRepairAction Action, string Code,
    CloudSourcePathRepairState? NextState = null, int? MoveIndex = null);
