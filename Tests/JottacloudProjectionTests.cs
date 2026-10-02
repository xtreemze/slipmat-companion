using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Configuration;
using Jellyfin.Plugin.AudioGateway.Services;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public class JottacloudProjectionTests
{
    [Fact]
    public async Task CliHost_StatusAndListing_UseBoundedArgumentVectors()
    {
        var runner = new FakeRunner(
            Result(0, "jotta-cli version : 0.17.176206"),
            Result(0, "{\"User\":{\"Email\":\"hidden@example.invalid\"}}"),
            Result(0, "Account : hidden@example.invalid"),
            Result(0, "Album A\nAlbum B"));

        var host = new JottacloudCliHost(runner: runner);

        var status = await host.GetStatusAsync();
        var listing = await host.ListAsync("Archive/Music; touch /tmp/not-a-command", includeDetails: true);

        Assert.Equal(JottacloudCliHealth.Ready, status.Health);
        Assert.Equal("0.17.176206", status.CliVersion);
        Assert.True(listing.Success);
        Assert.Equal(new[] { "Album A", "Album B" }, listing.Lines);

        Assert.Equal(new[] { "version" }, runner.Invocations[0].Arguments);
        Assert.Equal(new[] { "status", "--json" }, runner.Invocations[1].Arguments);
        Assert.Equal(new[] { "status" }, runner.Invocations[2].Arguments);
        Assert.NotNull(status.AccountFingerprint);
        Assert.DoesNotContain("hidden@example.invalid", status.AccountFingerprint!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            new[] { "ls", "-a", "Archive/Music; touch /tmp/not-a-command" },
            runner.Invocations[3].Arguments);
    }

    [Theory]
    [InlineData("Archive/Music\nOther")]
    [InlineData("Archive/Music\rOther")]
    [InlineData("Archive/Music\0Other")]
    [InlineData("--host")]
    public async Task CliHost_List_RejectsOptionAndControlInjection(string path)
    {
        var host = new JottacloudCliHost(runner: new FakeRunner());

        await Assert.ThrowsAsync<ArgumentException>(() => host.ListAsync(path));
    }

    [Theory]
    [InlineData("[]", 0)]
    [InlineData("[{\"id\":\"one\"}]", 1)]
    [InlineData("{\"downloads\":[1,2]}", 2)]
    [InlineData("{\"Downloads\":[]}", 0)]
    public void CliHost_DownloadQueueParser_AcceptsKnownShapes(string json, int expected)
    {
        Assert.True(JottacloudCliHost.TryParseDownloadQueueEntryCount(json, out var count));
        Assert.Equal(expected, count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("\"text\"")]
    [InlineData("{\"downloads\":{}}")]
    public void CliHost_DownloadQueueParser_FailsClosedOnUnknownShape(string json)
    {
        Assert.False(JottacloudCliHost.TryParseDownloadQueueEntryCount(json, out _));
    }

    [Fact]
    public async Task Reconcile_EmptyOwnedRoot_QueuesFirstMaterializationWithoutLibraryScan()
    {
        using var temp = new TempDirectory();
        var projectionPath = Path.Combine(temp.Path, "projection");
        var config = EnabledConfig(projectionPath);

        var runner = HealthyIdleDownloadRunner();
        var library = new FakeLibraryProjection();
        var service = CreateService(config, runner, library);

        var result = await service.ReconcileAsync();

        Assert.Equal("queued", result.Status);
        Assert.Equal("download-queued", result.Code);
        Assert.True(result.DownloadQueued);
        Assert.False(result.LibraryReady);
        Assert.False(result.LibraryScanQueued);
        Assert.Equal(0, library.EnsureCalls);
        Assert.Equal(0, library.ScanCalls);
        Assert.True(File.Exists(Path.Combine(
            projectionPath,
            ".slipmat-jottacloud-projection.json")));

        Assert.Equal(
            new[]
            {
                "download",
                "Archive/Music",
                projectionPath,
                "--merge",
                "--mergemode=metadata",
            },
            runner.Invocations[^1].Arguments);
    }

    [Fact]
    public async Task Reconcile_CompletedProjection_ScansBeforeAnyLaterRefresh()
    {
        using var temp = new TempDirectory();
        var projectionPath = Path.Combine(temp.Path, "projection");
        var config = EnabledConfig(projectionPath);

        var firstRunner = HealthyIdleDownloadRunner();
        var library = new FakeLibraryProjection();
        var service = CreateService(config, firstRunner, library);
        await service.ReconcileAsync();

        File.WriteAllText(Path.Combine(projectionPath, "track.flac"), "fixture");

        var secondRunner = HealthyIdleDownloadRunner();
        service = CreateService(config, secondRunner, library);

        var result = await service.ReconcileAsync();

        Assert.Equal("scan-queued", result.Status);
        Assert.Equal("projection-scan-queued", result.Code);
        Assert.False(result.DownloadQueued);
        Assert.True(result.LibraryReady);
        Assert.True(result.LibraryScanQueued);
        Assert.Equal(1, library.EnsureCalls);
        Assert.Equal(1, library.ScanCalls);
        Assert.Equal("Jottacloud Music", library.LastLibraryName);
        Assert.Equal(CollectionTypeOptions.music, library.LastCollectionType);
        Assert.Equal(Path.GetFullPath(projectionPath), library.LastProjectionPath);
        Assert.Equal(4, secondRunner.Invocations.Count);

        var thirdRunner = HealthyIdleDownloadRunner();
        service = CreateService(config, thirdRunner, library);
        result = await service.ReconcileAsync();

        Assert.Equal("queued", result.Status);
        Assert.True(result.DownloadQueued);
        Assert.Equal(1, library.ScanCalls);
        Assert.Equal("download", thirdRunner.Invocations[^1].Arguments[0]);
    }

    [Fact]
    public async Task Reconcile_NonEmptyUnmanagedDirectory_FailsClosedBeforeDownload()
    {
        using var temp = new TempDirectory();
        var projectionPath = Path.Combine(temp.Path, "projection");
        Directory.CreateDirectory(projectionPath);
        File.WriteAllText(Path.Combine(projectionPath, "foreign.txt"), "do not touch");

        var config = EnabledConfig(projectionPath);
        var runner = new FakeRunner(
            Result(0, "jotta-cli version : 0.17.176206"),
            Result(0, "{}"),
            Result(0, "Account : media@example.invalid"),
            Result(0, "[]"));
        var library = new FakeLibraryProjection();
        var service = CreateService(config, runner, library);

        var result = await service.ReconcileAsync();

        Assert.Equal("blocked", result.Status);
        Assert.Equal("projection-path-not-owned", result.Code);
        Assert.Equal(4, runner.Invocations.Count);
        Assert.Equal(0, library.EnsureCalls);
        Assert.Equal(0, library.ScanCalls);
    }

    [Fact]
    public async Task Reconcile_OutstandingDownloadEntry_DoesNotScanOrQueueAnotherTransfer()
    {
        using var temp = new TempDirectory();
        var projectionPath = Path.Combine(temp.Path, "projection");

        var config = EnabledConfig(projectionPath);
        var runner = new FakeRunner(
            Result(0, "jotta-cli version : 0.17.176206"),
            Result(0, "{}"),
            Result(0, "Account : media@example.invalid"),
            Result(0, "[{\"id\":\"active\"}]"));
        var library = new FakeLibraryProjection();
        var service = CreateService(config, runner, library);

        var result = await service.ReconcileAsync();

        Assert.Equal("in-progress", result.Status);
        Assert.Equal("download-queue-not-clear", result.Code);
        Assert.False(Directory.Exists(projectionPath));
        Assert.Equal(0, library.EnsureCalls);
        Assert.Equal(0, library.ScanCalls);
        Assert.Equal(4, runner.Invocations.Count);
    }

    [Fact]
    public async Task Reconcile_AuthenticationRequired_DoesNotTouchProjection()
    {
        using var temp = new TempDirectory();
        var projectionPath = Path.Combine(temp.Path, "projection");

        var config = EnabledConfig(projectionPath);
        var runner = new FakeRunner(
            Result(0, "jotta-cli version : 0.17.176206"),
            Result(1, stderr: "ERROR Not logged in"));
        var library = new FakeLibraryProjection();
        var service = CreateService(config, runner, library);

        var result = await service.ReconcileAsync();

        Assert.Equal("blocked", result.Status);
        Assert.Equal("authentication-required", result.Code);
        Assert.False(Directory.Exists(projectionPath));
        Assert.Equal(0, library.EnsureCalls);
        Assert.Equal(0, library.ScanCalls);
    }

    [Fact]
    public async Task Reconcile_RemoteRootChange_RejectsExistingMarker()
    {
        using var temp = new TempDirectory();
        var projectionPath = Path.Combine(temp.Path, "projection");

        var config = EnabledConfig(projectionPath);
        var service = CreateService(
            config,
            HealthyIdleDownloadRunner(),
            new FakeLibraryProjection());
        await service.ReconcileAsync();

        var changed = EnabledConfig(projectionPath);
        changed.JottacloudRemotePath = "Archive/Other";

        var runner = new FakeRunner(
            Result(0, "jotta-cli version : 0.17.176206"),
            Result(0, "{}"),
            Result(0, "Account : media@example.invalid"),
            Result(0, "[]"));
        service = CreateService(changed, runner, new FakeLibraryProjection());

        var result = await service.ReconcileAsync();

        Assert.Equal("blocked", result.Status);
        Assert.Equal("projection-marker-mismatch", result.Code);
        Assert.Equal(4, runner.Invocations.Count);
    }

    [Fact]
    public async Task Reconcile_ReauthenticatedDifferentAccount_RejectsExistingProjectionMarker()
    {
        using var temp = new TempDirectory();
        var projectionPath = Path.Combine(temp.Path, "projection");
        var config = EnabledConfig(projectionPath);

        var service = CreateService(
            config,
            HealthyIdleDownloadRunner("first@example.invalid"),
            new FakeLibraryProjection());
        await service.ReconcileAsync();

        var runner = new FakeRunner(
            Result(0, "jotta-cli version : 0.17.176206"),
            Result(0, "{}"),
            Result(0, "Account : second@example.invalid"),
            Result(0, "[]"));
        service = CreateService(config, runner, new FakeLibraryProjection());

        var result = await service.ReconcileAsync();

        Assert.Equal("blocked", result.Status);
        Assert.Equal("projection-marker-mismatch", result.Code);
        Assert.Equal(4, runner.Invocations.Count);
    }

    [Fact]
    public async Task CliHost_UnsupportedVersion_FailsClosedBeforeReadingAccountIdentity()
    {
        var runner = new FakeRunner(
            Result(0, "jotta-cli version : 0.18.000001"),
            Result(0, "{}"));
        var host = new JottacloudCliHost(runner: runner);

        var status = await host.GetStatusAsync();

        Assert.Equal(JottacloudCliHealth.Degraded, status.Health);
        Assert.Equal("unsupported-cli-version", status.Code);
        Assert.Null(status.AccountFingerprint);
        Assert.Equal(2, runner.Invocations.Count);
    }

    private static PluginConfiguration EnabledConfig(string projectionPath)
        => new()
        {
            JottacloudProjectionEnabled = true,
            JottacloudRemotePath = "Archive/Music",
            JottacloudProjectionPath = projectionPath,
            JottacloudLibraryName = "Jottacloud Music",
            JottacloudCollectionType = "music",
            JottacloudAutoCreateLibrary = true,
        };

    private static FakeRunner HealthyIdleDownloadRunner(
        string account = "media@example.invalid")
        => new(
            Result(0, "jotta-cli version : 0.17.176206"),
            Result(0, "{}"),
            Result(0, $"Account : {account}"),
            Result(0, "[]"),
            Result(0, "download queued"));

    private static JottacloudProjectionService CreateService(
        PluginConfiguration config,
        FakeRunner runner,
        FakeLibraryProjection library)
        => new(
            new FakeConfigurationSource(config),
            runner,
            library,
            NullLogger<JottacloudProjectionService>.Instance);

    private static JottacloudCliCommandResult Result(
        int exitCode,
        string stdout = "",
        string stderr = "")
        => new(
            StartSucceeded: true,
            ExitCode: exitCode,
            StandardOutput: stdout,
            StandardError: stderr,
            TimedOut: false,
            OutputTruncated: false);

    private sealed class FakeConfigurationSource : IJottacloudProjectionConfigurationSource
    {
        private readonly PluginConfiguration _config;

        public FakeConfigurationSource(PluginConfiguration config)
        {
            _config = config;
        }

        public PluginConfiguration GetCurrent() => _config;
    }

    private sealed class FakeLibraryProjection : IJottacloudLibraryProjection
    {
        public bool IsScanRunning { get; set; }

        public int EnsureCalls { get; private set; }

        public int ScanCalls { get; private set; }

        public string? LastLibraryName { get; private set; }

        public CollectionTypeOptions? LastCollectionType { get; private set; }

        public string? LastProjectionPath { get; private set; }

        public JottacloudLibraryEnsureResult Inspect(
            string libraryName,
            string projectionPath)
            => EnsureCalls > 0
                ? new JottacloudLibraryEnsureResult(true, false, "library-ready")
                : new JottacloudLibraryEnsureResult(false, false, "library-missing");

        public Task<JottacloudLibraryEnsureResult> EnsureAsync(
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
                new JottacloudLibraryEnsureResult(true, true, "library-created"));
        }

        public void QueueScan()
        {
            ScanCalls++;
        }
    }

    private sealed class FakeRunner : IJottacloudCliProcessRunner
    {
        private readonly Queue<JottacloudCliCommandResult> _results;

        public FakeRunner(params JottacloudCliCommandResult[] results)
        {
            _results = new Queue<JottacloudCliCommandResult>(results);
        }

        public List<Invocation> Invocations { get; } = new();

        public Task<JottacloudCliCommandResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Invocations.Add(
                new Invocation(
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
                "audio-gateway-jottacloud-test").FullName;
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
