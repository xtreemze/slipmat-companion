using Jellyfin.Plugin.AudioGateway.Models;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>Server-only provider effects, used under the canonical job lease and rclone fence.</summary>
public interface ICloudConflictRecoveryHost
{
    /// <summary>Capture complete fresh content evidence and its immutable job-owned manifest.</summary>
    Task<CloudConflictInventory> CaptureAsync(CloudConflictRecoveryBinding binding, string manifestPath,
        CancellationToken cancellationToken);

    /// <summary>Observe exact planned objects; proofs may be reused only while the same writer fence holds.</summary>
    Task<CloudConflictRecoveryObservation> ObserveAsync(CloudConflictRecoveryPlan plan,
        CloudConflictRecoveryState state, CancellationToken cancellationToken);

    /// <summary>Copy an original to its isolated no-replace archive path. Never changes an active path.</summary>
    Task ArchiveAsync(CloudConflictRecoveryPlan plan, int index, CancellationToken cancellationToken);

    /// <summary>Perform one approved metadata operation only with live archive and writer proof.</summary>
    Task MutateAsync(CloudConflictRecoveryPlan plan, int index, CancellationToken cancellationToken);
}

/// <summary>Optional, awaited observations from the server host during expensive content reads.</summary>
public interface ICloudConflictRecoveryProgressHost
{
    /// <summary>The canonical executor owns persistence; a report carries no mutation authority.</summary>
    Func<CloudConflictPreflightProgress, CancellationToken, Task>? ReportProgress { get; set; }
}

internal interface ICloudConflictHandoffHost
{
    // Returns whether the current download subset needs ASCII case matching.
    Task<bool> ValidateNamespaceAsync(CloudConflictRecoveryBinding binding, string sourceManifestSha256, string? knownArchivePath,
        CancellationToken cancellationToken);
}
