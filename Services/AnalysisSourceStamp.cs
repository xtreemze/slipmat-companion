using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>
/// Companion-private, cheap source identity for deciding whether an existing
/// derived analysis artifact still corresponds to the mounted/local file.
///
/// This deliberately uses filesystem metadata only. It must not read the media
/// payload merely to decide whether an artifact can be served.
/// </summary>
public static class AnalysisSourceStamp
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    /// <summary>Cheap media identity and whether retention intentionally removed its artifacts.</summary>
    /// <param name="Length">Current source file length in bytes.</param>
    /// <param name="LastWriteUtcTicks">Current source modification time in UTC ticks.</param>
    /// <param name="ArtifactsEvicted">True only after retention removes all tracked artifacts for this source.</param>
    public sealed record Stamp(long Length, long LastWriteUtcTicks, bool ArtifactsEvicted = false);

    /// <summary>Captures the source length and modification time without reading its media payload.</summary>
    public static Stamp? Capture(string? mediaPath)
    {
        if (string.IsNullOrWhiteSpace(mediaPath))
        {
            return null;
        }

        try
        {
            var file = new FileInfo(mediaPath);
            if (!file.Exists)
            {
                return null;
            }

            return new Stamp(file.Length, file.LastWriteTimeUtc.Ticks);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Checks whether the persisted source identity still matches the mounted file.</summary>
    public static bool IsCurrent(
        string storeRoot,
        string storeKey,
        string? mediaPath)
    {
        var current = Capture(mediaPath);
        if (current is null)
        {
            return false;
        }

        var path = StorePaths.AnalysisSourceStampPath(storeRoot, storeKey);
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var stored = Read(path);
            return SameSource(stored, current);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns true when the current source stamp records that reproducible
    /// artifacts were intentionally evicted by the retention policy.
    /// </summary>
    public static bool WereArtifactsEvicted(
        string storeRoot,
        string storeKey,
        string? mediaPath)
    {
        var current = Capture(mediaPath);
        if (current is null) return false;
        var stored = Read(StorePaths.AnalysisSourceStampPath(storeRoot, storeKey));
        return stored?.ArtifactsEvicted == true && SameSource(stored, current);
    }

    /// <summary>
    /// Marks a current source stamp after all reproducible artifacts for that
    /// subject have been removed. The stamp file itself remains in place.
    /// </summary>
    public static bool MarkArtifactsEvicted(string storeRoot, string storeKey)
    {
        var path = StorePaths.AnalysisSourceStampPath(storeRoot, storeKey);
        var directory = Path.GetDirectoryName(path)!;
        if (IsReparsePoint(directory) || IsReparsePoint(path)) return false;
        var stamp = Read(path);
        if (stamp is null) return false;
        if (stamp.ArtifactsEvicted) return true;

        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(stamp with { ArtifactsEvicted = true }, JsonOptions);
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        finally
        {
            TryDelete(temporary);
        }
    }

    /// <summary>Atomically writes the source identity for an analyzed media item.</summary>
    public static async Task WriteAsync(
        string storeRoot,
        string storeKey,
        Stamp stamp,
        CancellationToken cancellationToken)
    {
        var path = StorePaths.AnalysisSourceStampPath(storeRoot, storeKey);
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Analysis source stamp path has no parent directory.");
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(stamp, JsonOptions);
            await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken)
                .ConfigureAwait(false);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    /// <summary>Deletes advisory source-identity metadata for the specified subject.</summary>
    public static void Delete(string storeRoot, string storeKey)
        => TryDelete(StorePaths.AnalysisSourceStampPath(storeRoot, storeKey));

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
            // Freshness is advisory acceleration state. Failure to delete means
            // the next comparison can still fail closed if the source changed.
        }
    }

    private static Stamp? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<Stamp>(File.ReadAllText(path), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static bool SameSource(Stamp? left, Stamp? right)
        => left is not null && right is not null
            && left.Length == right.Length
            && left.LastWriteUtcTicks == right.LastWriteUtcTicks;

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }
}
