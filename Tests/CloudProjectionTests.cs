using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Configuration;
using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public class CloudProjectionTests
{
    private const string RemotesJson =
        "[{\"name\":\"tele2:\",\"type\":\"jottacloud\",\"description\":\"Tele2 Cloud\",\"source\":\"file\"}]";

    private const string ExpiredOAuthError =
        "couldn't fetch token: invalid_grant: maybe token expired? - try refreshing with \"rclone config reconnect tele2:\"";

    [Fact]
    public async Task RcloneHost_StatusAndListing_UseStructuredAllowlistedCommands()
    {
        var runner = new FakeRunner(
            Result(0, "rclone v1.71.2\n"),
            Result(0, RemotesJson),
            Result(
                0,
                "[{\"Path\":\"Music\",\"Name\":\"Music\",\"Size\":-1,\"IsDir\":true}," +
                "{\"Path\":\"cover.jpg\",\"Name\":\"cover.jpg\",\"Size\":42,\"IsDir\":false}]"));

        var host = new RcloneCliHost(runner: runner);

        var status = await host.GetStatusAsync();
        var listing = await host.ListAsync("tele2", "Media");

        Assert.Equal(RcloneCliHealth.Ready, status.Health);
        Assert.Equal("1.71.2", status.Version);
        var remote = Assert.Single(status.Remotes);
        Assert.Equal("tele2", remote.Name);
        Assert.Equal("jottacloud", remote.Type);

        Assert.True(listing.Success);
        Assert.Equal(2, listing.Entries.Count);
        Assert.True(listing.Entries[0].IsDirectory);
        Assert.Equal("Music", listing.Entries[0].Name);

        Assert.Equal(new[] { "version" }, runner.Invocations[0].Arguments);
        Assert.Equal(
            new[] { "listremotes", "--long", "--json" },
            runner.Invocations[1].Arguments);
        Assert.Equal(
            new[] { "lsjson", "tele2:Media", "--no-modtime", "--no-mimetype" },
            runner.Invocations[2].Arguments);
    }

    [Fact]
    public void RcloneHost_RemoteParser_AcceptsObjectWrapper()
    {
        const string json =
            "{\"remotes\":[{\"name\":\"drive:\",\"type\":\"drive\",\"description\":\"Media\"}]}";

        Assert.True(RcloneCliHost.TryParseRemotes(json, out var remotes));

        var remote = Assert.Single(remotes);
        Assert.Equal("drive", remote.Name);
        Assert.Equal("drive", remote.Type);
        Assert.Equal("Media", remote.Description);
    }

    [Theory]
    [InlineData("bad:name")]
    [InlineData("--config")]
    [InlineData("bad\nname")]
    [InlineData("bad\0name")]
    public void RcloneHost_RejectsUnsafeRemoteNames(string remote)
    {
        Assert.Throws<ArgumentException>(() => RcloneCliHost.NormalizeRemoteName(remote));
    }

    [Theory]
    [InlineData("../other")]
    [InlineData("folder/../other")]
    [InlineData("folder\nother")]
    [InlineData("folder\0other")]
    public void RcloneHost_RejectsUnsafeRemotePaths(string path)
    {
        Assert.Throws<ArgumentException>(() => RcloneCliHost.NormalizeRemotePath(path));
    }

    [Fact]
    public async Task RcloneHost_Probe_UsesBoundedLsjsonStat()
    {
        var runner = new FakeRunner(Result(0, "{\"Path\":\"Media/Music\",\"IsDir\":true}"));
        var host = new RcloneCliHost(runner: runner);

        var probe = await host.ProbeAsync("tele2", "Media/Music");

        Assert.True(probe.Success);
        Assert.Equal(
            new[]
            {
                "lsjson",
                "tele2:Media/Music",
                "--stat",
                "--no-modtime",
                "--no-mimetype",
            },
            runner.Invocations[0].Arguments);
    }

    [Fact]
    public async Task RcloneHost_ExpiredOAuth_ClassifiesAuthenticationRequired()
    {
        var runner = new FakeRunner(Result(1, stderr: ExpiredOAuthError));
        var host = new RcloneCliHost(runner: runner);

        var listing = await host.ListAsync("tele2", string.Empty);

        Assert.False(listing.Success);
        Assert.Equal("remote-authentication-required", listing.ErrorCode);
    }

    [Fact]
    public async Task RcloneHost_MigrationTransfer_UsesOnlyAdditiveCopyAndExactCheck()
    {
        using var temp = new TempDirectory();
        var localPath = Path.Combine(temp.Path, "library");
        Directory.CreateDirectory(localPath);
        var runner = new FakeRunner(Result(0), Result(0), Result(0));
        var host = new RcloneCliHost(runner: runner);

        await host.CopyLocalToRemoteAsync(localPath, "tele2", "Media/Music");
        await host.CheckLocalAndRemoteByDownloadAsync(localPath, "tele2", "Media/Music");
        await host.CopyRemoteToLocalAsync("tele2", "Media/Music", Path.Combine(temp.Path, "restore"));

        Assert.Equal("copy", runner.Invocations[0].Arguments[0]);
        Assert.Equal(Path.GetFullPath(localPath), runner.Invocations[0].Arguments[1]);
        Assert.Equal("tele2:Media/Music", runner.Invocations[0].Arguments[2]);
        Assert.Contains("--immutable", runner.Invocations[0].Arguments);

        Assert.Equal("check", runner.Invocations[1].Arguments[0]);
        Assert.Contains("--download", runner.Invocations[1].Arguments);
        Assert.Equal(Path.GetFullPath(localPath), runner.Invocations[1].Arguments[1]);
        Assert.Equal("tele2:Media/Music", runner.Invocations[1].Arguments[2]);
        Assert.DoesNotContain("--one-way", runner.Invocations[1].Arguments);

        Assert.Equal("copy", runner.Invocations[2].Arguments[0]);
        Assert.Equal("tele2:Media/Music", runner.Invocations[2].Arguments[1]);

        Assert.DoesNotContain(
            runner.Invocations.SelectMany(invocation => invocation.Arguments),
            argument => argument is "sync" or "move" or "delete" or "purge" or "rc" or "rcd");
    }

    [Fact]
    public async Task GetStatus_SelectedRemoteExpiredOAuth_IsDegradedButKeepsLocalProjectionState()
    {
        using var temp = new TempDirectory();
        var projectionPath = Path.Combine(temp.Path, "projection");
        Directory.CreateDirectory(projectionPath);

        var config = EnabledConfig(projectionPath);
        var runner = HealthyRunner(Result(1, stderr: ExpiredOAuthError));
        var service = CreateService(config, runner, new FakeLibraryProjection());

        var status = await service.GetStatusAsync();

        Assert.Equal("degraded", status.Health);
        Assert.Equal("remote-authentication-required", status.Code);
        Assert.Equal("tele2", status.SelectedRemote);
        Assert.Equal("jottacloud", status.SelectedRemoteType);
        Assert.Equal(projectionPath, status.ProjectionPath);
    }

    [Fact]
    public async Task GetStatus_LegacyMaterializedProjection_ReportsActionableBlocker()
    {
        using var temp = new TempDirectory();
        var projectionPath = Path.Combine(temp.Path, "projection");
        Directory.CreateDirectory(projectionPath);
        var legacyMarker = Path.Combine(projectionPath, ".slipmat-rclone-cloud-projection.json");
        File.WriteAllText(legacyMarker, "{}");

        var runner = HealthyRunner(Result(0));
        var service = CreateService(
            EnabledConfig(projectionPath),
            runner,
            new FakeLibraryProjection());

        var status = await service.GetStatusAsync();

        Assert.Equal("degraded", status.Health);
        Assert.Equal("legacy-materialized-projection-present", status.Code);
        Assert.False(status.ProjectionPathReady);
        Assert.False(status.LibraryReady);
        Assert.True(File.Exists(legacyMarker));
        Assert.DoesNotContain(runner.Invocations, invocation =>
            invocation.Arguments.Count > 0 && invocation.Arguments[0] == "mount");
    }

    [Fact]
    public void SuggestRecoveryPath_LegacyMediaRemainsUntouched()
    {
        using var temp = new TempDirectory();
        var oldPath = Path.Combine(temp.Path, "projection");
        Directory.CreateDirectory(oldPath);
        var oldMedia = Path.Combine(oldPath, "keep.flac");
        File.WriteAllText(oldMedia, "local");
        File.WriteAllText(
            Path.Combine(oldPath, ".slipmat-rclone-cloud-projection.json"), "{}");

        var runner = new FakeRunner();
        var service = CreateService(
            EnabledConfig(oldPath), runner, new FakeLibraryProjection(),
            new SimulatedMountProbe(neverMounts: true));

        var suggested = service.SuggestRecoveryPath(null);

        Assert.Equal("safe-empty-recovery-path-available", suggested.Code);
        Assert.Equal(oldPath, suggested.BlockedPath);
        Assert.Equal(string.Concat(oldPath, "-vfs"), suggested.SuggestedPath);
        Assert.Equal("local", File.ReadAllText(oldMedia));
        Assert.False(Directory.Exists(suggested.SuggestedPath));
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public void SuggestRecoveryPath_SkipsOccupiedCandidateAndItsMountMarker()
    {
        using var temp = new TempDirectory();
        var oldPath = Path.Combine(temp.Path, "projection");
        Directory.CreateDirectory(oldPath);
        File.WriteAllText(
            Path.Combine(oldPath, ".slipmat-rclone-cloud-projection.json"), "{}");
        Directory.CreateDirectory(string.Concat(oldPath, "-vfs"));
        File.WriteAllText(
            string.Concat(oldPath, "-vfs-2.slipmat-rclone-cloud-mount.json"), "{}");

        var suggested = CreateService(
            EnabledConfig(oldPath), new FakeRunner(), new FakeLibraryProjection(),
            new SimulatedMountProbe(neverMounts: true)).SuggestRecoveryPath(null);

        Assert.Equal(string.Concat(oldPath, "-vfs-3"), suggested.SuggestedPath);
        Assert.True(Directory.Exists(string.Concat(oldPath, "-vfs")));
    }

    [Fact]
    public void SuggestRecoveryPath_RejectsLiveLegacyMountAndUnmarkedPath()
    {
        using var temp = new TempDirectory();
        var oldPath = Path.Combine(temp.Path, "projection");
        Directory.CreateDirectory(oldPath);
        var config = EnabledConfig(oldPath);
        var noMarker = CreateService(
            config, new FakeRunner(), new FakeLibraryProjection(),
            new SimulatedMountProbe(neverMounts: true)).SuggestRecoveryPath(null);
        Assert.Equal("legacy-marker-not-present", noMarker.Code);
        Assert.Null(noMarker.SuggestedPath);

        File.WriteAllText(
            Path.Combine(oldPath, ".slipmat-rclone-cloud-projection.json"), "{}");
        var active = CreateService(
            config, new FakeRunner(), new FakeLibraryProjection(),
            new SimulatedMountProbe(initiallyMounted: true)).SuggestRecoveryPath(null);
        Assert.Equal("recovery-original-mount-not-idle", active.Code);
        Assert.Null(active.SuggestedPath);
    }

    [Fact]
    public async Task GetStatus_WhenMountedFuseProbeTimesOut_IsDegradedAndDoesNotScan()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "projection");
        // The adjacent marker identifies the expected mount; the probe
        // simulates the kernel namespace and an unresponsive FUSE readdir.
        Directory.CreateDirectory(path);
        File.WriteAllText(
            string.Concat(path, ".slipmat-rclone-cloud-mount.json"),
            "{\"Version\":2,\"RemoteName\":\"tele2\",\"RemoteType\":\"jottacloud\",\"RemotePath\":\"Media/Music\"}");
        var library = new FakeLibraryProjection();
        var status = await CreateService(
            EnabledConfig(path), HealthyRunner(Result(0)), library,
            new TimeoutMountProbe()).GetStatusAsync();

        Assert.Equal("degraded", status.Health);
        Assert.Equal("mount-probe-timeout", status.Code);
        Assert.False(status.ProjectionPathReady);
        Assert.False(status.LibraryReady);
        Assert.Equal(0, library.ScanCalls);
    }

    [Fact]
    public async Task Reconcile_WhenFuseReadTimesOut_DoesNotStartSecondRclone()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "projection");
        Directory.CreateDirectory(path);
        File.WriteAllText(
            string.Concat(path, ".slipmat-rclone-cloud-mount.json"),
            "{\"Version\":2,\"RemoteName\":\"tele2\",\"RemoteType\":\"jottacloud\",\"RemotePath\":\"Media/Music\"}");
        var runner = HealthyRunner(Result(0));
        var library = new FakeLibraryProjection();
        var result = await CreateService(
            EnabledConfig(path), runner, library, new TimeoutMountProbe())
            .ReconcileAsync();

        Assert.Equal("blocked", result.Status);
        Assert.Equal("mount-probe-timeout", result.Code);
        Assert.DoesNotContain(runner.Invocations, invocation =>
            invocation.Arguments.Count > 0 && invocation.Arguments[0] == "mount");
        Assert.Equal(0, library.ScanCalls);
    }

    [Fact]
    public async Task GetStatus_UnownedProjectionPath_ReportsOwnershipFailure()
    {
        using var temp = new TempDirectory();
        var projectionPath = Path.Combine(temp.Path, "projection");
        Directory.CreateDirectory(projectionPath);
        File.WriteAllText(Path.Combine(projectionPath, "keep.flac"), "local");

        var status = await CreateService(
            EnabledConfig(projectionPath),
            HealthyRunner(Result(0)),
            new FakeLibraryProjection()).GetStatusAsync();

        Assert.Equal("degraded", status.Health);
        Assert.Equal("projection-path-not-owned", status.Code);
        Assert.False(status.ProjectionPathReady);
        Assert.False(status.LibraryReady);
    }

    [Fact]
    public async Task GetStatus_DisabledSelectedRemoteExpiredOAuth_StillRequiresReconnect()
    {
        var config = new PluginConfiguration
        {
            CloudProjectionEnabled = false,
            CloudRemoteName = "tele2",
            CloudRemotePath = string.Empty,
            CloudLibraryName = "Cloud Media",
            CloudCollectionType = "music",
        };
        var runner = HealthyRunner(Result(1, stderr: ExpiredOAuthError));
        var service = CreateService(config, runner, new FakeLibraryProjection());

        var status = await service.GetStatusAsync();

        Assert.False(status.Enabled);
        Assert.Equal("degraded", status.Health);
        Assert.Equal("remote-authentication-required", status.Code);
        Assert.Equal("jottacloud", status.SelectedRemoteType);
    }

    [Fact]
    public async Task CreateDirectory_ExpiredOAuth_ReturnsAuthenticationRequired()
    {
        var runner = HealthyRunner(Result(1, stderr: ExpiredOAuthError));
        var service = CreateService(
            new PluginConfiguration(),
            runner,
            new FakeLibraryProjection());

        var code = await service.CreateDirectoryAsync("tele2", "Media/New");

        Assert.Equal("remote-authentication-required", code);
    }

    [Fact]
    public async Task Reconcile_ExpiredOAuth_ReturnsAuthenticationRequired()
    {
        using var temp = new TempDirectory();
        var projectionPath = Path.Combine(temp.Path, "projection");
        var config = EnabledConfig(projectionPath);
        var runner = HealthyRunner(Result(1, stderr: ExpiredOAuthError));
        var service = CreateService(config, runner, new FakeLibraryProjection());

        var result = await service.ReconcileAsync();

        Assert.Equal("failed", result.Status);
        Assert.Equal("remote-authentication-required", result.Code);
        Assert.False(result.MountReady);
    }

    [Fact]
    public async Task CreateDirectory_UsesRcloneMkdirOnly()
    {
        var runner = HealthyRunner(Result(0));
        var service = CreateService(
            new PluginConfiguration(),
            runner,
            new FakeLibraryProjection());

        var code = await service.CreateDirectoryAsync("tele2", "Media/New");

        Assert.Equal("created", code);
        Assert.Equal(
            new[] { "mkdir", "tele2:Media/New" },
            runner.Invocations[^1].Arguments);
    }

    [Fact]
    public async Task Unmount_EnabledProfile_RequiresDisableAndSave()
    {
        using var temp = new TempDirectory();
        var runner = new FakeRunner();
        var code = await CreateService(
            EnabledConfig(Path.Combine(temp.Path, "projection")),
            runner,
            new FakeLibraryProjection())
            .UnmountAsync("legacy");

        Assert.Equal("disable-projection-before-unmount", code);
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public async Task Unmount_AttachedJellyfinLibrary_FailsWithoutCallingUnmountHelper()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "projection");
        var config = EnabledConfig(path);
        var library = new FakeLibraryProjection();
        await CreateService(
            config, HealthyRunner(Result(0), Result(0)), library)
            .ReconcileAsync();

        config.CloudProjectionEnabled = false;
        var runner = HealthyRunner();
        var code = await CreateService(
            config, runner, library, new SimulatedMountProbe(initiallyMounted: true))
            .UnmountAsync("legacy");

        Assert.Equal("library-still-attached", code);
        Assert.Equal(2, runner.Invocations.Count);
    }

    [Fact]
    public async Task Unmount_DisabledDetachedOwnedMount_OnlyUnmountsAndRetainsCache()
    {
        if (!OperatingSystem.IsLinux()
            && !OperatingSystem.IsMacOS()
            && !OperatingSystem.IsFreeBSD())
        {
            return;
        }

        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "projection");
        Directory.CreateDirectory(path);
        var config = EnabledConfig(path);
        config.CloudProjectionEnabled = false;
        File.WriteAllText(
            string.Concat(path, ".slipmat-rclone-cloud-mount.json"),
            "{\"Version\":2,\"RemoteName\":\"tele2\",\"RemoteType\":\"jottacloud\",\"RemotePath\":\"Media/Music\"}");
        var cache = Path.Combine(temp.Path, "cache");
        Directory.CreateDirectory(cache);
        File.WriteAllText(Path.Combine(cache, "retain.bin"), "cache");
        var runner = HealthyRunner(Result(0));

        var code = await CreateService(
            config, runner, new FakeLibraryProjection(), new UnmountSequenceProbe())
            .UnmountAsync("legacy");

        Assert.Equal("unmounted", code);
        Assert.False(File.Exists(string.Concat(path, ".slipmat-rclone-cloud-mount.json")));
        Assert.Equal("cache", File.ReadAllText(Path.Combine(cache, "retain.bin")));
        Assert.Equal(
            OperatingSystem.IsLinux() ? "fusermount3" : "umount",
            runner.Invocations[^1].Executable);
    }

    [Fact]
    public async Task Reconcile_EmptyOwnedRoot_StartsBoundedReadOnlyVfsMount()
    {
        using var temp = new TempDirectory();
        var projectionPath = Path.Combine(temp.Path, "projection");
        var config = EnabledConfig(projectionPath);
        var runner = HealthyRunner(Result(0), Result(0));
        var library = new FakeLibraryProjection();
        var service = CreateService(config, runner, library);

        var result = await service.ReconcileAsync();

        Assert.Equal("scan-queued", result.Status);
        Assert.Equal("mount-ready-scan-queued", result.Code);
        Assert.True(result.MountReady);
        Assert.True(File.Exists(
            string.Concat(projectionPath, ".slipmat-rclone-cloud-mount.json")));

        var mount = runner.Invocations[^1].Arguments;
        Assert.Equal("mount", mount[0]);
        Assert.Equal("tele2:Media/Music", mount[1]);
        Assert.Equal(projectionPath, mount[2]);
        Assert.Contains("--devname=tele2:Media/Music", mount);
        Assert.Contains("--read-only", mount);
        Assert.Contains("--vfs-cache-mode=full", mount);
        Assert.Contains("--vfs-cache-max-size=16G", mount);
        Assert.Contains("--vfs-cache-max-age=24h", mount);
        Assert.Contains("--vfs-cache-min-free-space=4G", mount);
        // Full-mode read-ahead/buffering makes every probe download the whole
        // object; scan egress must stay bounded per open.
        Assert.Contains("--vfs-read-ahead=0", mount);
        Assert.Contains("--buffer-size=0", mount);
        Assert.Contains("--vfs-read-chunk-size=4M", mount);
        Assert.Contains("--vfs-read-chunk-size-limit=4M", mount);
        Assert.DoesNotContain(
            runner.Invocations.SelectMany(invocation => invocation.Arguments),
            argument => argument is "copy" or "sync" or "move" or "delete" or "purge" or "rc" or "rcd");
    }

    [Fact]
    public async Task Reconcile_MountCommandSucceedsButNoFuseMount_FailsWithoutCreatingMarker()
    {
        using var temp = new TempDirectory();
        var projectionPath = Path.Combine(temp.Path, "projection");
        var library = new FakeLibraryProjection();
        var service = CreateService(
            EnabledConfig(projectionPath),
            HealthyRunner(Result(0), Result(0)),
            library,
            new SimulatedMountProbe(neverMounts: true));

        var result = await service.ReconcileAsync();

        Assert.Equal("failed", result.Status);
        Assert.Equal("mount-not-active", result.Code);
        Assert.False(result.MountReady);
        Assert.Equal(0, library.EnsureCalls);
        Assert.False(File.Exists(
            string.Concat(projectionPath, ".slipmat-rclone-cloud-mount.json")));
    }

    [Fact]
    public async Task GetStatus_StaleMarkerAndLocalMedia_AreNotMisreportedAsMounted()
    {
        using var temp = new TempDirectory();
        var projectionPath = Path.Combine(temp.Path, "projection");
        Directory.CreateDirectory(projectionPath);
        File.WriteAllText(Path.Combine(projectionPath, "local.flac"), "retain");
        File.WriteAllText(
            string.Concat(projectionPath, ".slipmat-rclone-cloud-mount.json"),
            "{\"Version\":2,\"RemoteName\":\"tele2\",\"RemoteType\":\"jottacloud\",\"RemotePath\":\"Media/Music\"}");

        var service = CreateService(
            EnabledConfig(projectionPath),
            HealthyRunner(Result(0)),
            new FakeLibraryProjection(),
            new SimulatedMountProbe(neverMounts: true));
        var status = await service.GetStatusAsync();

        Assert.Equal("degraded", status.Health);
        Assert.Equal("mount-not-active", status.Code);
        Assert.False(status.ProjectionPathReady);
        Assert.False(status.LibraryReady);
        Assert.True(File.Exists(Path.Combine(projectionPath, "local.flac")));
    }

    [Fact]
    public async Task Reconcile_StaleMarkerWithLocalMedia_RefusesToHideFiles()
    {
        using var temp = new TempDirectory();
        var projectionPath = Path.Combine(temp.Path, "projection");
        Directory.CreateDirectory(projectionPath);
        File.WriteAllText(Path.Combine(projectionPath, "local.flac"), "retain");
        File.WriteAllText(
            string.Concat(projectionPath, ".slipmat-rclone-cloud-mount.json"),
            "{\"Version\":2,\"RemoteName\":\"tele2\",\"RemoteType\":\"jottacloud\",\"RemotePath\":\"Media/Music\"}");

        var runner = HealthyRunner(Result(0));
        var result = await CreateService(
            EnabledConfig(projectionPath),
            runner,
            new FakeLibraryProjection(),
            new SimulatedMountProbe(neverMounts: true)).ReconcileAsync();

        Assert.Equal("blocked", result.Status);
        Assert.Equal("projection-path-not-empty-unmounted", result.Code);
        Assert.DoesNotContain(runner.Invocations, invocation =>
            invocation.Arguments.Count > 0 && invocation.Arguments[0] == "mount");
        Assert.Equal("retain", File.ReadAllText(Path.Combine(projectionPath, "local.flac")));
    }

    [Fact]
    public async Task Reconcile_EmptyMountedRemote_DoesNotRestartTheDaemon()
    {
        using var temp = new TempDirectory();
        var projectionPath = Path.Combine(temp.Path, "projection");
        var config = EnabledConfig(projectionPath);
        var library = new FakeLibraryProjection();
        await CreateService(config, HealthyRunner(Result(0), Result(0)), library)
            .ReconcileAsync();

        var runner = HealthyRunner(Result(0));
        var service = CreateService(
            config, runner, library, new SimulatedMountProbe(initiallyMounted: true));
        var result = await service.ReconcileAsync();

        Assert.True(result.MountReady);
        Assert.True(result.LibraryReady);
        Assert.DoesNotContain(runner.Invocations, invocation =>
            invocation.Arguments.Count > 0 && invocation.Arguments[0] == "mount");

        var statusRunner = HealthyRunner(Result(0));
        var status = await CreateService(
            config, statusRunner, library, new SimulatedMountProbe(initiallyMounted: true))
            .GetStatusAsync();
        Assert.True(status.ProjectionPathReady);
        Assert.False(status.ProjectionHasFiles);
        Assert.True(status.LibraryReady);
    }

    [Fact]
    public async Task Reconcile_ReadyMount_CreatesLibraryAndQueuesScanWithoutRemounting()
    {
        using var temp = new TempDirectory();
        var projectionPath = Path.Combine(temp.Path, "projection");
        var config = EnabledConfig(projectionPath);
        var library = new FakeLibraryProjection();

        var firstRunner = HealthyRunner(Result(0), Result(0));
        await CreateService(config, firstRunner, library).ReconcileAsync();
        File.WriteAllText(Path.Combine(projectionPath, "track.flac"), "fixture");

        var secondRunner = HealthyRunner(Result(0));
        var result = await CreateService(
            config, secondRunner, library, new SimulatedMountProbe(initiallyMounted: true))
            .ReconcileAsync();

        Assert.Equal("scan-queued", result.Status);
        Assert.True(result.MountReady);
        Assert.True(result.LibraryReady);
        Assert.True(result.LibraryScanQueued);
        Assert.DoesNotContain(
            secondRunner.Invocations,
            invocation => invocation.Arguments.Count > 0 && invocation.Arguments[0] == "mount");
        Assert.Equal("Cloud Music", library.LastLibraryName);
        Assert.Equal(CollectionTypeOptions.music, library.LastCollectionType);
    }

    [Fact]
    public async Task Reconcile_WhenLibraryScanIsSuppressed_DoesNotQueueCatalogScan()
    {
        using var temp = new TempDirectory();
        var projectionPath = Path.Combine(temp.Path, "projection");
        var config = EnabledConfig(projectionPath);
        var library = new FakeLibraryProjection();
        var service = CreateService(
            config,
            HealthyRunner(Result(0), Result(0)),
            library);

        var result = await service.ReconcileAsync(
            projectionId: null,
            cancellationToken: default,
            queueLibraryScan: false);

        Assert.Equal("completed", result.Status);
        Assert.Equal("mount-ready-library-ready-no-scan", result.Code);
        Assert.True(result.MountReady);
        Assert.True(result.LibraryReady);
        Assert.False(result.LibraryScanQueued);
        Assert.Equal(0, library.ScanCalls);
    }

    [Fact]
    public async Task Reconcile_ExistingVfsLibrary_AppliesCloudSafeScanPolicyBeforeScan()
    {
        using var temp = new TempDirectory();
        var projectionPath = Path.Combine(temp.Path, "projection");
        var config = EnabledConfig(projectionPath);
        var library = new FakeLibraryProjection();

        await CreateService(
            config,
            HealthyRunner(Result(0), Result(0)),
            library)
            .ReconcileAsync();
        File.WriteAllText(Path.Combine(projectionPath, "track.flac"), "fixture");

        var result = await CreateService(
            config,
            HealthyRunner(Result(0)),
            library,
            new SimulatedMountProbe(initiallyMounted: true))
            .ReconcileAsync();

        Assert.Equal("scan-queued", result.Status);
        Assert.Equal(1, library.ScanPolicyCalls);
        Assert.Equal(2, library.ScanCalls);
    }

    [Fact]
    public async Task Reconcile_ExistingVfsLibrary_BlocksWhenScanPolicyCannotBeHardened()
    {
        using var temp = new TempDirectory();
        var projectionPath = Path.Combine(temp.Path, "projection");
        var config = EnabledConfig(projectionPath);
        var library = new FakeLibraryProjection();

        await CreateService(
            config,
            HealthyRunner(Result(0), Result(0)),
            library)
            .ReconcileAsync();
        File.WriteAllText(Path.Combine(projectionPath, "track.flac"), "fixture");
        library.ScanPolicyAvailable = false;

        var result = await CreateService(
            config,
            HealthyRunner(Result(0)),
            library,
            new SimulatedMountProbe(initiallyMounted: true))
            .ReconcileAsync();

        Assert.Equal("blocked", result.Status);
        Assert.Equal("library-scan-policy-unavailable", result.Code);
        Assert.Equal(1, library.ScanPolicyCalls);
        Assert.Equal(1, library.ScanCalls);
    }

    [Fact]
    public async Task Reconcile_LegacyMaterializedProjection_FailsClosedWithoutDeletingFiles()
    {
        using var temp = new TempDirectory();
        var projectionPath = Path.Combine(temp.Path, "projection");
        Directory.CreateDirectory(projectionPath);
        var mediaPath = Path.Combine(projectionPath, "keep.flac");
        File.WriteAllText(mediaPath, "legacy-media");
        File.WriteAllText(
            Path.Combine(projectionPath, ".slipmat-rclone-cloud-projection.json"),
            "{}");

        var runner = HealthyRunner(Result(0));
        var result = await CreateService(
            EnabledConfig(projectionPath),
            runner,
            new FakeLibraryProjection())
            .ReconcileAsync();

        Assert.Equal("blocked", result.Status);
        Assert.Equal("legacy-materialized-projection-present", result.Code);
        Assert.True(File.Exists(mediaPath));
        Assert.Equal("legacy-media", File.ReadAllText(mediaPath));
        Assert.DoesNotContain(
            runner.Invocations,
            invocation => invocation.Arguments.Count > 0 && invocation.Arguments[0] == "mount");
    }

    [Fact]
    public async Task Reconcile_NonEmptyUnmanagedDirectory_FailsClosedBeforeMount()
    {
        using var temp = new TempDirectory();
        var projectionPath = Path.Combine(temp.Path, "projection");
        Directory.CreateDirectory(projectionPath);
        File.WriteAllText(Path.Combine(projectionPath, "foreign.txt"), "keep");

        var runner = HealthyRunner(Result(0));
        var service = CreateService(
            EnabledConfig(projectionPath),
            runner,
            new FakeLibraryProjection());

        var result = await service.ReconcileAsync();

        Assert.Equal("blocked", result.Status);
        Assert.Equal("projection-path-not-owned", result.Code);
        Assert.DoesNotContain(
            runner.Invocations,
            invocation => invocation.Arguments.Count > 0 && invocation.Arguments[0] == "mount");
    }

    [Fact]
    public async Task Reconcile_RemoteRootChange_RejectsExistingMountMarker()
    {
        using var temp = new TempDirectory();
        var projectionPath = Path.Combine(temp.Path, "projection");
        var config = EnabledConfig(projectionPath);

        await CreateService(
            config,
            HealthyRunner(Result(0), Result(0)),
            new FakeLibraryProjection())
            .ReconcileAsync();

        var changed = EnabledConfig(projectionPath);
        changed.CloudRemotePath = "Media/Other";

        var runner = HealthyRunner(Result(0));
        var result = await CreateService(
            changed,
            runner,
            new FakeLibraryProjection())
            .ReconcileAsync();

        Assert.Equal("blocked", result.Status);
        Assert.Equal("projection-marker-mismatch", result.Code);
        Assert.DoesNotContain(
            runner.Invocations,
            invocation => invocation.Arguments.Count > 0 && invocation.Arguments[0] == "mount");
    }

    [Fact]
    public async Task Reconcile_SelectedRemoteMissing_FailsBeforeMount()
    {
        using var temp = new TempDirectory();
        var config = EnabledConfig(Path.Combine(temp.Path, "projection"));
        config.CloudRemoteName = "missing";

        var runner = new FakeRunner(
            Result(0, "rclone v1.71.2\n"),
            Result(0, RemotesJson));
        var result = await CreateService(
            config,
            runner,
            new FakeLibraryProjection())
            .ReconcileAsync();

        Assert.Equal("blocked", result.Status);
        Assert.Equal("selected-remote-missing", result.Code);
        Assert.Equal(2, runner.Invocations.Count);
    }

    [Fact]
    public async Task GetStatus_ProfileId_SelectsIndependentVfsLibrary()
    {
        using var temp = new TempDirectory();
        var musicMount = Path.Combine(temp.Path, "music");
        var movieMount = Path.Combine(temp.Path, "movies");
        var config = new PluginConfiguration
        {
            CloudLibraries =
            [
                new CloudLibraryProfile
                {
                    Id = "music",
                    Enabled = true,
                    RemoteName = "tele2",
                    RemotePath = "Media/Music",
                    ProjectionPath = musicMount,
                    CachePath = Path.Combine(temp.Path, "music-cache"),
                    LibraryName = "Cloud Music",
                    CollectionType = "music",
                },
                new CloudLibraryProfile
                {
                    Id = "movies",
                    Enabled = true,
                    RemoteName = "tele2",
                    RemotePath = "Media/Movies",
                    ProjectionPath = movieMount,
                    CachePath = Path.Combine(temp.Path, "movie-cache"),
                    LibraryName = "Cloud Movies",
                    CollectionType = "movies",
                },
            ],
        };

        var service = CreateService(
            config,
            HealthyRunner(Result(0)),
            new FakeLibraryProjection());

        var status = await service.GetStatusAsync("movies");

        Assert.Equal("tele2", status.SelectedRemote);
        Assert.Equal("Media/Movies", status.RemotePath);
        Assert.Equal(movieMount, status.ProjectionPath);
        Assert.Equal("Cloud Movies", status.LibraryName);
        Assert.Equal("movies", status.CollectionType);
    }


    [Fact]
    public async Task GetStatus_ReportsJellyfinScanActivity()
    {
        using var temp = new TempDirectory();
        var projectionPath = Path.Combine(temp.Path, "projection");
        var library = new FakeLibraryProjection { IsScanRunning = true };
        var service = CreateService(
            EnabledConfig(projectionPath),
            HealthyRunner(Result(0)),
            library);

        var status = await service.GetStatusAsync();

        Assert.True(status.JellyfinScanActive);
    }

    [Fact]
    public async Task Reconcile_OverlappingProfileStorage_FailsClosed()
    {
        using var temp = new TempDirectory();
        var shared = Path.Combine(temp.Path, "shared");
        var config = new PluginConfiguration
        {
            CloudLibraries =
            [
                new CloudLibraryProfile
                {
                    Id = "music",
                    Enabled = true,
                    RemoteName = "tele2",
                    RemotePath = "Media/Music",
                    ProjectionPath = shared,
                    CachePath = Path.Combine(temp.Path, "cache-a"),
                    LibraryName = "Cloud Music",
                },
                new CloudLibraryProfile
                {
                    Id = "movies",
                    Enabled = true,
                    RemoteName = "tele2",
                    RemotePath = "Media/Movies",
                    ProjectionPath = shared,
                    CachePath = Path.Combine(temp.Path, "cache-b"),
                    LibraryName = "Cloud Movies",
                    CollectionType = "movies",
                },
            ],
        };

        var service = CreateService(
            config,
            new FakeRunner(),
            new FakeLibraryProjection());

        var result = await service.ReconcileAsync("movies");

        Assert.Equal("blocked", result.Status);
        Assert.Equal("projection-storage-conflict", result.Code);
    }

    [Fact]
    public async Task GetStatus_UnknownProfile_FailsWithoutRcloneMutation()
    {
        var service = CreateService(
            new PluginConfiguration
            {
                CloudLibraries =
                [
                    new CloudLibraryProfile
                    {
                        Id = "music",
                        RemoteName = "tele2",
                        RemotePath = "Media/Music",
                        LibraryName = "Cloud Music",
                    },
                ],
            },
            new FakeRunner(
                Result(0, "rclone v1.71.2\n"),
                Result(0, RemotesJson)),
            new FakeLibraryProjection());

        var status = await service.GetStatusAsync("missing");

        Assert.Equal("projection-not-found", status.Code);
        Assert.False(status.Enabled);
    }

    private static PluginConfiguration EnabledConfig(string projectionPath)
        => new()
        {
            CloudProjectionEnabled = true,
            CloudRemoteName = "tele2",
            CloudRemotePath = "Media/Music",
            CloudProjectionPath = projectionPath,
            CloudLibraryName = "Cloud Music",
            CloudCollectionType = "music",
            CloudAutoCreateLibrary = true,
        };

    private static FakeRunner HealthyRunner(
        params RcloneCommandResult[] afterStatus)
    {
        var results = new List<RcloneCommandResult>
        {
            Result(0, "rclone v1.71.2\n"),
            Result(0, RemotesJson),
        };
        results.AddRange(afterStatus);
        return new FakeRunner(results.ToArray());
    }

    private static CloudProjectionService CreateService(
        PluginConfiguration config,
        FakeRunner runner,
        FakeLibraryProjection library,
        ICloudMountProbe? mountProbe = null)
        => new(
            new FakeConfigurationSource(config),
            runner,
            library,
            NullLogger<CloudProjectionService>.Instance,
            mountProbe ?? new SimulatedMountProbe());

    private static RcloneCommandResult Result(
        int exitCode,
        string stdout = "",
        string stderr = "")
        => new(
            true,
            exitCode,
            stdout,
            stderr,
            false,
            false);

    private sealed class FakeConfigurationSource : ICloudProjectionConfigurationSource
    {
        private readonly PluginConfiguration _config;

        public FakeConfigurationSource(PluginConfiguration config)
        {
            _config = config;
        }

        public PluginConfiguration GetCurrent() => _config;
    }

    private sealed class FakeLibraryProjection : ICloudLibraryProjection
    {
        public bool IsScanRunning { get; set; }

        public int EnsureCalls { get; private set; }

        public int ScanCalls { get; private set; }

        public string? LastLibraryName { get; private set; }

        public CollectionTypeOptions? LastCollectionType { get; private set; }

        public string? LastProjectionPath { get; private set; }

        public CloudLibraryEnsureResult Inspect(
            string libraryName,
            string projectionPath)
            => EnsureCalls > 0
                ? new CloudLibraryEnsureResult(true, false, "library-ready")
                : new CloudLibraryEnsureResult(false, false, "library-missing");

        public Task<CloudLibraryEnsureResult> EnsureAsync(
            string libraryName,
            CollectionTypeOptions collectionType,
            string projectionPath,
            CancellationToken cancellationToken)
        {
            EnsureCalls++;
            LastLibraryName = libraryName;
            LastCollectionType = collectionType;
            LastProjectionPath = projectionPath;
            return Task.FromResult(
                new CloudLibraryEnsureResult(true, true, "library-created"));
        }

        public CloudLibraryScanPolicy? CaptureScanPolicy(
            string libraryName,
            string projectionPath)
            => new(
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false);

        public bool ApplyCloudSafeScanPolicy(
            string libraryName,
            string projectionPath)
        {
            ScanPolicyCalls++;
            return ScanPolicyAvailable;
        }

        public bool RestoreScanPolicy(
            string libraryName,
            string projectionPath,
            CloudLibraryScanPolicy policy)
            => true;

        public int ScanPolicyCalls { get; private set; }

        public bool ScanPolicyAvailable { get; set; } = true;

        public void QueueScan()
        {
            ScanCalls++;
        }
    }

    private sealed class UnmountSequenceProbe : ICloudMountProbe
    {
        private int _inspections;

        public CloudMountObservation Inspect(string mountPath, string remoteSpec)
            => ++_inspections <= 2
                ? new(true, true, true, "mount-ready")
                : new(false, false, false, "mount-not-active");
    }

    private sealed class TimeoutMountProbe : ICloudMountProbe
    {
        public CloudMountObservation Inspect(string mountPath, string remoteSpec)
            => new(true, true, false, "mount-read-not-checked");

        public Task<CloudMountObservation> InspectAsync(
            string mountPath, string remoteSpec,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new CloudMountObservation(
                true, true, false, "mount-probe-timeout"));
    }

    private sealed class SimulatedMountProbe : ICloudMountProbe
    {
        private readonly bool _neverMounts;
        private bool _mounted;
        private int _observations;

        public SimulatedMountProbe(bool initiallyMounted = false, bool neverMounts = false)
        {
            _mounted = initiallyMounted;
            _neverMounts = neverMounts;
        }

        public CloudMountObservation Inspect(string mountPath, string remoteSpec)
        {
            _observations++;
            if (!_neverMounts && _observations > 1)
            {
                _mounted = true;
            }

            return _mounted
                ? new(true, true, true, "mount-ready")
                : new(false, false, false, "mount-not-active");
        }
    }

    private sealed class FakeRunner : IRcloneProcessRunner
    {
        private readonly Queue<RcloneCommandResult> _results;

        public FakeRunner(params RcloneCommandResult[] results)
        {
            _results = new Queue<RcloneCommandResult>(results);
        }

        public List<Invocation> Invocations { get; } = new();

        public Task<RcloneCommandResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Invocations.Add(new Invocation(
                executable,
                new List<string>(arguments)));

            if (_results.Count == 0)
            {
                throw new InvalidOperationException("Fake runner has no queued result.");
            }

            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed record Invocation(
        string Executable,
        IReadOnlyList<string> Arguments);

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = Directory.CreateTempSubdirectory(
                "audio-gateway-cloud-test").FullName;
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
