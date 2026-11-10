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
