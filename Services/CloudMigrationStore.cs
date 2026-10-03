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
            var root = ResolveRoot();
            Directory.CreateDirectory(root);
            var path = JobPath(job.Id);
            var temp = Path.Combine(
                root,
                $".{job.Id}.{Guid.NewGuid():N}.tmp");
            try
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(job, JsonOptions);
                await File
                    .WriteAllBytesAsync(temp, bytes, cancellationToken)
                    .ConfigureAwait(false);
                File.Move(temp, path, overwrite: true);
            }
            finally
            {
                TryDelete(temp);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private string ResolveRoot()
        => Path.Combine(
            RuntimeSettings.ResolveStoreRoot(_configurationSource.GetCurrent()),
            "cloud-migrations");

    private string JobPath(string jobId)
        => Path.Combine(ResolveRoot(), $"{jobId}.json");

    private static bool IsValidJobId(string? jobId)
        => Guid.TryParseExact(jobId, "N", out _);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
