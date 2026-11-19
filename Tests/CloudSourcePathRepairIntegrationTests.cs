using System.Text.Json;
using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public partial class CloudMigrationRecoveryTests
{
    private static CloudSourcePathRepairState UnknownRepair() =>
        JsonSerializer.Deserialize<CloudSourcePathRepairState>("{\"phase\":999}", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static CloudSourcePathRepairState StableRepair(CloudMigrationJob job)
    {
        static string Digest(char c) => new(c, 64);
        var root = new CloudSourcePathRepairIdentity(5, 7, 11);
        var plan = new CloudSourcePathRepairPlan(1,
            new(job.Id, Guid.NewGuid(), Guid.NewGuid(), job.LibraryName, job.ProfileId, job.LocalPath, job.RemoteName, job.RemotePath,
                root, new(1, Digest('a'), 12, 99, Digest('b')), Digest('c')),
            [new(CloudSourcePathRepairKind.File, "A/first", "A/preserved-first", root with { Inode = 12 })],
            Digest('d'), Digest('e'), "");
        plan = plan with { Fingerprint = CloudSourcePathRepairTransaction.Fingerprint(plan) };
        return new(plan, CloudSourcePathRepairPhase.RolledBack, RollbackRequested: true,
            Verification: new(plan.Fingerprint, CloudSourcePathRepairVerificationKind.Original,
                plan.Binding.Source.NamespaceSha256, plan.PreservationManifestSha256));
    }

    [Fact]
    public async Task OrdinaryWritersCannotAdmitOrClearRepairOwnership()
    {
        using var fixture = await Fixture.CreateAsync();
        var original = (await fixture.Store.GetSnapshotAsync(fixture.Job.Id))!;
        var owned = fixture.Job with { SourcePathRepair = UnknownRepair() };
        Assert.False(CloudSourcePathRepairTransaction.HasStableReceipt(owned.SourcePathRepair!));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.SaveAsync(owned));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.SaveIfUnchangedAsync(original, owned));
        await using (var lease = await fixture.Store.AcquireRecoveryLeaseAsync(default))
            await lease.CompareExchangeAsync(original, owned);
        var current = (await fixture.Store.GetSnapshotAsync(fixture.Job.Id))!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.SaveAsync(fixture.Job));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.SaveIfUnchangedAsync(current, fixture.Job));
        Assert.NotNull((await fixture.Store.GetAsync(fixture.Job.Id))!.SourcePathRepair);
    }

    [Fact]
    public async Task RepairOwnerBlocksConflictRecoveryAndOrdinaryRecoveryPredicates()
    {
        using var fixture = await Fixture.CreateAsync();
        var snapshot = (await fixture.Store.GetSnapshotAsync(fixture.Job.Id))!;
        var owned = snapshot.Job with { SourcePathRepair = UnknownRepair() };
        await using (var lease = await fixture.Store.AcquireRecoveryLeaseAsync(default))
            await lease.CompareExchangeAsync(snapshot, owned);
        Assert.False(CloudMigrationRecovery.CanResume(owned));
        Assert.False(CloudMigrationRecovery.CanRebaseline(owned));
        Assert.False(CloudMigrationRecovery.CanRecoverConflicts(owned));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Coordinator.RequestConflictRecoveryAsync(
            owned.Id, new(owned.ManifestDigestSha256!, CloudConflictRecoverySession.CompanionOnlyWriter)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Coordinator.RequestResumeAsync(owned.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Coordinator.RequestDiagnosticsAsync(owned.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Coordinator.RequestRebaselineAsync(owned.Id,
            new(owned.ManifestDigestSha256!)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Coordinator.PrepareMaintenanceAsync(owned.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Coordinator.CancelMaintenanceAdmissionAsync(owned.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Coordinator.RequestCutoverAsync(owned.Id));
    }

    [Fact]
    public async Task OwnerStopsWorkerRestorationAndConflictExecutorWithoutChangingHistory()
    {
        using var fixture = await Fixture.CreateAsync();
        var snapshot = (await fixture.Store.GetSnapshotAsync(fixture.Job.Id))!;
        var session = new CloudConflictRecoverySession(Guid.NewGuid().ToString("N"), "Music-archives/test",
            CloudConflictRecoverySession.CompanionOnlyWriter, "queued", DateTimeOffset.UtcNow);
        var owned = snapshot.Job with { SourcePathRepair = UnknownRepair(), ConflictRecovery = session,
            Phase = CloudMigrationPhases.QueuedConflictRecovery };
        await using (var lease = await fixture.Store.AcquireRecoveryLeaseAsync(default))
            await lease.CompareExchangeAsync(snapshot, owned);
        var executor = new CloudConflictRecoveryExecutor(fixture.Store, (_, _) => throw new Exception("host must not start"), _ => true);
        await executor.ExecuteAsync(owned.Id);
        Assert.True(fixture.Queue.TryEnqueue(owned.Id));
        using var worker = new CloudMigrationWorker(fixture.Store, fixture.Queue, new ProgressRunner(), fixture.Source,
            null!, null!, fixture.LibraryManager, NullLogger<CloudMigrationWorker>.Instance);
        await worker.StartAsync(default);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (fixture.Queue.IsPending(owned.Id)) await Task.Delay(10, deadline.Token);
        await worker.StopAsync(default);
        Assert.False(fixture.Queue.IsPending(owned.Id));
        Assert.Equal(owned, await fixture.Store.GetAsync(owned.Id));
    }

    [Fact]
    public async Task LegacyJobSerializationOmitsAbsentRepairState()
    {
        using var fixture = await Fixture.CreateAsync();
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(fixture.Job, options);
        Assert.DoesNotContain("sourcePathRepair", System.Text.Encoding.UTF8.GetString(bytes));
        var reserialized = JsonSerializer.SerializeToUtf8Bytes(JsonSerializer.Deserialize<CloudMigrationJob>(bytes, options), options);
        Assert.Equal(bytes, reserialized);
    }

    [Fact]
    public async Task StableRepairStateIsStructuralAndCannotBeChangedByOrdinarySaves()
    {
        using var fixture = await Fixture.CreateAsync();
        var snapshot = (await fixture.Store.GetSnapshotAsync(fixture.Job.Id))!;
        var stable = StableRepair(snapshot.Job);
        Assert.True(CloudSourcePathRepairTransaction.HasStableReceipt(stable));
        await using (var lease = await fixture.Store.AcquireRecoveryLeaseAsync(default))
            await lease.CompareExchangeAsync(snapshot, snapshot.Job with { SourcePathRepair = stable });
        var current = (await fixture.Store.GetSnapshotAsync(fixture.Job.Id))!;
        var copy = JsonSerializer.Deserialize<CloudSourcePathRepairState>(
            JsonSerializer.Serialize(stable, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await fixture.Store.SaveIfUnchangedAsync(current, current.Job with { SourcePathRepair = copy, Code = "allowed" });
        var saved = (await fixture.Store.GetSnapshotAsync(fixture.Job.Id))!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.SaveAsync(saved.Job with { SourcePathRepair = null }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.SaveIfUnchangedAsync(saved,
            saved.Job with { SourcePathRepair = stable with { Cursor = 1 } }));
    }

    [Fact]
    public async Task OtherProfileCannotStartAgainstOwnedSourceRoot()
    {
        using var fixture = await Fixture.CreateAsync();
        var current = (await fixture.Store.GetSnapshotAsync(fixture.Job.Id))!;
        var owned = current.Job with { ProfileId = "another-profile", SourcePathRepair = UnknownRepair() };
        await using (var lease = await fixture.Store.AcquireRecoveryLeaseAsync(default))
            await lease.CompareExchangeAsync(current, owned);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Coordinator.StartAsync(
            new("music", "Music", CloudMigrationDirections.LocalToVfs)));
        Assert.Equal("migration-source-repair-owned", error.Message);
    }

    [Fact]
    public async Task ForeignTerminalReceiptCannotReleaseTheContainingJob()
    {
        using var fixture = await Fixture.CreateAsync();
        var snapshot = (await fixture.Store.GetSnapshotAsync(fixture.Job.Id))!;
        var foreign = StableRepair(fixture.Job with { Id = Guid.NewGuid().ToString("N") });
        Assert.True(CloudSourcePathRepairTransaction.HasStableReceipt(foreign));
        await using (var lease = await fixture.Store.AcquireRecoveryLeaseAsync(default))
            await lease.CompareExchangeAsync(snapshot, snapshot.Job with { SourcePathRepair = foreign });
        var current = (await fixture.Store.GetSnapshotAsync(fixture.Job.Id))!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.SaveAsync(current.Job with { Code = "must-block" }));
        Assert.False(CloudMigrationRecovery.CanResume(current.Job));
        Assert.DoesNotContain("sourcePathRepairOwnsJob", JsonSerializer.Serialize(current.Job, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
    [Fact]
    public async Task StableReceiptCannotBeSavedWithChangedSourceBinding()
    {
        using var fixture = await Fixture.CreateAsync();
        var snapshot = (await fixture.Store.GetSnapshotAsync(fixture.Job.Id))!;
        var stable = snapshot.Job with { SourcePathRepair = StableRepair(snapshot.Job) };
        await using (var lease = await fixture.Store.AcquireRecoveryLeaseAsync(default))
            await lease.CompareExchangeAsync(snapshot, stable);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.SaveAsync(
            stable with { LocalPath = stable.LocalPath + "-different" }));
        foreach (var changed in new[] {
            stable with { Id = Guid.NewGuid().ToString("N") },
            stable with { ProfileId = stable.ProfileId + "-different" },
            stable with { LibraryName = stable.LibraryName + "-different" },
            stable with { LocalPath = stable.LocalPath + "-different" },
            stable with { RemoteName = stable.RemoteName + "-different" },
            stable with { RemotePath = stable.RemotePath + "-different" },
            stable with { Direction = CloudMigrationDirections.VfsToLocal },
        }) Assert.True(changed.SourcePathRepairOwnsJob);
    }

}
