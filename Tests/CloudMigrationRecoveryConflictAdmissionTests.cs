using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public partial class CloudMigrationRecoveryTests
{
    [Fact]
    public async Task ConflictRecoveryOwnerBlocksStaleOrdinaryJobWrites()
    {
        using var fixture = await Fixture.CreateAsync();
        var owned = fixture.Job with { ConflictRecovery = new(Guid.NewGuid().ToString("N"),
            "Slipmat-recovery-archives/test", CloudConflictRecoverySession.CompanionOnlyWriter,
            "blocked", DateTimeOffset.UtcNow) };
        await fixture.Store.SaveAsync(owned);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.SaveAsync(fixture.Job));
        Assert.Equal(owned, await fixture.Store.GetAsync(owned.Id));
        Assert.False(CloudMigrationRecovery.CanResume(owned));
        Assert.False(CloudMigrationRecovery.CanRebaseline(owned));
    }

    [Fact]
    public async Task ConflictRecoveryAdmissionRejectsStaleConfirmationAndMissingWriterDeclaration()
    {
        using var fixture = await Fixture.CreateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Coordinator.RequestConflictRecoveryAsync(
            fixture.Job.Id, new(new string('f', 64), CloudConflictRecoverySession.CompanionOnlyWriter)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Coordinator.RequestConflictRecoveryAsync(
            fixture.Job.Id, new(fixture.Job.ManifestDigestSha256!, "unknown")));
        Assert.Equal(fixture.Job, await fixture.Store.GetAsync(fixture.Job.Id));
        Assert.False(fixture.Queue.IsPending(fixture.Job.Id));
    }

    [Fact]
    public async Task ConflictRecoveryAdmissionPreservesLibraryAndOldBaselineAndUsesIsolatedArchive()
    {
        using var fixture = await Fixture.CreateAsync();
        var queued = await fixture.Coordinator.RequestConflictRecoveryAsync(fixture.Job.Id,
            new(fixture.Job.ManifestDigestSha256!, CloudConflictRecoverySession.CompanionOnlyWriter));
        Assert.Equal(CloudMigrationPhases.QueuedConflictRecovery, queued.Phase);
        Assert.Equal(fixture.Job.Id, queued.Id);
        Assert.Equal(fixture.Job.ManifestDigestSha256, queued.ManifestDigestSha256);
        Assert.Equal(fixture.Job.LocalPath, queued.LocalPath);
        Assert.Null(queued.BackupPath);
        Assert.True(queued.ConflictRecovery!.OwnsJob);
        Assert.False(queued.ConflictRecovery.ArchiveRoot.StartsWith(queued.RemotePath + "/", StringComparison.OrdinalIgnoreCase));
        Assert.True(fixture.Queue.IsPending(queued.Id));
        Assert.Equal(queued, await fixture.Store.GetAsync(queued.Id));
    }

    [Fact]
    public async Task ConflictRecoveryRetryWaitsUntilWorkerReleasesPendingJob()
    {
        using var fixture = await Fixture.CreateAsync();
        var blocked = fixture.Job with { ConflictRecovery = new(Guid.NewGuid().ToString("N"),
            "Archive/retained", CloudConflictRecoverySession.CompanionOnlyWriter, "blocked", DateTimeOffset.UtcNow,
            Code: "conflict-remote-hash-failed", FailureStage: "hashing-cloud",
            Progress: new("hashing-cloud", 1, 2, 100, DateTimeOffset.UtcNow)) };
        await fixture.Store.SaveAsync(blocked);
        Assert.True(fixture.Queue.TryEnqueue(blocked.Id));
        Assert.True(fixture.Queue.Reader.TryRead(out _)); // currently executing, not still in the channel
        var request = new CloudConflictRecoveryRequest(blocked.ManifestDigestSha256!, CloudConflictRecoverySession.CompanionOnlyWriter);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Coordinator.RequestConflictRecoveryAsync(blocked.Id, request));
        Assert.Equal(blocked, await fixture.Store.GetAsync(blocked.Id));
        fixture.Queue.Complete(blocked.Id);
        var resumed = await fixture.Coordinator.RequestConflictRecoveryAsync(blocked.Id, request);
        Assert.Equal(CloudMigrationPhases.QueuedConflictRecovery, resumed.Phase);
        Assert.Null(resumed.ConflictRecovery!.Code);
        Assert.Null(resumed.ConflictRecovery.FailureStage);
        Assert.Null(resumed.ConflictRecovery.Progress);
        Assert.True(fixture.Queue.Reader.TryRead(out var queued));
        Assert.Equal(blocked.Id, queued);
        Assert.False(fixture.Queue.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("ascii-case")]
    [InlineData("changed-audio")]
    [InlineData("source-drift")]
    [InlineData("corrupt-inventory")]
    public async Task ConflictRecoveryWorkerRunsCanonicalVerificationAndNeverCutsOver(string scenario)
    {
        using var fixture = await Fixture.CreateAsync();
        var runner = new ProgressRunner
        {
            ExitFor = args => scenario == "ascii-case" && args.Contains("--one-way")
                && !args.Contains("--ignore-case-sync") ? 1 : 0,
        };
        var host = new WorkerConflictHost(scenario);
        await fixture.Coordinator.RequestConflictRecoveryAsync(fixture.Job.Id,
            new(fixture.Job.ManifestDigestSha256!, CloudConflictRecoverySession.CompanionOnlyWriter));
        using var worker = new CloudMigrationWorker(fixture.Store, fixture.Queue, runner,
            fixture.Source, null!, null!, fixture.LibraryManager, NullLogger<CloudMigrationWorker>.Instance,
            (_, _) => host);
        await worker.StartAsync(CancellationToken.None);
        var result = await ObserveConflictHandoffAsync(fixture);
        await worker.StopAsync(CancellationToken.None);
        Assert.Null(result.BackupPath);
        Assert.False(fixture.Config.CloudLibraries[0].Enabled);
        Assert.True(File.Exists(Path.Combine(fixture.Root, "track.flac")));
        if (scenario == "changed-audio")
        {
            Assert.Equal(CloudMigrationPhases.Failed, result.Phase);
            Assert.Equal("blocked", result.ConflictRecovery!.Status);
            Assert.Equal(fixture.Job.ManifestDigestSha256, result.ManifestDigestSha256);
            Assert.Empty(runner.Commands);
        }
        else if (scenario is "source-drift" or "corrupt-inventory")
        {
            Assert.Equal(CloudMigrationPhases.Failed, result.Phase);
            Assert.Equal(scenario == "source-drift" ? "baseline-conflict-source-changed"
                : "baseline-conflict-proof-invalid", result.Code);
            Assert.Equal("completed", result.ConflictRecovery!.Status);
            Assert.Equal(fixture.Job.ManifestDigestSha256, result.ManifestDigestSha256);
            Assert.Empty(result.BaselineHistory ?? []);
            Assert.Empty(runner.Commands);
        }
        else
        {
            Assert.Equal(CloudMigrationPhases.ReadyForCutover, result.Phase);
            Assert.Equal("completed", result.ConflictRecovery!.Status);
            Assert.Equal(1, result.ConflictRecovery.Progress!.Files);
            var subset = Assert.Single(runner.Commands, command => command.Contains("--one-way"));
            Assert.Equal(scenario == "ascii-case", subset.Contains("--ignore-case-sync"));
            Assert.Contains("--download", subset);
            var copy = Assert.Single(runner.Commands, command => command[0] == "copy");
            Assert.Contains("--immutable", copy);
            Assert.DoesNotContain("--ignore-case-sync", copy);
            var check = Assert.Single(runner.Commands, command => command[0] == "check" && !command.Contains("--one-way"));
            Assert.Contains("--download", check);
            Assert.DoesNotContain("--ignore-case-sync", check);
            var cutover = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Coordinator.RequestCutoverAsync(result.Id));
            Assert.Equal("maintenance-offline-runner-required", cutover.Message);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitConflictRebaselineAndCopyResumeUseTheNewlyVerifiedSource(bool restartDuringBaseline)
    {
        using var fixture = await Fixture.CreateAsync();
        var host = new WorkerConflictHost("ascii-case");
        await fixture.Coordinator.RequestConflictRecoveryAsync(fixture.Job.Id,
            new(fixture.Job.ManifestDigestSha256!, CloudConflictRecoverySession.CompanionOnlyWriter));
        Assert.True(fixture.Queue.Reader.TryRead(out var jobId));
        var executor = new CloudConflictRecoveryExecutor(fixture.Store, (_, _) => host, _ => true);
        var recovered = await executor.ExecuteAsync(jobId!);
        fixture.Queue.Complete(jobId!);
        Assert.Equal(CloudMigrationPhases.QueuedRebaseline, recovered.Phase);
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "additional.flac"), "other audio");
        var adoptedSource = await CloudMigrationManifestBuilder.CaptureAsync(fixture.Root);
        Assert.NotEqual(recovered.ManifestDigestSha256, adoptedSource.DigestSha256);
        await fixture.Store.SaveAsync(recovered with
        {
            Phase = CloudMigrationPhases.Failed,
            Code = "baseline-conflict-source-changed",
        });
        var queued = await fixture.Coordinator.RequestRebaselineAsync(jobId!,
            new(recovered.ManifestDigestSha256!));
        if (restartDuringBaseline)
        {
            await fixture.Store.SaveAsync(queued with
            {
                Phase = CloudMigrationPhases.CheckingBaseline,
                Code = "baseline-checking-explicit-conflict-review",
            });
        }

        var firstRunner = new ProgressRunner { CopyExitCode = 1 };
        using (var worker = new CloudMigrationWorker(fixture.Store, fixture.Queue, firstRunner,
            fixture.Source, null!, null!, fixture.LibraryManager, NullLogger<CloudMigrationWorker>.Instance,
            (_, _) => host))
        {
            await worker.StartAsync(CancellationToken.None);
            var failedCopy = await ObserveConflictHandoffAsync(fixture);
            await worker.StopAsync(CancellationToken.None);
            Assert.Equal("copy-failed", failedCopy.Code);
            Assert.Equal(adoptedSource.DigestSha256, failedCopy.ManifestDigestSha256);
            Assert.Contains(firstRunner.Commands, command => command.Contains("--one-way")
                && command.Contains("--ignore-case-sync") && command.Contains("--download"));
        }

        await fixture.Coordinator.RequestResumeAsync(jobId!);
        var retryRunner = new ProgressRunner();
        using var resumed = new CloudMigrationWorker(fixture.Store, fixture.Queue, retryRunner,
            fixture.Source, null!, null!, fixture.LibraryManager, NullLogger<CloudMigrationWorker>.Instance,
            (_, _) => host);
        await resumed.StartAsync(CancellationToken.None);
        var ready = await ObserveConflictHandoffAsync(fixture);
        await resumed.StopAsync(CancellationToken.None);
        Assert.Equal(CloudMigrationPhases.ReadyForCutover, ready.Phase);
        Assert.Equal(adoptedSource.DigestSha256, ready.ManifestDigestSha256);
        Assert.Equal(new[] { adoptedSource.DigestSha256, adoptedSource.DigestSha256 }, host.VerifiedSourceDigests);
        Assert.Equal(new[] { "copy", "check" }, retryRunner.Commands.Select(command => command[0]));
        Assert.All(retryRunner.Commands, command => Assert.DoesNotContain("--ignore-case-sync", command));
    }

    private static async Task<CloudMigrationJob> ObserveConflictHandoffAsync(Fixture fixture)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        CloudMigrationJob? result;
        do
        {
            await Task.Delay(10, deadline.Token);
            result = await fixture.Store.GetAsync(fixture.Job.Id, deadline.Token);
        } while (result!.Phase is not (CloudMigrationPhases.ReadyForCutover or CloudMigrationPhases.Failed));
        return result;
    }

    private sealed class WorkerConflictHost(string scenario) : ICloudConflictRecoveryHost, ICloudConflictRecoveryProgressHost, ICloudConflictHandoffHost
    {
        private string? _inventoryPath;
        public Func<CloudConflictPreflightProgress, CancellationToken, Task>? ReportProgress { get; set; }
        public List<string> VerifiedSourceDigests { get; } = [];
        public async Task<CloudConflictInventory> CaptureAsync(CloudConflictRecoveryBinding binding, string path, CancellationToken token)
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(binding.LocalPath, "track.flac"), token);
            var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (ReportProgress is not null)
                await ReportProgress(new("hashing-local", 1, 1, bytes.Length, DateTimeOffset.UtcNow), token);
            var local = new CloudConflictObject(bytes.Length, digest, "local", CloudConflictContentKind.Audio);
            var remote = local with { Identity = "remote", Sha256 = scenario == "changed-audio" ? new string('e', 64) : digest };
            var entries = new[] { new CloudConflictCandidate("track.flac", local, remote,
                scenario is "ascii-case" or "source-drift" ? "Track.flac" : null) };
            var document = JsonSerializer.SerializeToUtf8Bytes(new CloudConflictInventoryDocument(binding, entries),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await File.WriteAllBytesAsync(path, document, token);
            _inventoryPath = path;
            return new(binding, new(path, Convert.ToHexStringLower(SHA256.HashData(document)), 1), entries);
        }
        public async Task<CloudConflictRecoveryObservation> ObserveAsync(CloudConflictRecoveryPlan plan, CloudConflictRecoveryState state, CancellationToken token)
        {
            if (scenario == "source-drift") await File.WriteAllTextAsync(Path.Combine(plan.Binding.LocalPath, "second.flac"), "other audio", token);
            if (scenario == "corrupt-inventory") await File.AppendAllTextAsync(_inventoryPath!, " ", token);
            return CloudConflictRecoveryObservation.Original(plan);
        }
        public Task<bool> ValidateNamespaceAsync(CloudConflictRecoveryBinding binding, string sourceManifestSha256,
            string? knownArchivePath, CancellationToken token)
        {
            VerifiedSourceDigests.Add(sourceManifestSha256);
            return Task.FromResult(scenario == "ascii-case");
        }
        public Task ArchiveAsync(CloudConflictRecoveryPlan plan, int index, CancellationToken token) => throw new InvalidOperationException("unexpected-archive");
        public Task MutateAsync(CloudConflictRecoveryPlan plan, int index, CancellationToken token) => throw new InvalidOperationException("unexpected-mutation");
    }
}
