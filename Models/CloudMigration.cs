using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AudioGateway.Models;

public static class CloudMigrationDirections
{
    public const string LocalToVfs = "local-to-vfs";
    public const string VfsToLocal = "vfs-to-local";
}

public static class CloudMigrationPhases
{
    public const string QueuedDiagnostics = "queued-diagnostics";
    public const string Diagnosing = "diagnosing";
    public const string QueuedRebaseline = "queued-rebaseline";
    public const string CheckingBaseline = "checking-baseline";
    public const string QueuedCopy = "queued-copy";
    public const string Copying = "copying";
    public const string Verifying = "verifying";
    public const string ReadyForCutover = "ready-for-cutover";
    public const string QueuedCutover = "queued-cutover";
    public const string FinalCopying = "final-copying";
    public const string FinalVerifying = "final-verifying";
    public const string CuttingOver = "cutting-over";
    public const string ReadyToFinalize = "ready-to-finalize";
    public const string QueuedRollback = "queued-rollback";
    public const string RollingBack = "rolling-back";
    public const string RolledBack = "rolled-back";
    public const string Finalizing = "finalizing";
    public const string Completed = "completed";
    public const string Failed = "failed";
}

public sealed record CloudMigrationCandidate(
    [property: JsonPropertyName("libraryName")] string LibraryName,
    [property: JsonPropertyName("collectionType")] string? CollectionType,
    [property: JsonPropertyName("localPath")] string LocalPath,
    [property: JsonPropertyName("profileId")] string? ProfileId,
    [property: JsonPropertyName("eligible")] bool Eligible,
    [property: JsonPropertyName("code")] string Code);

public sealed record CloudMigrationStartRequest(
    [property: JsonPropertyName("profileId")] string ProfileId,
    [property: JsonPropertyName("libraryName")] string LibraryName,
    [property: JsonPropertyName("direction")] string Direction);

public sealed record CloudMigrationBulkStartRequest(
    [property: JsonPropertyName("direction")] string Direction);

public sealed record CloudMigrationRebaselineRequest(
    [property: JsonPropertyName("expectedManifestDigestSha256")] string ExpectedManifestDigestSha256);

public sealed record CloudMigrationBaselineRevision(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("digestSha256")] string DigestSha256,
    [property: JsonPropertyName("fileCount")] long FileCount,
    [property: JsonPropertyName("totalBytes")] long TotalBytes,
    [property: JsonPropertyName("capturedAt")] DateTimeOffset? CapturedAt,
    [property: JsonPropertyName("replacedAt")] DateTimeOffset ReplacedAt,
    [property: JsonPropertyName("replacementDigestSha256")] string ReplacementDigestSha256,
    [property: JsonPropertyName("evidence")] string Evidence);

public sealed record CloudMigrationFinalizeRequest(
    [property: JsonPropertyName("confirmationPath")] string ConfirmationPath);

public sealed record CloudMigrationJob(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("profileId")] string ProfileId,
    [property: JsonPropertyName("libraryName")] string LibraryName,
    [property: JsonPropertyName("direction")] string Direction,
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("localPath")] string LocalPath,
    [property: JsonPropertyName("stagingPath")] string? StagingPath,
    [property: JsonPropertyName("backupPath")] string? BackupPath,
    [property: JsonPropertyName("remoteName")] string RemoteName,
    [property: JsonPropertyName("remotePath")] string RemotePath,
    [property: JsonPropertyName("previousProjectionPath")] string PreviousProjectionPath,
    [property: JsonPropertyName("previousEnabled")] bool PreviousEnabled,
    [property: JsonPropertyName("verification")] string? Verification,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("previousLibraryScanPolicy")] CloudLibraryScanPolicy? PreviousLibraryScanPolicy = null,
    [property: JsonPropertyName("manifestVersion")] int? ManifestVersion = null,
    [property: JsonPropertyName("manifestDigestSha256")] string? ManifestDigestSha256 = null,
    [property: JsonPropertyName("manifestFileCount")] long? ManifestFileCount = null,
    [property: JsonPropertyName("manifestTotalBytes")] long? ManifestTotalBytes = null,
    [property: JsonPropertyName("manifestCapturedAt")] DateTimeOffset? ManifestCapturedAt = null,
    [property: JsonPropertyName("vfsCertificationState")] string VfsCertificationState = "uncertified",
    [property: JsonPropertyName("vfsCertificationCode")] string? VfsCertificationCode = null,
    [property: JsonPropertyName("vfsCertifiedAt")] DateTimeOffset? VfsCertifiedAt = null,
    [property: JsonPropertyName("lastExitCode")] int? LastExitCode = null,
    [property: JsonPropertyName("progress")] CloudMigrationProgress? Progress = null,
    [property: JsonPropertyName("baselineHistory")] IReadOnlyList<CloudMigrationBaselineRevision>? BaselineHistory = null,
    [property: JsonPropertyName("diagnostics")] CloudMigrationDiagnostics? Diagnostics = null);

public sealed record CloudMigrationProgress(
    [property: JsonPropertyName("bytes")] long Bytes,
    [property: JsonPropertyName("totalBytes")] long TotalBytes,
    [property: JsonPropertyName("checks")] long Checks,
    [property: JsonPropertyName("transfers")] long Transfers,
    [property: JsonPropertyName("errors")] long Errors,
    [property: JsonPropertyName("observedAt")] DateTimeOffset ObservedAt);

public sealed record CloudMigrationListResponse(
    [property: JsonPropertyName("jobs")] IReadOnlyList<CloudMigrationJob> Jobs);

public sealed record CloudMigrationBulkStartResponse(
    [property: JsonPropertyName("started")] IReadOnlyList<CloudMigrationJob> Started,
    [property: JsonPropertyName("skipped")] IReadOnlyList<CloudMigrationCandidate> Skipped);

public sealed record CloudMigrationDiagnosticEntry(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("path")] string Path);

public sealed record CloudMigrationDiagnostics(
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("observedAt")] DateTimeOffset ObservedAt,
    [property: JsonPropertyName("startSucceeded")] bool StartSucceeded,
    [property: JsonPropertyName("exitCode")] int? ExitCode,
    [property: JsonPropertyName("timedOut")] bool TimedOut,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("entries")] IReadOnlyList<CloudMigrationDiagnosticEntry> Entries);
