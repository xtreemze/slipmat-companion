using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Configuration;
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
    public async Task RcloneHost_ExpiredOAuth_ClassifiesAuthenticationRequired()
    {
        var runner = new FakeRunner(Result(1, stderr: ExpiredOAuthError));
        var host = new RcloneCliHost(runner: runner);

        var listing = await host.ListAsync("tele2", string.Empty);

        Assert.False(listing.Success);
        Assert.Equal("remote-authentication-required", listing.ErrorCode);
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
        Assert.False(result.CopyCompleted);
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
    public async Task Reconcile_EmptyOwnedRoot_UsesOneWayRcloneCopy()
    {
        using var temp = new TempDirectory();
        var projectionPath = Path.Combine(temp.Path, "projection");
        var config = EnabledConfig(projectionPath);
        var runner = HealthyRunner(Result(0));
        var library = new FakeLibraryProjection();
        var service = CreateService(config, runner, library);

        var result = await service.ReconcileAsync();

        Assert.Equal("idle", result.Status);
        Assert.Equal("projection-empty", result.Code);
        Assert.True(result.CopyCompleted);
        Assert.True(File.Exists(Path.Combine(
            projectionPath,
            ".slipmat-rclone-cloud-projection.json")));

        Assert.Equal(
            new[]
            {
                "copy",
                "tele2:Media/Music",
                projectionPath,
                "--create-empty-src-dirs",
                "--stats=0",
                "--log-level=ERROR",
            },
            runner.Invocations[^1].Arguments);

        Assert.DoesNotContain(
            runner.Invocations.SelectMany(invocation => invocation.Arguments),
            argument => argument is "sync" or "move" or "delete" or "purge" or "rc" or "rcd");
    }

    [Fact]
    public async Task Reconcile_CompletedProjection_CreatesLibraryAndQueuesScan()
    {
        using var temp = new TempDirectory();
        var projectionPath = Path.Combine(temp.Path, "projection");
        var config = EnabledConfig(projectionPath);
        var library = new FakeLibraryProjection();

        var first = CreateService(
            config,
            HealthyRunner(Result(0)),
            library);
        await first.ReconcileAsync();
        File.WriteAllText(Path.Combine(projectionPath, "track.flac"), "fixture");

        var second = CreateService(
            config,
            HealthyRunner(Result(0)),
            library);
        var result = await second.ReconcileAsync();

        Assert.Equal("scan-queued", result.Status);
        Assert.True(result.CopyCompleted);
        Assert.True(result.LibraryReady);
        Assert.True(result.LibraryScanQueued);
        Assert.Equal(1, library.EnsureCalls);
        Assert.Equal(1, library.ScanCalls);
        Assert.Equal("Cloud Music", library.LastLibraryName);
        Assert.Equal(CollectionTypeOptions.music, library.LastCollectionType);
    }

    [Fact]
    public async Task Reconcile_NonEmptyUnmanagedDirectory_FailsClosedBeforeCopy()
    {
        using var temp = new TempDirectory();
        var projectionPath = Path.Combine(temp.Path, "projection");
        Directory.CreateDirectory(projectionPath);
        File.WriteAllText(Path.Combine(projectionPath, "foreign.txt"), "keep");

        var runner = HealthyRunner();
        var service = CreateService(
            EnabledConfig(projectionPath),
            runner,
            new FakeLibraryProjection());

        var result = await service.ReconcileAsync();

        Assert.Equal("blocked", result.Status);
        Assert.Equal("projection-path-not-owned", result.Code);
        Assert.Equal(2, runner.Invocations.Count);
    }

    [Fact]
    public async Task Reconcile_RemoteRootChange_RejectsExistingMarker()
    {
        using var temp = new TempDirectory();
        var projectionPath = Path.Combine(temp.Path, "projection");
        var config = EnabledConfig(projectionPath);

        await CreateService(
            config,
            HealthyRunner(Result(0)),
            new FakeLibraryProjection())
            .ReconcileAsync();

        var changed = EnabledConfig(projectionPath);
        changed.CloudRemotePath = "Media/Other";

        var runner = HealthyRunner();
        var result = await CreateService(
            changed,
            runner,
            new FakeLibraryProjection())
            .ReconcileAsync();

        Assert.Equal("blocked", result.Status);
        Assert.Equal("projection-marker-mismatch", result.Code);
        Assert.Equal(2, runner.Invocations.Count);
    }

    [Fact]
    public async Task Reconcile_SelectedRemoteMissing_FailsBeforeCopy()
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
        FakeLibraryProjection library)
        => new(
            new FakeConfigurationSource(config),
            runner,
            library,
            NullLogger<CloudProjectionService>.Instance);

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

        public void QueueScan()
        {
            ScanCalls++;
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
