using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Services;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public class CloudMigrationManifestBuilderTests
{
    [Fact]
    public async Task Capture_IsDeterministicForUnchangedLibrary()
    {
        using var temp = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "Artist", "Album"));
        await File.WriteAllTextAsync(
            Path.Combine(temp.Path, "Artist", "Album", "track.flac"),
            "audio");
        await File.WriteAllTextAsync(
            Path.Combine(temp.Path, "Artist", "Album", "cover.jpg"),
            "cover");

        var first = await CloudMigrationManifestBuilder.CaptureAsync(temp.Path);
        var second = await CloudMigrationManifestBuilder.CaptureAsync(temp.Path);

        Assert.Equal(first.Version, second.Version);
        Assert.Equal(first.DigestSha256, second.DigestSha256);
        Assert.Equal(2, first.FileCount);
        Assert.Equal(10, first.TotalBytes);
    }

    [Fact]
    public async Task Capture_ChangesWhenSourceChanges()
    {
        using var temp = new TemporaryDirectory();
        var track = Path.Combine(temp.Path, "track.flac");
        await File.WriteAllTextAsync(track, "audio");
        var first = await CloudMigrationManifestBuilder.CaptureAsync(temp.Path);

        await File.AppendAllTextAsync(track, "-changed");
        var second = await CloudMigrationManifestBuilder.CaptureAsync(temp.Path);

        Assert.NotEqual(first.DigestSha256, second.DigestSha256);
        Assert.NotEqual(first.TotalBytes, second.TotalBytes);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = Directory.CreateTempSubdirectory(
                "audio-gateway-cloud-manifest-test").FullName;
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
