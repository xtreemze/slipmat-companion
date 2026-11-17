namespace Jellyfin.Plugin.AudioGateway.Models;

/// <summary>Explicit policy acknowledgement; content and lease evidence is always server-produced.</summary>
public sealed record CloudConflictRecoveryRequest(string ExpectedManifestDigestSha256, string WriterPolicy);

/// <summary>Canonical job-owned conflict recovery, independent of VFS cutover.</summary>
public sealed record CloudConflictRecoverySession(
    string AttemptId,
    string ArchiveRoot,
    string WriterPolicy,
    string Status,
    DateTimeOffset RequestedAt,
    CloudConflictPlanReference? Plan = null,
    CloudConflictRecoveryCursor? Cursor = null,
    string? Code = null,
    CloudConflictPreflightProgress? Progress = null)
{
    /// <summary>Only this explicit operator declaration permits acquiring the server-side writer fence.</summary>
    public const string CompanionOnlyWriter = "companion-only";

    /// <summary>Other mutation paths remain blocked until repair has completed.</summary>
    public bool OwnsJob => Status != "completed";
}

/// <summary>Immutable job-owned plan sidecar; the journal is its sole admission authority.</summary>
public sealed record CloudConflictPlanReference(string Path, string Sha256, string Fingerprint, int Count);

/// <summary>
/// Ordered durable receipts. Every original is archived before the resolved prefix can advance.
/// Prefixes keep journal replacement bounded for libraries with thousands of conflicts.
/// </summary>
public sealed record CloudConflictRecoveryCursor(int Archived, int Resolved, string? Intent = null);

/// <summary>Canonical immutable inventory bytes; its descriptor hashes this document without a self-reference.</summary>
public sealed record CloudConflictInventoryDocument(CloudConflictRecoveryBinding Binding,
    IReadOnlyList<CloudConflictCandidate> Entries);

/// <summary>Observed preflight reads; these counters never replace content or custody proofs.</summary>
public sealed record CloudConflictPreflightProgress(string Phase, long Files, long TotalFiles,
    long BytesRead, DateTimeOffset ObservedAt);
