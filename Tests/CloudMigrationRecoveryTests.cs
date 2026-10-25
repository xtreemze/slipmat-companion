using System;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Jellyfin.Plugin.AudioGateway.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public class CloudMigrationRecoveryTests
{
    [Fact]
    public async Task Recovery_RetainsJobIdentityAndRequiresOriginalSourceInventory()
    {
        var root = Directory.CreateTempSubdirectory("migration-recovery-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "track.flac"), "audio");
            var manifest = await CloudMigrationManifestBuilder.CaptureAsync(root);
            var job = Job(root, manifest);
            Assert.True(CloudMigrationRecovery.CanResume(job));
            Assert.True(CloudMigrationRecovery.ManifestMatches(job, manifest));
            await File.AppendAllTextAsync(Path.Combine(root, "track.flac"), "changed");
            Assert.False(CloudMigrationRecovery.ManifestMatches(job,
                await CloudMigrationManifestBuilder.CaptureAsync(root)));
            Assert.False(CloudMigrationRecovery.CanResume(job with { BackupPath = root + "-backup" }));
            Assert.False(CloudMigrationRecovery.CanResume(job with { ManifestDigestSha256 = null }));
            Assert.False(CloudMigrationRecovery.CanResume(job with { Code = "cutover-mount-failed" }));
            Assert.False(CloudMigrationRecovery.CanResume(job with { Phase = CloudMigrationPhases.Copying }));
            Assert.False(CloudMigrationRecovery.CanResume(job with { Direction = CloudMigrationDirections.VfsToLocal }));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(5, "rclone-temporary-error")]
    [InlineData(7, "rclone-fatal-error")]
    [InlineData(8, "rclone-transfer-limit")]
    [InlineData(10, "rclone-duration-limit")]
    public void Diagnostics_ClassifiesExitWithoutReturningProviderSecrets(int exit, string code)
    {
        var result = new RcloneCommandResult(true, exit, "", "token=secret https://private.invalid", false, false);
        Assert.Equal(code, CloudMigrationRecovery.FailureCode(result, "copy"));
    }

    [Fact]
    public void Diagnostics_PrefersActionableImmutableConflict()
    {
        var result = new RcloneCommandResult(true, 1, "",
            "private.flac: immutable file modified token=secret", false, false);
        Assert.Equal("remote-immutable-conflict", CloudMigrationRecovery.FailureCode(result, "copy"));
    }

    [Fact]
    public async Task Resume_ReusesFailedJobAndDoesNotBrowseOrReplacePartialDestination()
    {
        using var fixture = await Fixture.CreateAsync();
        var resumed = await fixture.Coordinator.RequestResumeAsync(fixture.Job.Id);
        Assert.Equal(fixture.Job.Id, resumed.Id);
        Assert.Equal(fixture.Job.ManifestDigestSha256, resumed.ManifestDigestSha256);
        Assert.Equal(CloudMigrationPhases.QueuedCopy, resumed.Phase);
        Assert.Equal(resumed.Id, await fixture.Queue.Reader.ReadAsync());
        Assert.Single(await fixture.Store.LoadAllAsync());
        Assert.True(File.Exists(Path.Combine(fixture.Root, "track.flac")));
        var duplicate = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Coordinator.RequestResumeAsync(fixture.Job.Id));
        Assert.Equal("migration-not-resumable", duplicate.Message);
    }

    [Theory]
    [InlineData("source", "migration-source-changed")]
    [InlineData("remote", "migration-profile-changed")]
    [InlineData("enabled", "migration-profile-changed")]
    [InlineData("library", "migration-library-changed")]
    public async Task Resume_RejectsChangedCustodyWithoutQueuing(string change, string code)
    {
        using var fixture = await Fixture.CreateAsync();
        if (change == "source") await File.AppendAllTextAsync(Path.Combine(fixture.Root, "track.flac"), "changed");
        if (change == "remote") fixture.Config.CloudLibraries[0].RemotePath = "unrelated";
        if (change == "enabled") fixture.Config.CloudLibraries[0].Enabled = true;
        if (change == "library") fixture.Library.Folder.Locations = [fixture.Root + "-other"];
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Coordinator.RequestResumeAsync(fixture.Job.Id));
        Assert.Equal(code, error.Message);
        Assert.False(fixture.Queue.Reader.TryRead(out _));
        Assert.Equal(CloudMigrationPhases.Failed, (await fixture.Store.GetAsync(fixture.Job.Id))!.Phase);
    }

    [Fact]
    public async Task Worker_ResumesCopyThenRequiresDownloadVerificationAndPersistsOnlyNumericProgress()
    {
        using var fixture = await Fixture.CreateAsync();
        var runner = new ProgressRunner();
        await fixture.Coordinator.RequestResumeAsync(fixture.Job.Id);
        using var worker = new CloudMigrationWorker(fixture.Store, fixture.Queue, runner,
            fixture.Source, null!, null!, fixture.LibraryManager, NullLogger<CloudMigrationWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        CloudMigrationJob? job;
        do
        {
            await Task.Delay(10, timeout.Token);
            job = await fixture.Store.GetAsync(fixture.Job.Id, timeout.Token);
        } while (job!.Phase is CloudMigrationPhases.QueuedCopy or CloudMigrationPhases.Copying or CloudMigrationPhases.Verifying);
        await worker.StopAsync(CancellationToken.None);
        Assert.Equal(CloudMigrationPhases.ReadyForCutover, job.Phase);
        Assert.Equal("rclone-check-download-content", job.Verification);
        Assert.Equal(2, runner.Commands.Count);
        Assert.Contains("--immutable", runner.Commands[0]);
        Assert.Contains("--download", runner.Commands[1]);
        Assert.Contains("--use-json-log", runner.Commands[0]);
        Assert.Equal(5, job.Progress!.Bytes);
        Assert.Equal(0, job.LastExitCode);
    }

    [Theory]
    [InlineData("{\"stats\":{\"bytes\":5,\"totalBytes\":10,\"checks\":1,\"msg\":\"token=private\"}}", true)]
    [InlineData("{\"stats\":{\"bytes\":-1,\"totalBytes\":10}}", false)]
    [InlineData("{\"stats\":{\"bytes\":\"token\",\"totalBytes\":10}}", false)]
    [InlineData("{\"msg\":\"token=private\"}", false)]
    public void Progress_ParsesOnlyAllowlistedNonnegativeNumbers(string json, bool valid)
    {
        Assert.Equal(valid, RcloneMigrationProgress.TryParse(json, out var progress));
        if (valid) Assert.DoesNotContain("token", System.Text.Json.JsonSerializer.Serialize(progress));
    }

    [Fact]
    public async Task ProcessRunner_StreamsNumericStatsFromStderrWithoutReturningLogTextAsProgress()
    {
        if (OperatingSystem.IsWindows()) return;
        var samples = new List<CloudMigrationProgress>();
        var result = await new RcloneProcessRunner().RunWithProgressAsync("/bin/sh",
            ["-c", "printf '%s\\n' '{\"stats\":{\"bytes\":5,\"totalBytes\":10,\"checks\":1},\"msg\":\"token=private\"}' >&2"],
            TimeSpan.FromSeconds(5), sample => { samples.Add(sample); return Task.CompletedTask; },
            CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(5, Assert.Single(samples).Bytes);
        Assert.DoesNotContain("private", System.Text.Json.JsonSerializer.Serialize(samples));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resume_RejectsOtherJobsAndWorkerStillFinishing(bool pending)
    {
        using var fixture = await Fixture.CreateAsync();
        if (pending) fixture.Queue.TryEnqueue(fixture.Job.Id);
        else await fixture.Store.SaveAsync(fixture.Job with { Id = Guid.NewGuid().ToString("N") });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Coordinator.RequestResumeAsync(fixture.Job.Id));
        Assert.Equal("migration-recovery-conflict", error.Message);
    }

    [Fact]
    public async Task Manifest_RejectsDirectoryLinksBeforeTraversingTheirContents()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.CreateAsync();
        var external = Directory.CreateTempSubdirectory("migration-external-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(external, "private.flac"), "outside");
            Directory.CreateSymbolicLink(Path.Combine(fixture.Root, "linked"), external);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => CloudMigrationManifestBuilder.CaptureAsync(fixture.Root));
            Assert.Equal("source-reparse-point-unsupported", error.Message);
        }
        finally { Directory.Delete(external, true); }
    }

    [Fact]
    public async Task Resume_QueueFullRestoresOriginalFailureJournal()
    {
        using var fixture = await Fixture.CreateAsync();
        for (var i = 0; i < 32; i++) Assert.True(fixture.Queue.TryEnqueue(Guid.NewGuid().ToString("N")));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Coordinator.RequestResumeAsync(fixture.Job.Id));
        Assert.Equal("migration-queue-full", error.Message);
        Assert.Equal(fixture.Job, await fixture.Store.GetAsync(fixture.Job.Id));
    }

    [Fact]
    public async Task Worker_FailedCopyRetainsExitAndObservationForTheSameResumableJob()
    {
        using var fixture = await Fixture.CreateAsync();
        var runner = new ProgressRunner { CopyExitCode = 5 };
        await fixture.Coordinator.RequestResumeAsync(fixture.Job.Id);
        using var worker = new CloudMigrationWorker(fixture.Store, fixture.Queue, runner,
            fixture.Source, null!, null!, fixture.LibraryManager, NullLogger<CloudMigrationWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        CloudMigrationJob? job;
        do
        {
            await Task.Delay(10, timeout.Token);
            job = await fixture.Store.GetAsync(fixture.Job.Id, timeout.Token);
        } while (job!.Phase is CloudMigrationPhases.QueuedCopy or CloudMigrationPhases.Copying);
        await worker.StopAsync(CancellationToken.None);
        Assert.Equal(CloudMigrationPhases.Failed, job.Phase);
        Assert.Equal("rclone-temporary-error", job.Code);
        Assert.Equal(5, job.LastExitCode);
        Assert.Equal(5, job.Progress!.Bytes);
        Assert.True(CloudMigrationRecovery.CanResume(job));
        Assert.Single(runner.Commands);
        Assert.Null(job.Verification);
    }

    public class LibraryProxy : DispatchProxy
    {
        public VirtualFolderInfo Folder { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
            => method!.Name == "GetVirtualFolders" ? new List<VirtualFolderInfo> { Folder }
                : throw new InvalidOperationException(method.Name);
    }

    private sealed class ConfigSource(PluginConfiguration config) : ICloudProjectionConfigurationStore
    {
        public PluginConfiguration GetCurrent() => config;
        public void Save(PluginConfiguration value) => throw new InvalidOperationException("No config mutation during prepare");
    }

    private sealed class ProgressRunner : IRcloneProgressRunner
    {
        public List<IReadOnlyList<string>> Commands { get; } = [];
        public int CopyExitCode { get; init; }
        public Task<RcloneCommandResult> RunAsync(string executable, IReadOnlyList<string> args,
            TimeSpan timeout, CancellationToken token) => throw new InvalidOperationException("Progress path required");
        public async Task<RcloneCommandResult> RunWithProgressAsync(string executable,
            IReadOnlyList<string> args, TimeSpan timeout, Func<CloudMigrationProgress, Task> progress,
            CancellationToken token)
        {
            Commands.Add(args);
            await progress(new(5, 5, 1, 1, 0, DateTimeOffset.UtcNow));
            return new(true, args[0] == "copy" ? CopyExitCode : 0, "", "", false, false);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("migration-resume-").FullName;
        public PluginConfiguration Config { get; }
        public ConfigSource Source { get; }
        public CloudMigrationStore Store { get; }
        public CloudMigrationQueue Queue { get; } = new();
        public ILibraryManager LibraryManager { get; }
        public LibraryProxy Library => (LibraryProxy)(object)LibraryManager;
        public CloudMigrationCoordinator Coordinator { get; }
        public CloudMigrationJob Job { get; private set; } = null!;
        private Fixture()
        {
            Config = new()
            {
                StoreRoot = Root + "-store",
                CloudLibraries = [new()
                { Id = "music", LibraryName = "Music", RemoteName = "tele2", RemotePath = "Slipmat" }]
            };
            Source = new(Config);
            Store = new(Source);
            LibraryManager = DispatchProxy.Create<ILibraryManager, LibraryProxy>();
            Library.Folder = new() { Name = "Music", Locations = [Root] };
            Coordinator = new(Store, Queue, Source, LibraryManager, null!, null!);
        }
        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            await File.WriteAllTextAsync(Path.Combine(fixture.Root, "track.flac"), "audio");
            fixture.Job = CloudMigrationRecoveryTests.Job(fixture.Root,
                await CloudMigrationManifestBuilder.CaptureAsync(fixture.Root));
            await fixture.Store.SaveAsync(fixture.Job);
            return fixture;
        }
        public void Dispose()
        {
            Directory.Delete(Root, true);
            if (Directory.Exists(Config.StoreRoot)) Directory.Delete(Config.StoreRoot, true);
        }
    }

    internal static CloudMigrationJob Job(string root, CloudMigrationManifest manifest)
        => new(Guid.NewGuid().ToString("N"), "music", "Music",
            CloudMigrationDirections.LocalToVfs, CloudMigrationPhases.Failed, "copy-failed",
            root, null, null, "tele2", "Slipmat", "", false, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            ManifestVersion: manifest.Version, ManifestDigestSha256: manifest.DigestSha256,
            ManifestFileCount: manifest.FileCount, ManifestTotalBytes: manifest.TotalBytes);
}
