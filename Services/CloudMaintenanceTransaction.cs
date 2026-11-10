using Jellyfin.Plugin.AudioGateway.Models;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>
/// Adapter for the authoritative job journal and exclusive host lease. Every operation must
/// validate lease, complete custody, and exact live identity at the mutation boundary. CAS
/// compares the entire job snapshot revision, not only the intent. It must durably persist.
/// </summary>
public interface ICloudMaintenanceOperations
{
    /// <summary>Read the maintenance intent from the leased authoritative job.</summary>
    Task<CloudMaintenanceIntent?> ReadAsync(string jobId, CancellationToken cancellationToken);

    /// <summary>Observe current custody under the same lease.</summary>
    Task<CloudMaintenanceObservation> ObserveAsync(CloudMaintenanceIntent intent, CancellationToken cancellationToken);

    /// <summary>Compare the whole previously read job revision and durably replace its intent.</summary>
    Task<bool> CompareExchangeAsync(CloudMaintenanceIntent expected, CloudMaintenanceIntent replacement, CancellationToken cancellationToken);

    /// <summary>Perform one idempotent effect; Mount returns its verified exact instance identity.</summary>
    Task<string?> ExecuteAsync(CloudMaintenanceIntent intent, CloudMaintenanceAction action, CancellationToken cancellationToken);
}

/// <summary>Pure, fail-closed decisions for the existing job's offline handoff.</summary>
public static class CloudMaintenanceTransaction
{
    /// <summary>
    /// Run bounded steps under one externally held exclusive lease. A mount created before its
    /// identity is durably recorded is intentionally not adopted after a crash.
    /// </summary>
    public static async Task<CloudMaintenanceDecision> RunAsync(string jobId, ICloudMaintenanceOperations operations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operations);
        var intent = await operations.ReadAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (intent is null || intent.JobId != jobId) return Block();
        for (var step = 0; step < 32; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observation = await operations.ObserveAsync(intent, cancellationToken).ConfigureAwait(false);
            var decision = Decide(intent, observation);
            if (decision.Action is CloudMaintenanceAction.Blocked or CloudMaintenanceAction.Complete) return decision;
            if (decision.Action == CloudMaintenanceAction.PersistPhase)
            {
                var next = intent with { Phase = decision.NextPhase!.Value };
                if (!await operations.CompareExchangeAsync(intent, next, cancellationToken).ConfigureAwait(false)) return Block();
                intent = next;
                continue;
            }

            var mountIdentity = await operations.ExecuteAsync(intent, decision.Action, cancellationToken).ConfigureAwait(false);
            if (decision.Action == CloudMaintenanceAction.Mount)
            {
                if (string.IsNullOrWhiteSpace(mountIdentity)) return Block();
                var next = intent with { MountIdentity = mountIdentity };
                if (!await operations.CompareExchangeAsync(intent, next, cancellationToken).ConfigureAwait(false)) return Block();
                intent = next;
            }
        }
        return new CloudMaintenanceDecision(CloudMaintenanceAction.Blocked, null, "maintenance-step-limit");
    }

    /// <summary>Determine one step from a durable intent and a fresh leased observation.</summary>
    public static CloudMaintenanceDecision Decide(CloudMaintenanceIntent intent, CloudMaintenanceObservation observation)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(observation);
        if (!observation.LeaseHeld || !observation.BindingsMatch || !ValidCustody(intent)) return Block();

        var source = observation.Source;
        var backup = observation.Backup;
        var mount = observation.Mount;
        var local = source == CloudMaintenancePathState.Expected && backup == CloudMaintenancePathState.Missing && mount == CloudMaintenanceMountState.Missing;
        var moved = source == CloudMaintenancePathState.Missing && backup == CloudMaintenancePathState.Expected && mount == CloudMaintenanceMountState.Missing;
        var mounted = source == CloudMaintenancePathState.MountPoint && backup == CloudMaintenancePathState.Expected
            && mount == CloudMaintenanceMountState.Expected && !string.IsNullOrWhiteSpace(intent.MountIdentity);
        var unmounted = (source == CloudMaintenancePathState.Missing || source == CloudMaintenancePathState.MountPoint)
            && backup == CloudMaintenancePathState.Expected && mount == CloudMaintenanceMountState.Missing;

        if (intent.Direction == CloudMaintenanceDirection.Promote)
        {
            if (intent.MountIdentity is not null
                && (intent.Phase is CloudMaintenancePhase.Prepared or CloudMaintenancePhase.MoveIntent or CloudMaintenancePhase.Moved
                    || (intent.Phase == CloudMaintenancePhase.MountIntent && moved))) return Block();
            return intent.Phase switch
            {
                CloudMaintenancePhase.Prepared when local => Persist(CloudMaintenancePhase.MoveIntent),
                CloudMaintenancePhase.MoveIntent when local => Act(CloudMaintenanceAction.MoveSourceToBackup),
                CloudMaintenancePhase.MoveIntent when moved => Persist(CloudMaintenancePhase.Moved),
                CloudMaintenancePhase.Moved when moved => Persist(CloudMaintenancePhase.MountIntent),
                CloudMaintenancePhase.MountIntent when moved => Act(CloudMaintenanceAction.Mount),
                CloudMaintenancePhase.MountIntent when mounted => Persist(CloudMaintenancePhase.Mounted),
                CloudMaintenancePhase.Mounted when mounted => Persist(CloudMaintenancePhase.ConfigIntent),
                CloudMaintenancePhase.ConfigIntent when mounted && (!observation.ProfileMatchesTarget || !observation.PolicyMatchesTarget) => Act(CloudMaintenanceAction.ApplyConfiguration),
                CloudMaintenancePhase.ConfigIntent when mounted => Persist(CloudMaintenancePhase.Stable),
                CloudMaintenancePhase.Stable when mounted && observation.ProfileMatchesTarget && observation.PolicyMatchesTarget => Act(CloudMaintenanceAction.Complete),
                _ => Block(),
            };
        }

        if (intent.Direction == CloudMaintenanceDirection.Rollback)
        {
            return intent.Phase switch
            {
                CloudMaintenancePhase.Prepared when mounted => Persist(CloudMaintenancePhase.UnmountIntent),
                CloudMaintenancePhase.UnmountIntent when mounted => Act(CloudMaintenanceAction.Unmount),
                CloudMaintenancePhase.UnmountIntent when unmounted => Persist(CloudMaintenancePhase.Unmounted),
                CloudMaintenancePhase.Unmounted when unmounted => Persist(CloudMaintenancePhase.RestoreIntent),
                CloudMaintenancePhase.RestoreIntent when unmounted => Act(CloudMaintenanceAction.RestoreBackup),
                CloudMaintenancePhase.RestoreIntent when local => Persist(CloudMaintenancePhase.Restored),
                CloudMaintenancePhase.Restored when local => Persist(CloudMaintenancePhase.ConfigIntent),
                CloudMaintenancePhase.ConfigIntent when local && (!observation.ProfileMatchesTarget || !observation.PolicyMatchesTarget) => Act(CloudMaintenanceAction.ApplyConfiguration),
                CloudMaintenancePhase.ConfigIntent when local => Persist(CloudMaintenancePhase.Stable),
                CloudMaintenancePhase.Stable when local && observation.ProfileMatchesTarget && observation.PolicyMatchesTarget => Act(CloudMaintenanceAction.Complete),
                _ => Block(),
            };
        }
        return Block();
    }

    private static CloudMaintenanceDecision Persist(CloudMaintenancePhase phase) => new(CloudMaintenanceAction.PersistPhase, phase, "journal-before-next-step");
    private static CloudMaintenanceDecision Act(CloudMaintenanceAction action) => new(action, null, "step-ready");
    private static CloudMaintenanceDecision Block() => new(CloudMaintenanceAction.Blocked, null, "maintenance-custody-ambiguous");

    private static bool ValidCustody(CloudMaintenanceIntent intent)
    {
        var custody = intent.Custody;
        if (!Guid.TryParseExact(intent.JobId, "N", out var jobId) || jobId == Guid.Empty
            || custody is null || !Guid.TryParse(custody.LibraryId, out var libraryId) || libraryId == Guid.Empty
            || string.IsNullOrWhiteSpace(intent.LocalIdentity) || string.IsNullOrWhiteSpace(custody.LibraryName)
            || string.IsNullOrWhiteSpace(custody.ProfileId) || string.IsNullOrWhiteSpace(custody.RemoteName)
            || !IsSha256(custody.ManifestDigest) || !IsSha256(custody.ConfigurationDigest)
            || custody.Verification != "rclone-check-download-content") return false;
        try
        {
            var local = custody.LocalPath;
            var backup = custody.BackupPath;
            if (string.IsNullOrWhiteSpace(local) || string.IsNullOrWhiteSpace(backup)
                || !Path.IsPathFullyQualified(local) || !Path.IsPathFullyQualified(backup)) return false;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(local, Path.GetFullPath(local), comparison)
                || !string.Equals(backup, Path.GetFullPath(backup), comparison)
                || !string.Equals(local, Path.TrimEndingDirectorySeparator(local), comparison)
                || !string.Equals(backup, Path.TrimEndingDirectorySeparator(backup), comparison)
                || string.Equals(local, backup, comparison)) return false;
            var parent = Path.GetDirectoryName(local);
            return !string.IsNullOrEmpty(parent) && string.Equals(parent, Path.GetDirectoryName(backup), comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsSha256(string? value)
        => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
