namespace Jellyfin.Plugin.AudioGateway.Models;

/// <summary>Read-only admission snapshot tied to the existing migration job.</summary>
public sealed record CloudMaintenanceAdmission(Guid LibraryId, string LibraryName, string LocalPath, string OptionsPath, string PluginConfigurationPath, CloudLibraryScanPolicy PreviousLibraryScanPolicy, string ProfileId, string RemoteName, string RemotePath, string ManifestDigestSha256, string Verification, string PluginConfigurationSha256, string OptionsSha256, bool PreviousEnabled, string PreviousProjectionPath, DateTimeOffset CapturedAt);

/// <summary>Direction of a maintenance transaction belonging to an existing migration job.</summary>
public enum CloudMaintenanceDirection
{
    /// <summary>Switch from local source to verified VFS.</summary>
    Promote,
    /// <summary>Restore retained local source.</summary>
    Rollback,
}

/// <summary>Durable write-ahead stages; a stage ending in Intent precedes its side effect.</summary>
public enum CloudMaintenancePhase
{
    /// <summary>Validated transaction admission.</summary>
    Prepared,
    /// <summary>Source move is journaled.</summary>
    MoveIntent,
    /// <summary>Source backup is observed.</summary>
    Moved,
    /// <summary>Mount creation is journaled.</summary>
    MountIntent,
    /// <summary>Exact managed mount is observed.</summary>
    Mounted,
    /// <summary>Unmount is journaled.</summary>
    UnmountIntent,
    /// <summary>Managed mount absence is observed.</summary>
    Unmounted,
    /// <summary>Backup restore is journaled.</summary>
    RestoreIntent,
    /// <summary>Original local identity is restored.</summary>
    Restored,
    /// <summary>Configuration application is journaled.</summary>
    ConfigIntent,
    /// <summary>Expected storage and configuration are observed.</summary>
    Stable,
}

/// <summary>Identity-checked custody path. MountPoint with no mount means an owned, verified-empty mountpoint only; unknown or nonempty is Other.</summary>
public enum CloudMaintenancePathState
{
    /// <summary>Path is absent.</summary>
    Missing,
    /// <summary>Path has the expected local identity.</summary>
    Expected,
    /// <summary>Verified managed mountpoint or owned empty mountpoint.</summary>
    MountPoint,
    /// <summary>Unknown or inconsistent path.</summary>
    Other,
}

/// <summary>Mount instance identity, not just a source or marker match.</summary>
public enum CloudMaintenanceMountState
{
    /// <summary>No mount covers the exact mountpoint.</summary>
    Missing,
    /// <summary>Exact recorded mount instance is present.</summary>
    Expected,
    /// <summary>Foreign, replaced or ambiguous mount.</summary>
    Other,
}

/// <summary>The sole next step permitted by the transaction decision.</summary>
public enum CloudMaintenanceAction
{
    /// <summary>Do not perform a side effect.</summary>
    Blocked,
    /// <summary>Durably persist the next phase.</summary>
    PersistPhase,
    /// <summary>Rename the verified source to its backup.</summary>
    MoveSourceToBackup,
    /// <summary>Create and verify the managed mount.</summary>
    Mount,
    /// <summary>Unmount the exact recorded instance.</summary>
    Unmount,
    /// <summary>Restore the verified backup at the original path.</summary>
    RestoreBackup,
    /// <summary>Apply the bound target profile and policy.</summary>
    ApplyConfiguration,
    /// <summary>The stable target is observed under the lease.</summary>
    Complete,
}

/// <summary>Immutable custody facts. ConfigurationDigest is SHA-256 of the exact versioned host-specification bytes, not an XML digest.</summary>
public sealed record CloudMaintenanceCustody(string LibraryId, string LibraryName, string LocalPath, string BackupPath, string ProfileId, string RemoteName, string RemotePath, string ManifestDigest, string Verification, string ConfigurationDigest);

/// <summary>Maintenance intent persisted as part of the authoritative migration job.</summary>
public sealed record CloudMaintenanceIntent(string JobId, CloudMaintenanceDirection Direction, CloudMaintenancePhase Phase, string LocalIdentity, string? MountIdentity, CloudMaintenanceCustody Custody);

/// <summary>Fresh observation made while an exclusive host lease is held.</summary>
public sealed record CloudMaintenanceObservation(bool LeaseHeld, bool BindingsMatch, CloudMaintenancePathState Source, CloudMaintenancePathState Backup, CloudMaintenanceMountState Mount, bool ProfileMatchesTarget, bool PolicyMatchesTarget);

/// <summary>Decision requires durable journal update before any subsequent effect.</summary>
public sealed record CloudMaintenanceDecision(CloudMaintenanceAction Action, CloudMaintenancePhase? NextPhase, string Code);
