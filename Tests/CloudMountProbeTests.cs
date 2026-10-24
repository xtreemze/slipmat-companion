using Jellyfin.Plugin.AudioGateway.Services;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public sealed class CloudMountProbeTests
{
    private const string ExampleMount =
        "41 36 0:75 / /mnt/music rw,nosuid,nodev - fuse.rclone tele2:Music rw,user_id=997";

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
