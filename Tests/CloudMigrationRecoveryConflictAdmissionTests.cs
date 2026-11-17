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
            Code: "conflict-remote-hash-failed", FailureStage: "hashing-cloud") };
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
        Assert.True(fixture.Queue.Reader.TryRead(out var queued));
        Assert.Equal(blocked.Id, queued);
        Assert.False(fixture.Queue.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConflictRecoveryWorkerRunsCanonicalVerificationAndNeverCutsOver(bool audioConflict)
    {
        using var fixture = await Fixture.CreateAsync();
        var runner = new ProgressRunner();
        var host = new WorkerConflictHost(audioConflict);
        await fixture.Coordinator.RequestConflictRecoveryAsync(fixture.Job.Id,
            new(fixture.Job.ManifestDigestSha256!, CloudConflictRecoverySession.CompanionOnlyWriter));
        using var worker = new CloudMigrationWorker(fixture.Store, fixture.Queue, runner,
            fixture.Source, null!, null!, fixture.LibraryManager, NullLogger<CloudMigrationWorker>.Instance,
            (_, _) => host);
        await worker.StartAsync(CancellationToken.None);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        CloudMigrationJob? result;
        do
        {
            await Task.Delay(10, deadline.Token);
            result = await fixture.Store.GetAsync(fixture.Job.Id, deadline.Token);
        } while (result!.Phase is not (CloudMigrationPhases.ReadyForCutover or CloudMigrationPhases.Failed));
        await worker.StopAsync(CancellationToken.None);
        Assert.Null(result.BackupPath);
        Assert.False(fixture.Config.CloudLibraries[0].Enabled);
        Assert.True(File.Exists(Path.Combine(fixture.Root, "track.flac")));
        if (audioConflict)
        {
            Assert.Equal(CloudMigrationPhases.Failed, result.Phase);
            Assert.Equal("blocked", result.ConflictRecovery!.Status);
            Assert.Equal(fixture.Job.ManifestDigestSha256, result.ManifestDigestSha256);
            Assert.Empty(runner.Commands);
        }
        else
        {
            Assert.Equal(CloudMigrationPhases.ReadyForCutover, result.Phase);
            Assert.Equal("completed", result.ConflictRecovery!.Status);
            Assert.Equal(1, result.ConflictRecovery.Progress!.Files);
            Assert.Contains(runner.Commands, command => command[0] == "copy" && command.Contains("--immutable"));
            Assert.Contains(runner.Commands, command => command[0] == "check" && command.Contains("--download"));
            var cutover = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Coordinator.RequestCutoverAsync(result.Id));
            Assert.Equal("maintenance-offline-runner-required", cutover.Message);
        }
    }

    private sealed class WorkerConflictHost(bool audioConflict) : ICloudConflictRecoveryHost, ICloudConflictRecoveryProgressHost
    {
        public Func<CloudConflictPreflightProgress, CancellationToken, Task>? ReportProgress { get; set; }
        public async Task<CloudConflictInventory> CaptureAsync(CloudConflictRecoveryBinding binding, string path, CancellationToken token)
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(binding.LocalPath, "track.flac"), token);
            var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (ReportProgress is not null)
                await ReportProgress(new("hashing-local", 1, 1, bytes.Length, DateTimeOffset.UtcNow), token);
            var local = new CloudConflictObject(bytes.Length, digest, "local", CloudConflictContentKind.Audio);
            var remote = local with { Identity = "remote", Sha256 = audioConflict ? new string('e', 64) : digest };
            var entries = new[] { new CloudConflictCandidate("track.flac", local, remote) };
            var document = JsonSerializer.SerializeToUtf8Bytes(new CloudConflictInventoryDocument(binding, entries),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await File.WriteAllBytesAsync(path, document, token);
            return new(binding, new(path, Convert.ToHexStringLower(SHA256.HashData(document)), 1), entries);
        }
        public Task<CloudConflictRecoveryObservation> ObserveAsync(CloudConflictRecoveryPlan plan, CloudConflictRecoveryState state, CancellationToken token)
            => Task.FromResult(CloudConflictRecoveryObservation.Original(plan));
        public Task ArchiveAsync(CloudConflictRecoveryPlan plan, int index, CancellationToken token) => throw new InvalidOperationException("unexpected-archive");
        public Task MutateAsync(CloudConflictRecoveryPlan plan, int index, CancellationToken token) => throw new InvalidOperationException("unexpected-mutation");
    }
}
