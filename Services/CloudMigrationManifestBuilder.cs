using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.AudioGateway.Services;

public sealed record CloudMigrationManifest(
    int Version,
    string DigestSha256,
    long FileCount,
    long TotalBytes,
    DateTimeOffset CapturedAt);

/// <summary>
/// Captures bounded filesystem evidence for a migration source. The manifest is
/// operational custody evidence, never canonical media identity.
/// </summary>
public static class CloudMigrationManifestBuilder
{
    public const int CurrentVersion = 1;

    public static async Task<CloudMigrationManifest> CaptureAsync(
        string root,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot))
        {
            throw new DirectoryNotFoundException(fullRoot);
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long count = 0;
        long bytes = 0;

        foreach (var path in Directory
            .EnumerateFiles(fullRoot, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("source-reparse-point-unsupported");
            }

            var relative = Path.GetRelativePath(fullRoot, path)
                .Replace(Path.DirectorySeparatorChar, '/');
            Append(hash, relative);
            Append(hash, info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(hash, info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
            count++;
            checked { bytes += info.Length; }

            // Yield periodically so a large library inventory remains cancellable.
            if ((count & 0x3ff) == 0)
            {
                await Task.Yield();
            }
        }

        return new CloudMigrationManifest(
            CurrentVersion,
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
            count,
            bytes,
            DateTimeOffset.UtcNow);
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var data = Encoding.UTF8.GetBytes(value);
        hash.AppendData(data);
        hash.AppendData([0]);
    }
}
