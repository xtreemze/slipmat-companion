using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Configuration;
using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services.Analysis;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>Current usage and configured limits for reproducible analysis artifacts.</summary>
/// <param name="UsedBytes">Total bytes in retained analysis sidecars and waveform/spectral files.</param>
/// <param name="SubjectCount">Number of analysis subjects with at least one retained artifact.</param>
/// <param name="MaximumBytes">Configured size limit, or null when disabled.</param>
/// <param name="MaximumAgeHours">Configured idle age, or null when disabled.</param>
/// <param name="Enabled">Whether any retention limit is active.</param>
/// <param name="ActiveSubjects">Number of subjects currently leased for generation or serving.</param>
public sealed record AnalysisArtifactUsage(
    long UsedBytes,
    int SubjectCount,
    long? MaximumBytes,
    int? MaximumAgeHours,
    bool Enabled,
    int ActiveSubjects);

/// <summary>Aggregate outcome of one artifact retention pass.</summary>
/// <param name="DeletedSubjects">Subjects whose reproducible artifacts were deleted.</param>
/// <param name="DeletedBytes">Bytes removed by this pass.</param>
/// <param name="RemainingBytes">Bytes remaining after cleanup.</param>
/// <param name="SkippedActiveSubjects">Subjects kept because they were in use.</param>
/// <param name="FailedSubjects">Subjects that could not be fully removed.</param>
/// <param name="Enabled">Whether a retention limit was active.</param>
public sealed record AnalysisArtifactCleanupResult(
    int DeletedSubjects,
    long DeletedBytes,
    long RemainingBytes,
    int SkippedActiveSubjects,
    int FailedSubjects,
    bool Enabled);

/// <summary>
/// Retention for reproducible analysis sidecars and waveform/spectral artifacts.
/// Source stamps and other plugin stores are deliberately outside this cache.
/// </summary>
public sealed class AnalysisArtifactRetentionService
{
    private const long GiB = 1024L * 1024 * 1024;
    private readonly object _leaseGate = new();
    private readonly SemaphoreSlim _cleanupGate = new(1, 1);
    private readonly Dictionary<string, int> _activeSubjects = new(StringComparer.Ordinal);
    private readonly HashSet<string> _evictingSubjects = new(StringComparer.Ordinal);
    private readonly Func<string> _storeRoot;

    /// <summary>Creates the retention service using the current plugin store.</summary>
    public AnalysisArtifactRetentionService()
        : this(() => RuntimeSettings.ResolveStoreRoot(
            Plugin.Instance?.Configuration ?? new PluginConfiguration()))
    {
    }

    internal AnalysisArtifactRetentionService(Func<string> storeRoot)
    {
        _storeRoot = storeRoot;
    }

    /// <summary>Measures only the analysis sidecar and waveform/spectral cache.</summary>
    public AnalysisArtifactUsage GetUsage(string storeRoot, PluginConfiguration config)
    {
        var entries = ScanEntries(storeRoot);
        var policy = ReadPolicy(config);
        int active;
        lock (_leaseGate)
        {
            active = _activeSubjects.Count;
        }

        return new AnalysisArtifactUsage(
            entries.Sum(entry => entry.Bytes),
            entries.Count,
            policy.MaxBytes,
            policy.MaxAgeHours,
            policy.MaxBytes.HasValue || policy.MaxAgeHours.HasValue,
            active);
    }

    /// <summary>Applies the configured opt-in limits to the resolved store root.</summary>
    public AnalysisArtifactCleanupResult ApplyConfiguredPolicy(
        PluginConfiguration config,
        DateTimeOffset now)
    {
        var policy = ReadPolicy(config);
        return ApplyRetention(
            _storeRoot(),
            policy.MaxBytes,
            policy.MaxAgeHours is int hours ? TimeSpan.FromHours(hours) : null,
            now);
    }

    internal AnalysisArtifactCleanupResult ApplyRetention(
        string storeRoot,
        long? maxBytes,
        TimeSpan? maxAge,
        DateTimeOffset now)
    {
        _cleanupGate.Wait();
        try
        {
            var remaining = ScanEntries(storeRoot);
            var deletedSubjects = 0;
            long deletedBytes = 0;
            var skippedActive = 0;
            var failedSubjects = 0;

            if (maxAge is TimeSpan age)
            {
                var cutoff = now.UtcDateTime - age;
                foreach (var entry in remaining.Where(entry => entry.LastUsedUtc < cutoff)
                    .OrderBy(entry => entry.LastUsedUtc))
                {
                    var entryBytes = entry.Bytes;
                    var outcome = TryDelete(entry);
                    if (outcome == DeleteOutcome.Deleted)
                    {
                        deletedSubjects++;
                        deletedBytes += entryBytes;
                        remaining.Remove(entry);
                    }
                    else if (outcome == DeleteOutcome.Active)
                    {
                        skippedActive++;
                    }
                    else failedSubjects++;
                }
            }

            var currentBytes = remaining.Sum(entry => entry.Bytes);
            if (maxBytes is long maximum && currentBytes > maximum)
            {
                foreach (var entry in remaining.OrderBy(entry => entry.LastUsedUtc))
                {
                    if (currentBytes <= maximum)
                    {
                        break;
                    }

                    var entryBytes = entry.Bytes;
                    var outcome = TryDelete(entry);
                    if (outcome == DeleteOutcome.Deleted)
                    {
                        deletedSubjects++;
                        deletedBytes += entryBytes;
                        currentBytes -= entryBytes;
                        remaining.Remove(entry);
                    }
                    else if (outcome == DeleteOutcome.Active)
                    {
                        skippedActive++;
                    }
                    else failedSubjects++;
                }
            }

            var remainingBytes = remaining.Sum(entry => entry.Bytes);
            return new AnalysisArtifactCleanupResult(
                deletedSubjects,
                deletedBytes,
                remainingBytes,
                skippedActive,
                failedSubjects,
                maxBytes.HasValue || maxAge.HasValue);
        }
        finally
        {
            _cleanupGate.Release();
        }
    }

    /// <summary>
    /// Attempts to protect a subject from eviction while analysis is in progress
    /// or an artifact response is being written.
    /// </summary>
    /// <returns>A disposable subject lease, or null during the short eviction window.</returns>
    public IDisposable? TryAcquire(string storeRoot, string subjectStoreKey)
    {
        StorePaths.ValidateStoreKey(subjectStoreKey);
        var leaseKey = LeaseKey(storeRoot, subjectStoreKey);
        lock (_leaseGate)
        {
            if (_evictingSubjects.Contains(leaseKey))
            {
                return null;
            }

            _activeSubjects.TryGetValue(leaseKey, out var count);
            _activeSubjects[leaseKey] = count + 1;
        }

        try
        {
            Touch(storeRoot, subjectStoreKey);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return new Lease(this, leaseKey);
    }

    /// <summary>Waits for any atomic eviction of a subject to finish, then protects it from cleanup.</summary>
    public async ValueTask<IDisposable> AcquireAsync(
        string storeRoot,
        string subjectStoreKey,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lease = TryAcquire(storeRoot, subjectStoreKey);
            if (lease is not null) return lease;
            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken).ConfigureAwait(false);
        }
    }

    private DeleteOutcome TryDelete(CacheEntry entry)
    {
        lock (_leaseGate)
        {
            if (_activeSubjects.ContainsKey(entry.LeaseKey)
                || !_evictingSubjects.Add(entry.LeaseKey))
            {
                return DeleteOutcome.Active;
            }
        }

        try
        {
            foreach (var file in entry.Files)
            {
                try
                {
                    if (File.Exists(file) && !IsReparsePoint(file))
                    {
                        File.Delete(file);
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            foreach (var directory in entry.Directories.OrderByDescending(path => path.Length))
            {
                try
                {
                    if (Directory.Exists(directory) && !IsReparsePoint(directory)
                        && !EnumerateEntries(directory).Any())
                    {
                        Directory.Delete(directory);
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            if (entry.Files.Any(File.Exists)) return DeleteOutcome.Failed;
            AnalysisSourceStamp.MarkArtifactsEvicted(entry.StoreRoot, entry.SubjectStoreKey);
            return DeleteOutcome.Deleted;
        }
        finally
        {
            lock (_leaseGate)
            {
                _evictingSubjects.Remove(entry.LeaseKey);
            }
        }
    }

    private static List<CacheEntry> ScanEntries(string storeRoot)
    {
        var fullRoot = Path.GetFullPath(storeRoot);
        var analysisRoot = Path.Combine(fullRoot, "analysis");
        var waveformsRoot = Path.Combine(fullRoot, "waveforms");
        var entries = new Dictionary<string, CacheEntry>(StringComparer.Ordinal);

        if (Directory.Exists(analysisRoot) && !IsReparsePoint(analysisRoot))
        {
            foreach (var path in EnumerateFiles(analysisRoot, "*.json"))
            {
                var key = Path.GetFileNameWithoutExtension(path);
                if (!AnalysisSubjectV1.IsStoreKey(key) || IsReparsePoint(path)) continue;
                GetEntry(entries, fullRoot, key).AddFile(path);
            }
        }

        if (Directory.Exists(waveformsRoot) && !IsReparsePoint(waveformsRoot))
        {
            foreach (var subjectDirectory in EnumerateDirectories(waveformsRoot))
            {
                var key = Path.GetFileName(subjectDirectory);
                if (!AnalysisSubjectV1.IsStoreKey(key) || IsReparsePoint(subjectDirectory)) continue;
                var entry = GetEntry(entries, fullRoot, key);
                entry.Directories.Add(subjectDirectory);
                foreach (var variantDirectory in EnumerateDirectories(subjectDirectory))
                {
                    if (!IsKnownWaveformVariant(Path.GetFileName(variantDirectory))
                        || IsReparsePoint(variantDirectory)) continue;
                    entry.Directories.Add(variantDirectory);
                    foreach (var path in EnumerateFiles(variantDirectory, "*"))
                    {
                        if (IsKnownWaveformFile(Path.GetFileName(path)) && !IsReparsePoint(path))
                        {
                            entry.AddFile(path);
                        }
                    }
                }
            }
        }

        return entries.Values.Where(entry => entry.Files.Count > 0).ToList();
    }

    private static CacheEntry GetEntry(IDictionary<string, CacheEntry> entries, string root, string key)
    {
        if (!entries.TryGetValue(key, out var entry))
        {
            entry = new CacheEntry(root, key);
            entries.Add(key, entry);
        }

        return entry;
    }

    private static void Touch(string storeRoot, string subjectStoreKey)
    {
        foreach (var path in GetSubjectArtifactFiles(storeRoot, subjectStoreKey))
        {
            try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static IEnumerable<string> GetSubjectArtifactFiles(string storeRoot, string subjectStoreKey)
    {
        var sidecar = StorePaths.SidecarPath(storeRoot, subjectStoreKey);
        if (File.Exists(sidecar) && !IsReparsePoint(sidecar)) yield return sidecar;
        var waveforms = Path.Combine(storeRoot, "waveforms", subjectStoreKey);
        if (!Directory.Exists(waveforms) || IsReparsePoint(waveforms)) yield break;
        foreach (var variantDirectory in EnumerateDirectories(waveforms))
        {
            if (!IsKnownWaveformVariant(Path.GetFileName(variantDirectory)) || IsReparsePoint(variantDirectory)) continue;
            foreach (var path in EnumerateFiles(variantDirectory, "*"))
            {
                if (IsKnownWaveformFile(Path.GetFileName(path)) && !IsReparsePoint(path)) yield return path;
            }
        }
    }

    private static bool IsKnownWaveformVariant(string variant)
        => string.Equals(variant, IntegratedAudioAnalyzer.AmplitudeVariant, StringComparison.Ordinal)
            || string.Equals(variant, SpectralArtifactEncoder.Variant, StringComparison.Ordinal);

    private static bool IsKnownWaveformFile(string name)
        => string.Equals(name, "pps_1.dat", StringComparison.Ordinal)
            || string.Equals(name, "pps_10.dat", StringComparison.Ordinal)
            || string.Equals(name, "pps_100.dat", StringComparison.Ordinal);

    private static string[] EnumerateDirectories(string path)
    {
        try { return Directory.EnumerateDirectories(path).ToArray(); }
        catch (IOException) { return Array.Empty<string>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    private static string[] EnumerateFiles(string path, string pattern)
    {
        try { return Directory.EnumerateFiles(path, pattern).ToArray(); }
        catch (IOException) { return Array.Empty<string>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    private static string[] EnumerateEntries(string path)
    {
        try { return Directory.EnumerateFileSystemEntries(path).ToArray(); }
        catch (IOException) { return Array.Empty<string>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    private void Release(string leaseKey)
    {
        lock (_leaseGate)
        {
            if (!_activeSubjects.TryGetValue(leaseKey, out var count)) return;
            if (count <= 1) _activeSubjects.Remove(leaseKey);
            else _activeSubjects[leaseKey] = count - 1;
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private static string LeaseKey(string root, string subjectStoreKey)
        => Path.GetFullPath(root) + "\0" + subjectStoreKey;

    private static RetentionPolicy ReadPolicy(PluginConfiguration config)
    {
        var sizeGiB = Math.Clamp(config.AnalysisArtifactMaxSizeGiB, 0, 1024);
        var ageHours = Math.Clamp(config.AnalysisArtifactMaxAgeHours, 0, 87600);
        return new RetentionPolicy(
            sizeGiB == 0 ? null : sizeGiB * GiB,
            ageHours == 0 ? null : ageHours);
    }

    private sealed class CacheEntry(string storeRoot, string key)
    {
        public string StoreRoot { get; } = Path.GetFullPath(storeRoot);
        public string SubjectStoreKey { get; } = key;
        public string LeaseKey { get; } = AnalysisArtifactRetentionService.LeaseKey(storeRoot, key);
        public HashSet<string> Files { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Directories { get; } = new(StringComparer.Ordinal);
        public long Bytes => Files.Sum(LengthOrZero);
        public DateTime LastUsedUtc => Files.Select(LastWriteTimeOrMinimum).DefaultIfEmpty(DateTime.MinValue).Max();

        public void AddFile(string path)
        {
            Files.Add(path);
            var directory = Path.GetDirectoryName(path);
            if (directory is not null) Directories.Add(directory);
        }

        private static long LengthOrZero(string path)
        {
            try
            {
                var file = new FileInfo(path);
                return file.Exists ? file.Length : 0;
            }
            catch (IOException) { return 0; }
            catch (UnauthorizedAccessException) { return 0; }
        }

        private static DateTime LastWriteTimeOrMinimum(string path)
        {
            try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
            catch (IOException) { return DateTime.MinValue; }
            catch (UnauthorizedAccessException) { return DateTime.MinValue; }
        }
    }

    private sealed class Lease(AnalysisArtifactRetentionService owner, string key) : IDisposable
    {
        private AnalysisArtifactRetentionService? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(key);
    }

    private sealed record RetentionPolicy(long? MaxBytes, int? MaxAgeHours);

    private enum DeleteOutcome
    {
        Deleted,
        Active,
        Failed,
    }
}
