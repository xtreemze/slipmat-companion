using System;
using Jellyfin.Plugin.AudioGateway.Models;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>Custody checks shared by explicit resume and the transfer worker.</summary>
public static class CloudMigrationRecovery
{
    public const int MaxBaselineRevisions = 32;

    public static bool CanRebaseline(CloudMigrationJob job)
        => CanResume(job) || (IsPrecutoverFailure(job)
            && (job.Code == "migration-source-changed"
                || job.Code.StartsWith("baseline-", StringComparison.Ordinal)));

    private static bool IsPrecutoverFailure(CloudMigrationJob job)
        => job.Phase == CloudMigrationPhases.Failed
            && job.Direction == CloudMigrationDirections.LocalToVfs
            && job.BackupPath is null && job.StagingPath is null
            && job.ManifestVersion == CloudMigrationManifestBuilder.CurrentVersion
            && job.ManifestFileCount is >= 0 && job.ManifestTotalBytes is >= 0
            && job.ManifestDigestSha256?.Length == 64;

    public static bool CanResume(CloudMigrationJob job)
        => IsPrecutoverFailure(job)
            && job.Code is "copy-failed" or "copy-timeout" or "verification-failed"
                or "verification-timeout" or "rclone-unavailable"
                or "remote-authentication-required" or "remote-permission-denied"
                or "remote-quota-exceeded" or "remote-immutable-conflict"
                or "rclone-temporary-error" or "rclone-fatal-error"
                or "rclone-transfer-limit" or "rclone-duration-limit";

    public static bool ManifestMatches(CloudMigrationJob job, CloudMigrationManifest manifest)
        => job.ManifestVersion == manifest.Version
            && job.ManifestFileCount == manifest.FileCount
            && job.ManifestTotalBytes == manifest.TotalBytes
            && string.Equals(job.ManifestDigestSha256, manifest.DigestSha256, StringComparison.Ordinal);

    public static string? FailureCode(RcloneCommandResult result, string operation)
    {
        if (!result.StartSucceeded) return "rclone-unavailable";
        if (result.TimedOut) return operation + "-timeout";
        if (result.ExitCode == 0) return null;
        var classified = RcloneCliHost.ClassifyRemoteFailure(result);
        if (classified is not null) return classified;
        // Only allowlisted classifications cross the HTTP boundary; no raw provider output.
        var error = result.StandardError;
        if (error.Contains("immutable file modified", StringComparison.OrdinalIgnoreCase)) return "remote-immutable-conflict";
        if (error.Contains("permission denied", StringComparison.OrdinalIgnoreCase)) return "remote-permission-denied";
        if (error.Contains("quota exceeded", StringComparison.OrdinalIgnoreCase)
            || error.Contains("insufficient storage", StringComparison.OrdinalIgnoreCase)) return "remote-quota-exceeded";
        return result.ExitCode switch
        {
            5 => "rclone-temporary-error",
            7 => "rclone-fatal-error",
            8 => "rclone-transfer-limit",
            10 => "rclone-duration-limit",
            _ => operation + "-failed",
        };
    }
}
