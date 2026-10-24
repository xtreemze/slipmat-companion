using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>
/// Mount-table observation is synchronous and never accesses the FUSE directory.
/// Live readiness is verified by a bounded subprocess rather than calling readdir
/// on the Jellyfin thread pool. Marker files remain metadata, not mount proof.
/// </summary>
public interface ICloudMountProbe
{
    CloudMountObservation Inspect(string mountPath, string remoteSpec);

    // Default keeps deterministic fake adapters compatible with async callers.
    Task<CloudMountObservation> InspectAsync(
        string mountPath,
        string remoteSpec,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Inspect(mountPath, remoteSpec));
}

public sealed record CloudMountObservation(
    bool Active,
    bool Owned,
    bool Readable,
    string Code,
    bool HasFiles = false)
{
    public bool Ready => Active && Owned && Readable;
}

/// <summary>
/// Verifies the visible mount in Jellyfin's own Linux mount namespace.
/// Reading a stalled FUSE directory occurs only in a killable child process.
/// </summary>
public sealed class LinuxCloudMountProbe : ICloudMountProbe
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan TimeoutCooldown = TimeSpan.FromSeconds(30);

    private readonly string _directoryProbeExecutable;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _probeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, long> _recentTimeouts = new();

    public LinuxCloudMountProbe()
        : this("find", DefaultTimeout)
    {
    }

    // Allows isolated timeout tests to substitute a controlled child process.
    internal LinuxCloudMountProbe(string directoryProbeExecutable, TimeSpan timeout)
    {
        _directoryProbeExecutable = directoryProbeExecutable;
        _timeout = timeout;
    }

    public CloudMountObservation Inspect(string mountPath, string remoteSpec)
    {
        if (!OperatingSystem.IsLinux())
        {
            return new(false, false, false, "mount-probe-unsupported");
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines("/proc/self/mountinfo");
        }
        catch (IOException)
        {
            return new(false, false, false, "mount-probe-unavailable");
        }
        catch (UnauthorizedAccessException)
        {
            return new(false, false, false, "mount-probe-unavailable");
        }

        return InspectMountInfo(lines, Path.GetFullPath(mountPath), remoteSpec);
    }

    public async Task<CloudMountObservation> InspectAsync(
        string mountPath,
        string remoteSpec,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var mount = Inspect(mountPath, remoteSpec);
        if (!mount.Active || !mount.Owned)
        {
            return mount;
        }

        var key = string.Concat(Path.GetFullPath(mountPath), "\0", remoteSpec);
        if (_recentTimeouts.TryGetValue(key, out var lastTimeout)
            && Stopwatch.GetElapsedTime(lastTimeout) < TimeoutCooldown)
        {
            return mount with { Code = "mount-probe-timeout" };
        }

        // Concurrent dashboard/refresh requests MUST NOT start an unbounded
        // number of child processes against the same blocked FUSE mount.
        if (!await _probeGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return mount with { Code = "mount-probe-busy" };
        }

        try
        {
            // Re-read the namespace after acquiring the gate; a mount can have
            // disappeared or been replaced while another check was running.
            mount = Inspect(mountPath, remoteSpec);
            if (!mount.Active || !mount.Owned)
            {
                return mount;
            }

            var result = await CheckDirectoryAsync(
                mountPath, cancellationToken).ConfigureAwait(false);
            if (result.Code == "mount-probe-timeout")
            {
                _recentTimeouts[key] = Stopwatch.GetTimestamp();
            }
            else
            {
                _recentTimeouts.TryRemove(key, out _);
            }

            return mount with
            {
                Readable = result.Readable,
                HasFiles = result.HasFiles,
                Code = result.Code,
            };
        }
        finally
        {
            _probeGate.Release();
        }
    }

    private async Task<(bool Readable, bool HasFiles, string Code)> CheckDirectoryAsync(
        string mountPath,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _directoryProbeExecutable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        // No shell. The probe enumerates at most one pathname and never reads
        // file content. A path is passed as one argv token.
        process.StartInfo.ArgumentList.Add(mountPath);
        process.StartInfo.ArgumentList.Add("-mindepth");
        process.StartInfo.ArgumentList.Add("1");
        process.StartInfo.ArgumentList.Add("-maxdepth");
        process.StartInfo.ArgumentList.Add("1");
        process.StartInfo.ArgumentList.Add("-print");
        process.StartInfo.ArgumentList.Add("-quit");

        try
        {
            if (!process.Start())
            {
                return (false, false, "mount-probe-helper-unavailable");
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            deadline.CancelAfter(_timeout);
            try
            {
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                // find prints at most one path. Do not expose file names or stderr.
                var firstPath = await process.StandardOutput.ReadToEndAsync(
                    deadline.Token).ConfigureAwait(false);
                return process.ExitCode == 0
                    ? (true, firstPath.Length != 0, "mount-ready")
                    : (false, false, "mount-read-failed");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return (false, false, "mount-probe-timeout");
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (false, false, "mount-probe-helper-unavailable");
        }
        catch (IOException)
        {
            return (false, false, "mount-read-failed");
        }
        finally
        {
            if (process.Id != 0 && !process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Process already exited.
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // A blocked kernel task may not be immediately killable.
                }
            }
        }
    }

    // Pure parser for deterministic mount namespace tests. The optional reader
    // exists only for parser tests, never on the production request path.
    public static CloudMountObservation InspectMountInfo(
        string[] lines,
        string mountPath,
        string remoteSpec,
        Func<string, bool>? canEnumerate = null)
    {
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(mountPath));
        for (var index = lines.Length - 1; index >= 0; index--)
        {
            var sections = lines[index].Split(" - ", 2, StringSplitOptions.None);
            if (sections.Length != 2)
            {
                continue;
            }

            var left = sections[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var right = sections[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (left.Length < 5 || right.Length < 2
                || !string.Equals(DecodeMountInfoField(left[4]), target, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.Equals(right[0], "fuse.rclone", StringComparison.Ordinal)
                || !string.Equals(DecodeMountInfoField(right[1]), remoteSpec, StringComparison.Ordinal))
            {
                return new(true, false, false, "mount-foreign-filesystem");
            }

            if (canEnumerate is null)
            {
                return new(true, true, false, "mount-read-not-checked");
            }

            try
            {
                return canEnumerate(target)
                    ? new(true, true, true, "mount-ready")
                    : new(true, true, false, "mount-read-failed");
            }
            catch (UnauthorizedAccessException)
            {
                return new(true, true, false, "mount-permission-denied");
            }
            catch (IOException)
            {
                return new(true, true, false, "mount-read-failed");
            }
        }

        return new(false, false, false, "mount-not-active");
    }

    private static string DecodeMountInfoField(string field)
        => field.Replace("\\040", " ", StringComparison.Ordinal)
            .Replace("\\011", "\t", StringComparison.Ordinal)
            .Replace("\\012", "\n", StringComparison.Ordinal)
            .Replace("\\134", "\\", StringComparison.Ordinal);
}
