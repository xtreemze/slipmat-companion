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
    [property: JsonPropertyName("previousLibraryScanPolicy")] CloudLibraryScanPolicy? PreviousLibraryScanPolicy = null,\n    [property: JsonPropertyName("manifestVersion")] int? ManifestVersion = null,\n    [property: JsonPropertyName("manifestDigestSha256")] string? ManifestDigestSha256 = null,\n    [property: JsonPropertyName("manifestFileCount")] long? ManifestFileCount = null,\n    [property: JsonPropertyName("manifestTotalBytes")] long? ManifestTotalBytes = null,\n    [property: JsonPropertyName("manifestCapturedAt")] DateTimeOffset? ManifestCapturedAt = null);

public sealed record CloudMigrationListResponse(
    [property: JsonPropertyName("jobs")] IReadOnlyList<CloudMigrationJob> Jobs);

public sealed record CloudMigrationBulkStartResponse(
    [property: JsonPropertyName("started")] IReadOnlyList<CloudMigrationJob> Started,
    [property: JsonPropertyName("skipped")] IReadOnlyList<CloudMigrationCandidate> Skipped);
