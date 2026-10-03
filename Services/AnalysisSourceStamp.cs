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

    public sealed record Stamp(long Length, long LastWriteUtcTicks);

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

            var stored = JsonSerializer.Deserialize<Stamp>(
                File.ReadAllText(path),
                JsonOptions);
            return stored == current;
        }
        catch
        {
            return false;
        }
    }

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
}
