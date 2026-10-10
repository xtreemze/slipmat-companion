using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Configuration;
using Jellyfin.Plugin.AudioGateway.Services;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public sealed class CloudPreflightTests
{
    private const string RemotesJson =
        "[{\"name\":\"tele2:\",\"type\":\"jottacloud\",\"description\":\"\",\"source\":\"file\"}]";

    [Fact]
    public async Task RcloneHost_ReportsVersionUnsupported_WhenListremotesJsonFlagIsUnknown()
    {
        var runner = new QueueRunner(
            new RcloneCommandResult(true, 0, "rclone v1.60.1-DEV\n", "", false, false),
            new RcloneCommandResult(true, 1, "", "Error: unknown flag: --json", false, false));

        var status = await new RcloneCliHost(runner: runner).GetStatusAsync();

        Assert.Equal(RcloneCliHealth.Degraded, status.Health);
        Assert.Equal("1.60.1-DEV", status.Version);
        Assert.Equal("rclone-version-unsupported", status.Code);
    }

    [Fact]
    public async Task Preflight_FailsWithActionableFix_WhenRcloneIsTooOld()
    {
        var runner = new QueueRunner(
            new RcloneCommandResult(true, 0, "rclone v1.60.1-DEV\n", "", false, false),
            new RcloneCommandResult(true, 1, "", "Error: unknown flag: --json", false, false));

        var result = await Service(runner, new FakeProbe()).RunAsync(CancellationToken.None);

        Assert.Equal("fail", result.Status);
        var check = result.Checks.Single(c => c.Id == "rclone");
        Assert.Equal("fail", check.Status);
        Assert.Contains("listremotes --json", check.Fix);
    }

    [Fact]
    public async Task Preflight_ChecksCacheSpaceAgainstCapPlusFloor()
    {
        var profile = new CloudLibraryProfile
        {
            Id = "p",
            Enabled = true,
            RemoteName = "tele2",
            RemotePath = "Music",
            CacheMaxSizeGiB = 16,
            CacheMinFreeSpaceGiB = 4,
        };

        var tight = await Service(Ready(), new FakeProbe { Free = 10L * 1024 * 1024 * 1024 }, profile)
            .RunAsync(CancellationToken.None);
        var roomy = await Service(Ready(), new FakeProbe { Free = 64L * 1024 * 1024 * 1024 }, profile)
            .RunAsync(CancellationToken.None);

        Assert.Equal("fail", tight.Checks.Single(c => c.Id == "cache:p").Status);
        Assert.Equal("pass", roomy.Checks.Single(c => c.Id == "cache:p").Status);
        Assert.Equal("pass", roomy.Checks.Single(c => c.Id == "rclone").Status);
        Assert.Equal("pass", roomy.Checks.Single(c => c.Id == "remote").Status);
    }

    [Theory]
    [InlineData("fuse.sshfs", "warn")]
    [InlineData("virtiofs", "warn")]
    [InlineData("ext4", "pass")]
    [InlineData(null, "pass")]
    public async Task Preflight_WarnsWhenCacheIsOnNetworkLikeFilesystem(string? fsType, string expected)
    {
        var profile = new CloudLibraryProfile
        {
            Id = "p",
            Enabled = true,
            RemoteName = "tele2",
            RemotePath = "Music",
        };

        var result = await Service(Ready(), new FakeProbe { FsType = fsType }, profile)
            .RunAsync(CancellationToken.None);

        var check = result.Checks.Single(c => c.Id == "cache:p");
        Assert.Equal(expected, check.Status);
        if (expected == "warn")
        {
            Assert.Contains(fsType!, check.Detail);
            Assert.NotNull(check.Fix);
        }
    }

    [Fact]
    public async Task Preflight_OnLinux_FailsWithoutFuseDevice()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await Service(Ready(), new FakeProbe { FuseDevice = false })
            .RunAsync(CancellationToken.None);

        Assert.Equal("fail", result.Checks.Single(c => c.Id == "mount").Status);
    }

    private static QueueRunner Ready() => new(
        new RcloneCommandResult(true, 0, "rclone v1.75.2\n", "", false, false),
        new RcloneCommandResult(true, 0, RemotesJson, "", false, false));

    private static CloudPreflightService Service(
        IRcloneProcessRunner runner,
        ICloudHostProbe probe,
        params CloudLibraryProfile[] profiles)
        => new(new Source(new PluginConfiguration { CloudLibraries = profiles }), runner, probe);

    private sealed class Source(PluginConfiguration configuration) : ICloudProjectionConfigurationSource
    {
        public PluginConfiguration GetCurrent() => configuration;
    }

    private sealed class FakeProbe : ICloudHostProbe
    {
        public bool FuseDevice { get; init; } = true;

        public long? Free { get; init; } = 1024L * 1024 * 1024 * 1024;

        public bool FileExists(string path) => path != "/dev/fuse" || FuseDevice;

        public bool ExistsOnPath(string executable) => true;

        public long? AvailableFreeBytes(string path) => Free;

        public string? FsType { get; init; }

        public string? FileSystemType(string path) => FsType;
    }

    private sealed class QueueRunner(params RcloneCommandResult[] results) : IRcloneProcessRunner
    {
        private readonly Queue<RcloneCommandResult> _results = new(results);

        public Task<RcloneCommandResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
            => Task.FromResult(_results.Dequeue());
    }
}
