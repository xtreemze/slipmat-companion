using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>
/// A verified, optional JPEG-to-JXL recompression result. The source JPEG
/// remains authoritative. These bytes are not admitted to any durable cache here.
/// </summary>
public sealed record VerifiedJpegXl(byte[] Bytes, string SourceJpegSha256)
{
    public const string ContentType = "image/jxl";
}

/// <summary>
/// Invokes the optional libjxl CLI tools and accepts output only when djxl
/// reconstructs the source JPEG byte-for-byte. It never edits source media.
/// Callers must separately bound job concurrency, storage, source IO and decoder
/// memory before enabling this for live Jellyfin requests.
/// </summary>
public sealed class ReversibleJpegXlEncoder
{
    public const int MaxSourceBytes = 16 * 1024 * 1024;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly string _encoderExecutable;
    private readonly string _decoderExecutable;
    private readonly Func<string, string[], CancellationToken, Task<bool>> _run;
    private readonly TimeSpan _timeout;

    public ReversibleJpegXlEncoder(
        string encoderExecutable = "cjxl",
        string decoderExecutable = "djxl",
        TimeSpan? timeout = null)
        : this(encoderExecutable, decoderExecutable, RunProcessAsync, timeout)
    {
    }

    /// <summary>
    /// Runner injection is limited to the codec process boundary; it permits
    /// failure-path unit tests without installing libjxl on every test host.
    /// </summary>
    public ReversibleJpegXlEncoder(
        string encoderExecutable,
        string decoderExecutable,
        Func<string, string[], CancellationToken, Task<bool>> run,
        TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encoderExecutable);
        ArgumentException.ThrowIfNullOrWhiteSpace(decoderExecutable);
        ArgumentNullException.ThrowIfNull(run);

        var limit = timeout ?? DefaultTimeout;
        if (limit <= TimeSpan.Zero || limit > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        _encoderExecutable = encoderExecutable;
        _decoderExecutable = decoderExecutable;
        _run = run;
        _timeout = limit;
    }

    /// <summary>
    /// Returns null on an ineligible source, unsuccessful codec run, failed
    /// reconstruction or a JXL representation that does not save any bytes.
    /// Caller cancellation propagates; a local codec deadline is a cache miss.
    /// </summary>
    public async Task<VerifiedJpegXl?> TryEncodeAsync(byte[] jpeg, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jpeg);

        if (!IsJpeg(jpeg) || jpeg.Length > MaxSourceBytes)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.Combine(Path.GetTempPath(), "slipmat-jxl-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var source = Path.Combine(directory, "source.jpeg");
            var encoded = Path.Combine(directory, "encoded.jxl");
            var reconstructed = Path.Combine(directory, "reconstructed.jpeg");

            await File.WriteAllBytesAsync(source, jpeg, cancellationToken).ConfigureAwait(false);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_timeout);

            // Force coefficient-preserving JPEG transcoding and preserve JPEG
            // reconstruction data; --distance=0 alone is NOT equivalent.
            if (!await _run(_encoderExecutable, [
                source, encoded, "--lossless_jpeg=1",
                "--allow_jpeg_reconstruction=1", "--num_threads=2",
            ], deadline.Token).ConfigureAwait(false))
            {
                return null;
            }

            if (!IsFileWithinBudget(encoded, jpeg.Length - 1))
            {
                return null;
            }

            if (!await _run(_decoderExecutable, [
                encoded, reconstructed, "--num_threads=2",
            ], deadline.Token).ConfigureAwait(false))
            {
                return null;
            }

            if (!IsFileWithinBudget(reconstructed, jpeg.Length)
                || new FileInfo(reconstructed).Length != jpeg.Length)
            {
                return null;
            }

            var restored = await File.ReadAllBytesAsync(reconstructed, deadline.Token).ConfigureAwait(false);
            var sourceHash = SHA256.HashData(jpeg);
            var restoredHash = SHA256.HashData(restored);
            if (!CryptographicOperations.FixedTimeEquals(sourceHash, restoredHash))
            {
                return null;
            }

            var bytes = await File.ReadAllBytesAsync(encoded, deadline.Token).ConfigureAwait(false);
            if (!IsJxl(bytes))
            {
                return null;
            }

            return new VerifiedJpegXl(bytes, Convert.ToHexStringLower(sourceHash));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (Win32Exception)
        {
            // libjxl tools are an optional install, not a Jellyfin prerequisite.
            return null;
        }
        finally
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (IOException)
            {
                // A failed cleanup must not make a valid verification claim.
                // The future job owner must record and remove abandoned workspaces.
            }
            catch (UnauthorizedAccessException)
            {
                // See above.
            }
        }
    }

    private static bool IsJpeg(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 4
        && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff;

    private static bool IsJxl(ReadOnlySpan<byte> bytes) =>
        (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0x0a)
        || (bytes.Length >= 12 && bytes[..12].SequenceEqual(
            [0, 0, 0, 12, 0x4a, 0x58, 0x4c, 0x20, 0x0d, 0x0a, 0x87, 0x0a]));

    private static bool IsFileWithinBudget(string path, long maximumBytes) =>
        maximumBytes > 0
        && File.Exists(path)
        && new FileInfo(path).Length is > 0
        && new FileInfo(path).Length <= maximumBytes;

    private static async Task<bool> RunProcessAsync(
        string executable,
        string[] arguments,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = start };
        if (!process.Start())
        {
            return false;
        }

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Child exited between the HasExited check and Kill.
            }

            throw;
        }
    }
}
