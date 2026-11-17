using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public sealed class CloudConflictLocalContentTests
{
    private static readonly byte[] Jpeg = Convert.FromBase64String("/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/2wBDAQkJCQwLDBgNDRgyIRwhMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjL/wAARCAABAAEDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDi6KKK+ZP3E//Z");
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4z8AAAAMBAQDJ/pLvAAAAAElFTkSuQmCC");

    [Fact]
    public async Task HashesAndClassifiesActualContent()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture();
        var bytes = Encoding.UTF8.GetBytes("<album><title>Example</title></album>");
        await File.WriteAllBytesAsync(Path.Combine(fixture.Root, "album.nfo"), bytes);
        var actual = await CloudConflictLocalContent.ObserveAsync(fixture.Root, "album.nfo", default);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), actual.Sha256);
        Assert.Equal(CloudConflictContentKind.XmlMetadata, actual.Kind);
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "bad.nfo"), "<!DOCTYPE album [<!ENTITY x 'x'>]><album>&x;</album>");
        Assert.Equal(CloudConflictContentKind.Unknown, (await CloudConflictLocalContent.ObserveAsync(fixture.Root, "bad.nfo", default)).Kind);
    }

    [Fact]
    public async Task RejectsSymlinksAndUnsafePaths()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture();
        File.CreateSymbolicLink(Path.Combine(fixture.Root, "link"), "/etc/passwd");
        await Assert.ThrowsAnyAsync<Exception>(() => CloudConflictLocalContent.ObserveAsync(fixture.Root, "link", default));
        await Assert.ThrowsAnyAsync<Exception>(() => CloudConflictLocalContent.ObserveAsync(fixture.Root, "../etc/passwd", default));
        Directory.CreateDirectory(Path.Combine(fixture.Root, "directory"));
        File.CreateSymbolicLink(Path.Combine(fixture.Root, "directory", "nested"), "/etc/passwd");
        await Assert.ThrowsAnyAsync<Exception>(() => CloudConflictLocalContent.ObserveAsync(fixture.Root, "directory/nested", default));
        await Assert.ThrowsAnyAsync<Exception>(() => CloudConflictLocalContent.CaptureMetadataAsync(fixture.Root, default));
    }

    [Fact]
    public async Task MetadataMatchesObservationAndRejectsFifo()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "track"), "payload");
        var observed = await CloudConflictLocalContent.ObserveAsync(fixture.Root, "track", default);
        var metadata = Assert.Single(await CloudConflictLocalContent.CaptureMetadataAsync(fixture.Root, default));
        Assert.Equal(observed.Identity, metadata.Identity);
        Assert.Equal(observed.Size, metadata.Size);
        Assert.Equal(File.GetLastWriteTimeUtc(Path.Combine(fixture.Root, "track")).Ticks, metadata.LastWriteTimeUtcTicks);
        using var create = System.Diagnostics.Process.Start("mkfifo", Path.Combine(fixture.Root, "fifo"))!;
        Assert.True(create.WaitForExit(5000));
        Assert.Equal(0, create.ExitCode);
        await Assert.ThrowsAnyAsync<Exception>(() => CloudConflictLocalContent.ObserveAsync(fixture.Root, "fifo", default));
    }

    [Fact]
    public async Task ValidatesImageContainersAndRejectsTruncationAndBadCrc()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture();
        await File.WriteAllBytesAsync(Path.Combine(fixture.Root, "image.jpg"), Jpeg);
        await File.WriteAllBytesAsync(Path.Combine(fixture.Root, "image.png"), Png);
        Assert.Equal(CloudConflictContentKind.Jpeg, (await CloudConflictLocalContent.ObserveAsync(fixture.Root, "image.jpg", default)).Kind);
        Assert.Equal(CloudConflictContentKind.Png, (await CloudConflictLocalContent.ObserveAsync(fixture.Root, "image.png", default)).Kind);
        await File.WriteAllBytesAsync(Path.Combine(fixture.Root, "fake.jpg"), [255, 216, 255, 1]);
        await File.WriteAllBytesAsync(Path.Combine(fixture.Root, "truncated.jpg"), Jpeg[..^2]);
        var badPng = Png.ToArray(); badPng[50] ^= 1;
        await File.WriteAllBytesAsync(Path.Combine(fixture.Root, "bad.png"), badPng);
        Assert.Equal(CloudConflictContentKind.Unknown, (await CloudConflictLocalContent.ObserveAsync(fixture.Root, "fake.jpg", default)).Kind);
        Assert.Equal(CloudConflictContentKind.Unknown, (await CloudConflictLocalContent.ObserveAsync(fixture.Root, "truncated.jpg", default)).Kind);
        Assert.Equal(CloudConflictContentKind.Unknown, (await CloudConflictLocalContent.ObserveAsync(fixture.Root, "bad.png", default)).Kind);
    }

    [Fact]
    public async Task CopyRejectsChangedSourceAndWrongExistingCache()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture();
        var source = Path.Combine(fixture.Root, "cover.jpg");
        var cache = Path.Combine(fixture.Cache, "payload");
        await File.WriteAllBytesAsync(source, Jpeg);
        var expected = await CloudConflictLocalContent.ObserveAsync(fixture.Root, "cover.jpg", default);
        Assert.Equal(CloudConflictContentKind.Jpeg, expected.Kind);
        await File.WriteAllTextAsync(cache, "wrong");
        await Assert.ThrowsAnyAsync<Exception>(() => CloudConflictLocalContent.CopyVerifiedAsync(fixture.Root, "cover.jpg", expected, cache, default));
        Assert.Equal("wrong", await File.ReadAllTextAsync(cache));
        File.Delete(cache);
        await File.WriteAllTextAsync(source, "changed");
        await Assert.ThrowsAnyAsync<Exception>(() => CloudConflictLocalContent.CopyVerifiedAsync(fixture.Root, "cover.jpg", expected, cache, default));
        Assert.False(File.Exists(cache));
    }

    [Fact]
    public async Task CopiesAndReusesPrivateCacheAndHonorsCancellation()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "track"), "payload");
        var expected = await CloudConflictLocalContent.ObserveAsync(fixture.Root, "track", default);
        var cache = Path.Combine(fixture.Cache, "payload");
        await CloudConflictLocalContent.CopyVerifiedAsync(fixture.Root, "track", expected, cache, default);
        await CloudConflictLocalContent.CopyVerifiedAsync(fixture.Root, "track", expected, cache, default);
        Assert.Equal(expected.Sha256, (await CloudConflictLocalContent.ReadCacheAsync(cache, default)).Sha256);
        Assert.Equal("payload", await File.ReadAllTextAsync(Path.Combine(fixture.Root, "track")));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(cache) &
            (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite));
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CloudConflictLocalContent.ObserveAsync(fixture.Root, "track", cts.Token));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _base = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        public string Root => Path.Combine(_base, "root");
        public string Cache => Path.Combine(_base, "cache");
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(Cache);
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(Cache, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        public void Dispose() => Directory.Delete(_base, true);
    }
}
