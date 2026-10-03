using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Services;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public class AnalysisSourceStampTests
{
    private const string StoreKey =
        "asv1-0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task IsCurrent_MatchesUnchangedSourceMetadata()
    {
        using var temp = new TemporaryDirectory();
        var mediaPath = Path.Combine(temp.Path, "track.flac");
        File.WriteAllText(mediaPath, "fixture");

        var stamp = AnalysisSourceStamp.Capture(mediaPath);
        Assert.NotNull(stamp);
        await AnalysisSourceStamp.WriteAsync(
            temp.Path,
            StoreKey,
            stamp!,
            CancellationToken.None);

        Assert.True(AnalysisSourceStamp.IsCurrent(temp.Path, StoreKey, mediaPath));
    }

    [Fact]
    public async Task IsCurrent_FailsWhenSourceLengthChanges()
    {
        using var temp = new TemporaryDirectory();
        var mediaPath = Path.Combine(temp.Path, "track.flac");
        File.WriteAllText(mediaPath, "fixture");

        var stamp = AnalysisSourceStamp.Capture(mediaPath);
        Assert.NotNull(stamp);
        await AnalysisSourceStamp.WriteAsync(
            temp.Path,
            StoreKey,
            stamp!,
            CancellationToken.None);

        File.AppendAllText(mediaPath, "-changed");

        Assert.False(AnalysisSourceStamp.IsCurrent(temp.Path, StoreKey, mediaPath));
    }

    [Fact]
    public async Task IsCurrent_FailsWhenSourceModificationTimeChanges()
    {
        using var temp = new TemporaryDirectory();
        var mediaPath = Path.Combine(temp.Path, "track.flac");
        File.WriteAllText(mediaPath, "fixture");

        var stamp = AnalysisSourceStamp.Capture(mediaPath);
        Assert.NotNull(stamp);
        await AnalysisSourceStamp.WriteAsync(
            temp.Path,
            StoreKey,
            stamp!,
            CancellationToken.None);

        File.SetLastWriteTimeUtc(
            mediaPath,
            new DateTime(stamp!.LastWriteUtcTicks, DateTimeKind.Utc).AddSeconds(2));

        Assert.False(AnalysisSourceStamp.IsCurrent(temp.Path, StoreKey, mediaPath));
    }

    [Fact]
    public void IsCurrent_FailsClosedWhenStampIsMissing()
    {
        using var temp = new TemporaryDirectory();
        var mediaPath = Path.Combine(temp.Path, "track.flac");
        File.WriteAllText(mediaPath, "fixture");

        Assert.False(AnalysisSourceStamp.IsCurrent(temp.Path, StoreKey, mediaPath));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = Directory.CreateTempSubdirectory("audio-gateway-analysis-source").FullName;
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
