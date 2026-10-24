using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Services;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public sealed class CloudMountProbeTests
{
    private const string ExampleMount =
        "41 36 0:75 / /mnt/music rw,nosuid,nodev - fuse.rclone tele2:Music rw,user_id=997";

    [Fact]
    public void MountedRemote_MetadataProbeNeverAccessesFuseDirectory()
    {
        var result = LinuxCloudMountProbe.InspectMountInfo(
            [ExampleMount], "/mnt/music", "tele2:Music");

        Assert.True(result.Active);
        Assert.True(result.Owned);
        Assert.False(result.Ready);
        Assert.False(result.HasFiles);
        Assert.Equal("mount-read-not-checked", result.Code);
    }

    [Fact]
    public async Task BoundedDirectoryProbe_AcceptsReadableEmptyAndPopulatedDirectories()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var path = Directory.CreateTempSubdirectory("slipmat-cloud-probe").FullName;
        try
        {
            var probe = new LinuxCloudMountProbe(
                "find", TimeSpan.FromSeconds(2));
            var empty = await probe.CheckDirectoryAsync(path, CancellationToken.None);
            Assert.True(empty.Readable);
            Assert.False(empty.HasFiles);
            Assert.Equal("mount-ready", empty.Code);

            File.WriteAllText(Path.Combine(path, "track.flac"), "fixture");
            var populated = await probe.CheckDirectoryAsync(path, CancellationToken.None);
            Assert.True(populated.Readable);
            Assert.True(populated.HasFiles);
            Assert.Equal("mount-ready", populated.Code);
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public async Task BoundedDirectoryProbe_ReportsMissingHelperWithoutThrowing()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var probe = new LinuxCloudMountProbe(
            "/slipmat-does-not-exist/missing-find", TimeSpan.FromMilliseconds(200));
        var result = await probe.CheckDirectoryAsync("/mnt/music", CancellationToken.None);
        Assert.False(result.Readable);
        Assert.Equal("mount-probe-helper-unavailable", result.Code);
    }

    [Fact]
    public async Task BoundedDirectoryProbe_TimesOutAndKillsAStalledChild()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var root = Directory.CreateTempSubdirectory("slipmat-cloud-timeout").FullName;
        try
        {
            var helper = Path.Combine(root, "find-test");
            File.WriteAllText(helper, "#!/bin/sh\nsleep 10\n");
            File.SetUnixFileMode(helper,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var probe = new LinuxCloudMountProbe(
                helper, TimeSpan.FromMilliseconds(150));
            var stopwatch = Stopwatch.StartNew();
            var result = await probe.CheckDirectoryAsync(root, CancellationToken.None);
            stopwatch.Stop();

            Assert.Equal("mount-probe-timeout", result.Code);
            Assert.False(result.Readable);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EmptyMountedRemote_IsReadyWhenDirectoryEnumerationSucceeds()
    {
        var result = LinuxCloudMountProbe.InspectMountInfo(
            [ExampleMount],
            "/mnt/music",
            "tele2:Music",
            _ => true);

        Assert.True(result.Ready);
        Assert.True(result.Active);
        Assert.True(result.Readable);
        Assert.Equal("mount-ready", result.Code);
    }

    [Fact]
    public void MountedRcloneForDifferentRemote_IsForeign()
    {
        var result = LinuxCloudMountProbe.InspectMountInfo(
            [ExampleMount],
            "/mnt/music",
            "other:Music",
            _ => true);

        Assert.True(result.Active);
        Assert.False(result.Owned);
        Assert.False(result.Ready);
        Assert.Equal("mount-foreign-filesystem", result.Code);
    }

    [Fact]
    public void OrdinaryDirectoryWithMediaFiles_IsNotAMount()
    {
        var result = LinuxCloudMountProbe.InspectMountInfo(
            [],
            "/mnt/music",
            "tele2:Music",
            _ => true);

        Assert.False(result.Active);
        Assert.False(result.Ready);
        Assert.Equal("mount-not-active", result.Code);
    }

    [Fact]
    public void MountedForeignFilesystem_IsNotAnOwnedRcloneMount()
    {
        var result = LinuxCloudMountProbe.InspectMountInfo(
            ["41 36 0:75 / /mnt/music rw - ext4 /dev/sda2 rw"],
            "/mnt/music",
            "tele2:Music",
            _ => true);

        Assert.True(result.Active);
        Assert.False(result.Ready);
        Assert.Equal("mount-foreign-filesystem", result.Code);
    }

    [Fact]
    public void OverMountedRclone_IsNotMistakenForVisibleFilesystem()
    {
        var result = LinuxCloudMountProbe.InspectMountInfo(
            [
                ExampleMount,
                "42 41 0:78 / /mnt/music rw - tmpfs tmpfs rw",
            ],
            "/mnt/music",
            "tele2:Music",
            _ => true);

        Assert.True(result.Active);
        Assert.False(result.Ready);
        Assert.Equal("mount-foreign-filesystem", result.Code);
    }

    [Fact]
    public void EscapedMountpoint_IsComparedAfterDecoding()
    {
        var result = LinuxCloudMountProbe.InspectMountInfo(
            [@"41 36 0:75 / /mnt/cloud\040music rw - fuse.rclone tele2:Music rw"],
            "/mnt/cloud music",
            "tele2:Music",
            _ => true);

        Assert.True(result.Ready);
    }

    [Fact]
    public void UnreadableMountedRemote_IsNotReady()
    {
        var result = LinuxCloudMountProbe.InspectMountInfo(
            [ExampleMount],
            "/mnt/music",
            "tele2:Music",
            _ => false);

        Assert.True(result.Active);
        Assert.True(result.Owned);
        Assert.False(result.Ready);
        Assert.Equal("mount-read-failed", result.Code);
    }
}
