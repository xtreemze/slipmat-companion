using System;
using System.IO;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>
/// Observes the mount in the current Jellyfin process namespace. Marker files
/// describe intended ownership; they are not evidence of a live mount.
/// </summary>
public interface ICloudMountProbe
{
    CloudMountObservation Inspect(string mountPath, string remoteSpec);
}

public sealed record CloudMountObservation(
    bool Active,
    bool Owned,
    bool Readable,
    string Code)
{
    public bool Ready => Active && Owned && Readable;
}

/// <summary>
/// Linux-only rclone FUSE observation. Unsupported platforms fail closed
/// rather than treating directory contents as a mounted filesystem.
/// </summary>
public sealed class LinuxCloudMountProbe : ICloudMountProbe
{
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

    // Pure parser for deterministic mount namespace tests.
    public static CloudMountObservation InspectMountInfo(
        string[] lines,
        string mountPath,
        string remoteSpec,
        Func<string, bool>? canEnumerate = null)
    {
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(mountPath));
        // A later mount can hide an earlier mount at the same path.
        // Inspect the topmost entry, not an over-mounted stale rclone.
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

            try
            {
                // Enumerate at most one entry. An empty readable remote is valid.
                bool readable;
                if (canEnumerate is not null)
                {
                    readable = canEnumerate(target);
                }
                else
                {
                    using var entries = Directory.EnumerateFileSystemEntries(target).GetEnumerator();
                    _ = entries.MoveNext();
                    readable = true;
                }
                return readable
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
