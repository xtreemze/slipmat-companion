using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Jellyfin.Plugin.AudioGateway.Models;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>A revision covers the entire authoritative job, including maintenance intent.</summary>
public sealed record CloudMigrationJournalSnapshot(CloudMigrationJob Job, string Revision);

/// <summary>Shared cooperative cross-process journal exclusion for plugin and offline runner.</summary>
public sealed class CloudMigrationJournal
{
    private readonly string _root;

    /// <summary>Use a canonical absolute journal directory shared by all migration writers.</summary>
    public CloudMigrationJournal(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("migration-journal-root-not-absolute", nameof(root));
        _root = Path.GetFullPath(root);
    }

    /// <summary>Acquire a cooperative process lease, waiting at most thirty seconds.</summary>
    public async Task<CloudMigrationJournalLease> AcquireAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_root);
        RejectLink(_root);
        var path = Path.Combine(_root, ".writer.lock");
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectLink(path);
            try
            {
                var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return new CloudMigrationJournalLease(_root, stream);
            }
            catch (IOException)
            {
                if (deadline.Elapsed >= TimeSpan.FromSeconds(30))
                    throw new IOException("migration-journal-lease-unavailable");
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal static void RejectLink(string path)
    {
        if (File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("migration-journal-symbolic-link");
        }
    }
}

/// <summary>Owns a journal lease through a complete transaction. Callers must also hold the host barrier.</summary>
public sealed class CloudMigrationJournalLease : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _root;
    private readonly FileStream _lock;
    private bool _disposed;

    internal CloudMigrationJournalLease(string root, FileStream stream) { _root = root; _lock = stream; }

    /// <summary>Read the complete job and raw-byte revision; corrupt state is never absence.</summary>
    public async Task<CloudMigrationJournalSnapshot?> ReadAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var path = JobPath(jobId);
        CloudMigrationJournal.RejectLink(path);
        byte[] bytes;
        try { bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false); }
        catch (FileNotFoundException) { return null; }
        var job = JsonSerializer.Deserialize<CloudMigrationJob>(bytes, JsonOptions)
            ?? throw new JsonException("migration-journal-empty");
        if (!string.Equals(job.Id, jobId, StringComparison.Ordinal))
            throw new JsonException("migration-journal-id-mismatch");
        return new(job, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    private static readonly JsonSerializerOptions InventoryJsonOptions = new(JsonOptions)
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        AllowDuplicateProperties = false,
    };

    /// <summary>
    /// Read a complete bounded custody inventory under this lease. Unlike UI listing, any
    /// unreadable, malformed, oversized or mismatched job blocks the entire inventory.
    /// This cooperative lease is not a substitute for trusted host directory custody.
    /// </summary>
    public async Task<IReadOnlyList<CloudMigrationJournalSnapshot>> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        const int maxJobs = 4096;
        const int maxJobBytes = 4 * 1024 * 1024;
        const long maxInventoryBytes = 64L * 1024 * 1024;
        var paths = new List<string>();
        var entryCount = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(_root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++entryCount > 16384) throw new IOException("migration-journal-inventory-limit");
            if (!string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase)) continue;
            if (paths.Count == maxJobs) throw new IOException("migration-journal-inventory-limit");
            paths.Add(path);
        }
        var result = new List<CloudMigrationJournalSnapshot>(paths.Count);
        long totalBytes = 0;
        foreach (var path in paths.OrderBy(path => path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = Path.GetFileNameWithoutExtension(path);
            if (!Guid.TryParseExact(id, "N", out var parsed) || parsed == Guid.Empty
                || Path.GetFileName(path) != parsed.ToString("N") + ".json")
                throw new JsonException("migration-journal-id-invalid");
            CloudMigrationJournal.RejectLink(path);
            // Reject empty entries before opening: FIFOs/devices must never make custody
            // inventory wait for another process. Host admission must also pin directory custody.
            var advertisedLength = new FileInfo(path).Length;
            if (advertisedLength <= 0 || advertisedLength > maxJobBytes || advertisedLength > maxInventoryBytes - totalBytes)
                throw new IOException("migration-journal-inventory-limit");
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var length = stream.Length;
            if (length != advertisedLength || length > maxJobBytes || length > maxInventoryBytes - totalBytes)
                throw new IOException("migration-journal-inventory-limit");
            var bytes = new byte[(int)length];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (stream.ReadByte() != -1 || stream.Length != length)
                throw new IOException("migration-journal-inventory-changed");
            totalBytes += length;
            var job = JsonSerializer.Deserialize<CloudMigrationJob>(bytes, InventoryJsonOptions)
                ?? throw new JsonException("migration-journal-empty");
            if (!string.Equals(job.Id, id, StringComparison.Ordinal))
                throw new JsonException("migration-journal-id-mismatch");
            if (new[] { job.ProfileId, job.LibraryName, job.Direction, job.Phase, job.RemoteName }
                    .Any(string.IsNullOrWhiteSpace) || !Path.IsPathFullyQualified(job.LocalPath))
                throw new JsonException("migration-journal-custody-invalid");
            result.Add(new(job, Convert.ToHexStringLower(SHA256.HashData(bytes))));
        }
        return result.AsReadOnly();
    }

    /// <summary>Replace exactly the expected revision and flush file and Linux directory metadata.</summary>
    public async Task<CloudMigrationJournalSnapshot> CompareExchangeAsync(
        CloudMigrationJournalSnapshot? expected, CloudMigrationJob replacement,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(replacement);
        var path = JobPath(replacement.Id);
        CloudMigrationJournal.RejectLink(path);
        if (expected is null)
        {
            if (File.Exists(path)) throw new InvalidOperationException("migration-journal-revision-changed");
        }
        else
        {
            if (expected.Job.Id != replacement.Id) throw new InvalidOperationException("migration-journal-id-changed");
            var current = await ReadAsync(replacement.Id, cancellationToken).ConfigureAwait(false);
            if (current?.Revision != expected.Revision) throw new InvalidOperationException("migration-journal-revision-changed");
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(replacement, JsonOptions);
        var temporary = Path.Combine(_root, $".{replacement.Id}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: expected is not null);
            FlushDirectory(_root);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return new(replacement, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return _lock.DisposeAsync();
    }

    private string JobPath(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("migration-journal-id-invalid", nameof(id));
        return Path.Combine(_root, id + ".json");
    }

    // Linux offline execution requires directory durability, not just atomic replacement.
    // Other hosts retain file flush semantics; they are not supported maintenance-runner targets.
    internal static void FlushDirectory(string path)
    {
        if (!OperatingSystem.IsLinux()) return;
        var directoryFlag = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 or Architecture.Arm => 0x4000,
            Architecture.X64 or Architecture.X86 => 0x10000,
            _ => throw new PlatformNotSupportedException("migration-journal-architecture-unsupported"),
        };
        var descriptor = Open(path, directoryFlag | 0x80000); // O_DIRECTORY | O_CLOEXEC
        if (descriptor < 0) throw new IOException("migration-journal-directory-open-failed");
        try
        {
            if (Fsync(descriptor) != 0) throw new IOException("migration-journal-directory-flush-failed");
        }
        finally { Close(descriptor); }
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(int descriptor);
    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int descriptor);
}
