using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Configuration;
using Jellyfin.Plugin.AudioGateway.Models;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>
/// Durable, companion-private migration journal. Migration state is operational
/// coordination data; it is never media identity or provider authority.
/// </summary>
public sealed class CloudMigrationStore
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        };

    private readonly ICloudProjectionConfigurationSource _configurationSource;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CloudMigrationStore(ICloudProjectionConfigurationSource configurationSource)
    {
        _configurationSource = configurationSource;
    }

    public async Task<IReadOnlyList<CloudMigrationJob>> LoadAllAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var root = ResolveRoot();
            if (!Directory.Exists(root))
            {
                return Array.Empty<CloudMigrationJob>();
            }

            var jobs = new List<CloudMigrationJob>();
            foreach (var path in Directory
                .EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var bytes = await File
                        .ReadAllBytesAsync(path, cancellationToken)
                        .ConfigureAwait(false);
                    var job = JsonSerializer.Deserialize<CloudMigrationJob>(bytes, JsonOptions);
                    if (job is not null && IsValidJobId(job.Id))
                    {
                        jobs.Add(job);
                    }
                }
                catch (JsonException)
                {
                    // Corrupt operational state fails closed: it is not surfaced as a
                    // runnable migration and the file remains for operator inspection.
                }
                catch (IOException)
                {
                    // A concurrently unavailable journal entry is treated as absent.
                }
            }

            return jobs
                .OrderByDescending(job => job.UpdatedAt)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CloudMigrationJob?> GetAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidJobId(jobId))
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = JobPath(jobId);
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                var bytes = await File
                    .ReadAllBytesAsync(path, cancellationToken)
                    .ConfigureAwait(false);
                return JsonSerializer.Deserialize<CloudMigrationJob>(bytes, JsonOptions);
            }
            catch (JsonException)
            {
                return null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        CloudMigrationJob job,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!IsValidJobId(job.Id))
        {
            throw new ArgumentException("Invalid migration job ID.", nameof(job));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Every plugin writer cooperates with the offline runner's lifetime
            // lease. An offline-owned job is never overwritten by a stale worker.
            await using var lease = await new CloudMigrationJournal(ResolveRoot())
                .AcquireAsync(cancellationToken).ConfigureAwait(false);
            var current = await lease.ReadAsync(job.Id, cancellationToken).ConfigureAwait(false);
            if (current?.Job.Maintenance is not null)
                throw new InvalidOperationException("migration-maintenance-owned");
            if (current?.Job.MaintenanceAdmission is not null)
                throw new InvalidOperationException("migration-maintenance-admitted");
            if (current?.Job.ConflictRecovery?.OwnsJob == true)
                throw new InvalidOperationException("migration-conflict-recovery-owned");
            await lease.CompareExchangeAsync(current, job, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Read a full revision for explicit admission instead of using a stale in-memory job.</summary>
    public async Task<CloudMigrationJournalSnapshot?> GetSnapshotAsync(
        string jobId, CancellationToken cancellationToken = default)
    {
        await using var lease = await new CloudMigrationJournal(ResolveRoot())
            .AcquireAsync(cancellationToken).ConfigureAwait(false);
        return await lease.ReadAsync(jobId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Persist an explicit transition only if the complete previously read revision remains current.</summary>
    public async Task SaveIfUnchangedAsync(
        CloudMigrationJournalSnapshot expected, CloudMigrationJob replacement,
        CancellationToken cancellationToken = default)
    {
        if (expected.Job.Maintenance is not null)
            throw new InvalidOperationException("migration-maintenance-owned");
        if (expected.Job.ConflictRecovery?.OwnsJob == true)
            throw new InvalidOperationException("migration-conflict-recovery-owned");
        await using var lease = await new CloudMigrationJournal(ResolveRoot())
            .AcquireAsync(cancellationToken).ConfigureAwait(false);
        await lease.CompareExchangeAsync(expected, replacement, cancellationToken).ConfigureAwait(false);
    }

    private string ResolveRoot()
        => Path.Combine(
            RuntimeSettings.ResolveStoreRoot(_configurationSource.GetCurrent()),
            "cloud-migrations");

    /// <summary>Acquire the shared whole-job writer lease for a recovery transaction.</summary>
    internal Task<CloudMigrationJournalLease> AcquireRecoveryLeaseAsync(CancellationToken cancellationToken)
        => new CloudMigrationJournal(ResolveRoot()).AcquireAsync(cancellationToken);

    /// <summary>Resolve only a server-generated, job-owned recovery sidecar directory.</summary>
    internal string ConflictRecoveryDirectory(string jobId, string attemptId)
    {
        if (!Guid.TryParseExact(jobId, "N", out _) || !Guid.TryParseExact(attemptId, "N", out _))
            throw new InvalidOperationException("conflict-recovery-id-invalid");
        return Path.Combine(ResolveRoot(), "conflict-recovery", jobId, attemptId);
    }

    /// <summary>Recovery sidecars must never become part of the source media tree.</summary>
    internal static void RejectRecoverySourceOverlap(string localPath, string directory)
    {
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(localPath));
        var recovery = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (source.Equals(recovery, comparison)
            || recovery.StartsWith(source + Path.DirectorySeparatorChar, comparison)
            || source.StartsWith(recovery + Path.DirectorySeparatorChar, comparison))
            throw new InvalidOperationException("conflict-recovery-store-overlaps-source");
    }

    private string JobPath(string jobId)
        => Path.Combine(ResolveRoot(), $"{jobId}.json");

    private static bool IsValidJobId(string? jobId)
        => Guid.TryParseExact(jobId, "N", out _);

}
