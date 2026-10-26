using System.Text.Json;
using System;
using System.IO;
using System.Linq;
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

    [Fact]
    public async Task Rebaseline_QueuesPromptlyWithoutChangingTheOriginalInventory()
    {
        using var fixture = await Fixture.CreateAsync();
        await File.AppendAllTextAsync(Path.Combine(fixture.Root, "track.flac"), "current");
        var queued = await fixture.Coordinator.RequestRebaselineAsync(fixture.Job.Id,
            new CloudMigrationRebaselineRequest(fixture.Job.ManifestDigestSha256!));
        Assert.Equal(CloudMigrationPhases.QueuedRebaseline, queued.Phase);
        Assert.Equal(fixture.Job.ManifestDigestSha256, queued.ManifestDigestSha256);
        Assert.Empty(queued.BaselineHistory ?? []);
        Assert.Equal(fixture.Job.Id, await fixture.Queue.Reader.ReadAsync());
    }

    [Fact]
    public async Task Rebaseline_RejectsStaleOperatorConfirmation()
    {
        using var fixture = await Fixture.CreateAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Coordinator.RequestRebaselineAsync(fixture.Job.Id,
                new CloudMigrationRebaselineRequest(new string('0', 64))));
        Assert.Equal("migration-baseline-confirmation-stale", error.Message);
        Assert.False(fixture.Queue.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData("success")]
    [InlineData("remote-conflict")]
    [InlineData("source-drift")]
    [InlineData("profile-drift")]
    [InlineData("library-drift")]
    [InlineData("enabled-drift")]
    public async Task Rebaseline_ValidatesRemoteSubsetBeforeAdoptingSourceAndNeverWeakensFinalCheck(string mode)
    {
        using var fixture = await Fixture.CreateAsync();
        await File.AppendAllTextAsync(Path.Combine(fixture.Root, "track.flac"), "current");
        var current = await CloudMigrationManifestBuilder.CaptureAsync(fixture.Root);
        var runner = new ProgressRunner
        {
            BeforeCommand = async args =>
            {
                if (args.Contains("--one-way"))
                {
                    var reviewing = await fixture.Store.GetAsync(fixture.Job.Id);
                    Assert.Equal(fixture.Job.ManifestDigestSha256, reviewing!.ManifestDigestSha256);
                    if (mode == "source-drift") await File.AppendAllTextAsync(Path.Combine(fixture.Root, "track.flac"), "late");
                    if (mode == "profile-drift") fixture.Config.CloudLibraries[0].RemotePath = "another";
                    if (mode == "library-drift") fixture.Library.Folder.Locations = [fixture.Root + "-another"];
                    if (mode == "enabled-drift") fixture.Config.CloudLibraries[0].Enabled = true;
                }
            },
            ExitFor = args => mode == "remote-conflict" && args.Contains("--one-way") ? 1 : 0,
        };
        await fixture.Coordinator.RequestRebaselineAsync(fixture.Job.Id,
            new CloudMigrationRebaselineRequest(fixture.Job.ManifestDigestSha256!));
        using var worker = new CloudMigrationWorker(fixture.Store, fixture.Queue, runner,
            fixture.Source, null!, null!, fixture.LibraryManager, NullLogger<CloudMigrationWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        CloudMigrationJob? job;
        do
        {
            await Task.Delay(10, timeout.Token);
            job = await fixture.Store.GetAsync(fixture.Job.Id, timeout.Token);
        } while (job!.Phase != CloudMigrationPhases.Failed && job.Phase != CloudMigrationPhases.ReadyForCutover);
        await worker.StopAsync(CancellationToken.None);
        Assert.Equal("check", runner.Commands[0][0]);
        Assert.Equal("tele2:Slipmat", runner.Commands[0][1]);
        Assert.Equal(fixture.Root, runner.Commands[0][2]);
        Assert.Contains("--one-way", runner.Commands[0]);
        Assert.Contains("--download", runner.Commands[0]);
        if (mode == "success")
        {
            Assert.Equal(CloudMigrationPhases.ReadyForCutover, job.Phase);
            Assert.Equal(current.DigestSha256, job.ManifestDigestSha256);
            var old = Assert.Single(job.BaselineHistory!);
            Assert.Equal(fixture.Job.ManifestDigestSha256, old.DigestSha256);
            Assert.Equal(current.DigestSha256, old.ReplacementDigestSha256);
            Assert.Equal(3, runner.Commands.Count);
            Assert.Equal("copy", runner.Commands[1][0]);
            Assert.Contains("--immutable", runner.Commands[1]);
            Assert.Contains("--checksum", runner.Commands[1]);
            Assert.Equal("check", runner.Commands[2][0]);
            Assert.Contains("--download", runner.Commands[2]);
            Assert.DoesNotContain("--one-way", runner.Commands[2]);
            Assert.Equal("rclone-check-download-content", job.Verification);
        }
        else
        {
            Assert.Equal(CloudMigrationPhases.Failed, job.Phase);
            Assert.Equal(fixture.Job.ManifestDigestSha256, job.ManifestDigestSha256);
            Assert.Empty(job.BaselineHistory ?? []);
            Assert.Single(runner.Commands);
            Assert.Null(job.Verification);
        }
        Assert.DoesNotContain(runner.Commands.SelectMany(args => args),
            arg => arg is "sync" or "move" or "delete" or "purge" or "rc" or "rcd");
    }

    [Fact]
    public async Task Rebaseline_QueueFullRetainsTheEntireOriginalJournal()
    {
        using var fixture = await Fixture.CreateAsync();
        for (var i = 0; i < 32; i++) Assert.True(fixture.Queue.TryEnqueue(Guid.NewGuid().ToString("N")));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Coordinator.RequestRebaselineAsync(fixture.Job.Id,
                new CloudMigrationRebaselineRequest(fixture.Job.ManifestDigestSha256!)));
        Assert.Equal("migration-queue-full", error.Message);
        Assert.Equal(fixture.Job, await fixture.Store.GetAsync(fixture.Job.Id));
    }

    [Fact]
    public async Task Rebaseline_RestartRepeatsReadOnlyVerificationBeforeAdoption()
    {
        using var fixture = await Fixture.CreateAsync();
        await File.AppendAllTextAsync(Path.Combine(fixture.Root, "track.flac"), "current");
        await fixture.Store.SaveAsync(fixture.Job with
        {
            Phase = CloudMigrationPhases.CheckingBaseline,
            Code = "baseline-checking-existing-cloud-content",
        });
        var runner = new ProgressRunner();
        using var worker = new CloudMigrationWorker(fixture.Store, fixture.Queue, runner,
            fixture.Source, null!, null!, fixture.LibraryManager, NullLogger<CloudMigrationWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        CloudMigrationJob? job;
        do
        {
            await Task.Delay(10, timeout.Token);
            job = await fixture.Store.GetAsync(fixture.Job.Id, timeout.Token);
        } while (job!.Phase != CloudMigrationPhases.Failed && job.Phase != CloudMigrationPhases.ReadyForCutover);
        await worker.StopAsync(CancellationToken.None);
        Assert.Equal(CloudMigrationPhases.ReadyForCutover, job.Phase);
        Assert.Contains("--one-way", runner.Commands[0]);
        Assert.Single(job.BaselineHistory!);
    }

    [Fact]
    public async Task Rebaseline_LaterCopyExceptionCannotRestoreTheSupersededManifest()
    {
        using var fixture = await Fixture.CreateAsync();
        await File.AppendAllTextAsync(Path.Combine(fixture.Root, "track.flac"), "current");
        var current = await CloudMigrationManifestBuilder.CaptureAsync(fixture.Root);
        var runner = new ProgressRunner
        {
            BeforeCommand = args => args[0] == "copy"
            ? throw new IOException("simulated copy-start failure") : Task.CompletedTask
        };
        await fixture.Coordinator.RequestRebaselineAsync(fixture.Job.Id,
            new CloudMigrationRebaselineRequest(fixture.Job.ManifestDigestSha256!));
        using var worker = new CloudMigrationWorker(fixture.Store, fixture.Queue, runner,
            fixture.Source, null!, null!, fixture.LibraryManager, NullLogger<CloudMigrationWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        CloudMigrationJob? job;
        do
        {
            await Task.Delay(10, timeout.Token);
            job = await fixture.Store.GetAsync(fixture.Job.Id, timeout.Token);
        } while (job!.Phase != CloudMigrationPhases.Failed);
        await worker.StopAsync(CancellationToken.None);
        Assert.Equal(current.DigestSha256, job.ManifestDigestSha256);
        Assert.Equal(fixture.Job.ManifestDigestSha256, Assert.Single(job.BaselineHistory!).DigestSha256);
        Assert.Null(job.Verification);
    }

    [Fact]
    public async Task Rebaseline_BoundsHistoryAndRetainsFailedJournalAtTheLimit()
    {
        using var fixture = await Fixture.CreateAsync();
        var revision = new CloudMigrationBaselineRevision(1, fixture.Job.ManifestDigestSha256!,
            1, 5, null, DateTimeOffset.UtcNow, new string('1', 64), "test-evidence");
        var job = fixture.Job with
        {
            BaselineHistory = Enumerable.Repeat(revision,
            CloudMigrationRecovery.MaxBaselineRevisions).ToArray()
        };
        await fixture.Store.SaveAsync(job);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Coordinator.RequestRebaselineAsync(job.Id,
                new CloudMigrationRebaselineRequest(job.ManifestDigestSha256!)));
        Assert.Equal("migration-baseline-history-limit", error.Message);
        Assert.Equal(job.ManifestDigestSha256, (await fixture.Store.GetAsync(job.Id))!.ManifestDigestSha256);
        Assert.False(fixture.Queue.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Rebaseline_StillRequiresExactProfileAndNoCompetingJob()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Config.CloudLibraries[0].RemotePath = "unrelated";
        var changed = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Coordinator.RequestRebaselineAsync(fixture.Job.Id,
                new CloudMigrationRebaselineRequest(fixture.Job.ManifestDigestSha256!)));
        Assert.Equal("migration-profile-changed", changed.Message);
        fixture.Config.CloudLibraries[0].RemotePath = "Slipmat";
        fixture.Queue.TryEnqueue(fixture.Job.Id);
        var active = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Coordinator.RequestRebaselineAsync(fixture.Job.Id,
                new CloudMigrationRebaselineRequest(fixture.Job.ManifestDigestSha256!)));
        Assert.Equal("migration-recovery-conflict", active.Message);
    }

    [Fact]
    public async Task Diagnostics_RetainsOriginalFailureAndNeverCopiesOrAdopts()
    {
        using var fixture = await Fixture.CreateAsync();
        var original = fixture.Job with { Code = "baseline-verification-failed", LastExitCode = 1 };
        await fixture.Store.SaveAsync(original);
        var queued = await fixture.Coordinator.RequestDiagnosticsAsync(original.Id);
        Assert.Equal(CloudMigrationPhases.QueuedDiagnostics, queued.Phase);
        var runner = new ProgressRunner { StandardOutput = "* Artist/track.flac\n+ Artist/missing.flac\n! Artist/unreadable.flac\n", CopyExitCode = 0 };
        using var worker = new CloudMigrationWorker(fixture.Store, fixture.Queue, runner,
            fixture.Source, null!, null!, fixture.LibraryManager, NullLogger<CloudMigrationWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        CloudMigrationJob? job;
        do { await Task.Delay(10, timeout.Token); job = await fixture.Store.GetAsync(original.Id); }
        while (job!.Phase != CloudMigrationPhases.Failed);
        await worker.StopAsync(CancellationToken.None);
        Assert.Equal(original.Code, job.Code);
        Assert.Equal(original.LastExitCode, job.LastExitCode);
        Assert.Equal(original.ManifestDigestSha256, job.ManifestDigestSha256);
        Assert.Null(job.BaselineHistory);
        Assert.Null(job.Verification);
        Assert.Equal(3, job.Diagnostics!.Entries.Count);
        var args = Assert.Single(runner.Commands);
        Assert.Equal("check", args[0]);
        Assert.Contains("--one-way", args);
        Assert.Contains("--combined", args);
        Assert.Contains("--max-duration=2m", args);
        Assert.DoesNotContain("--download", args);
        Assert.DoesNotContain("--size-only", args);
    }

    [Fact]
    public void Diagnostics_ReportIsBoundedAndDoesNotPersistMessagesOrUnsafePaths()
    {
        var output = "= match.flac\n* Artist/track.flac\n+ remote-only.flac\n! unreadable.flac\n* ../escape\n* /absolute\n* https://private?token=secret\nRAW token=secret\n";
        var report = CloudMigrationDiagnosticReport.Parse(new(true, 1, output,
            "private provider message token=secret", false, false));
        Assert.Equal(3, report.Entries.Count);
        Assert.Equal("different", report.Entries[0].Kind);
        Assert.Equal("missing-local", report.Entries[1].Kind);
        Assert.Equal("read-error", report.Entries[2].Kind);
        var json = JsonSerializer.Serialize(report);
        Assert.DoesNotContain("secret", json);
        Assert.DoesNotContain("escape", json);
        var bounded = CloudMigrationDiagnosticReport.Parse(new(true, 1,
            string.Concat(Enumerable.Repeat("* track.flac\n", 150)), "", false, true));
        Assert.Equal(100, bounded.Entries.Count);
        Assert.True(bounded.Truncated);
    }

    [Fact]
    public async Task Diagnostics_QueueFullRestoresOriginalJobAndRejectsChangedBindings()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Config.CloudLibraries[0].Enabled = true;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Coordinator.RequestDiagnosticsAsync(fixture.Job.Id));
        Assert.Equal("migration-profile-changed", error.Message);
        fixture.Config.CloudLibraries[0].Enabled = false;
        for (var i = 0; i < 32; i++) Assert.True(fixture.Queue.TryEnqueue("unrelated-" + i));
        error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Coordinator.RequestDiagnosticsAsync(fixture.Job.Id));
        Assert.Equal("migration-queue-full", error.Message);
        Assert.Equal(fixture.Job, await fixture.Store.GetAsync(fixture.Job.Id));
    }

    [Fact]
    public async Task Diagnostics_InterruptedRunReturnsToFailedWithoutRepeating()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.Store.SaveAsync(fixture.Job with { Phase = CloudMigrationPhases.Diagnosing });
        var runner = new ProgressRunner();
        using var worker = new CloudMigrationWorker(fixture.Store, fixture.Queue, runner,
            fixture.Source, null!, null!, fixture.LibraryManager, NullLogger<CloudMigrationWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);
        var job = await fixture.Store.GetAsync(fixture.Job.Id);
        Assert.Equal(CloudMigrationPhases.Failed, job!.Phase);
        Assert.Equal(fixture.Job.Code, job.Code);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task Diagnostics_ProcessFailurePreservesTheInitiatingError()
    {
        using var fixture = await Fixture.CreateAsync();
        var runner = new ProgressRunner { BeforeCommand = _ => throw new IOException("private provider detail") };
        await fixture.Coordinator.RequestDiagnosticsAsync(fixture.Job.Id);
        using var worker = new CloudMigrationWorker(fixture.Store, fixture.Queue, runner,
            fixture.Source, null!, null!, fixture.LibraryManager, NullLogger<CloudMigrationWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        CloudMigrationJob? job;
        do { await Task.Delay(10, timeout.Token); job = await fixture.Store.GetAsync(fixture.Job.Id); }
        while (job!.Phase != CloudMigrationPhases.Failed);
        await worker.StopAsync(CancellationToken.None);
        Assert.Equal(fixture.Job.Code, job.Code);
        Assert.False(job.Diagnostics!.StartSucceeded);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(job));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(10, false)]
    [InlineData(null, true)]
    public void Diagnostics_EmptySuccessOrDurationLimitIsStillOnlyAdvisory(int? exitCode, bool timedOut)
    {
        var report = CloudMigrationDiagnosticReport.Parse(new(true, exitCode, "", "private", timedOut, false));
        Assert.Equal("remote-subset-hash-diagnostic-only", report.Method);
        Assert.Equal(exitCode, report.ExitCode);
        Assert.Equal(timedOut, report.TimedOut);
        Assert.Empty(report.Entries);
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
        public string StandardOutput { get; init; } = "";
        public Func<IReadOnlyList<string>, Task>? BeforeCommand { get; init; }
        public Func<IReadOnlyList<string>, int>? ExitFor { get; init; }
        public Task<RcloneCommandResult> RunAsync(string executable, IReadOnlyList<string> args,
            TimeSpan timeout, CancellationToken token) => RunWithProgressAsync(executable, args, timeout, _ => Task.CompletedTask, token);
        public async Task<RcloneCommandResult> RunWithProgressAsync(string executable,
            IReadOnlyList<string> args, TimeSpan timeout, Func<CloudMigrationProgress, Task> progress,
            CancellationToken token)
        {
            Commands.Add(args);
            if (BeforeCommand is not null) await BeforeCommand(args);
            await progress(new(5, 5, 1, 1, 0, DateTimeOffset.UtcNow));
            return new(true, ExitFor?.Invoke(args) ?? (args[0] == "copy" ? CopyExitCode : 0), StandardOutput, "", false, false);
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
