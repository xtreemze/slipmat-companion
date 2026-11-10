using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public sealed class CloudMigrationJournalTests
{
    [Fact]
    public async Task CompareExchangeRejectsStaleRevisionAndPreservesWinner()
    {
        using var directory = new JournalDirectory();
        await using var lease = await new CloudMigrationJournal(directory.Path).AcquireAsync();
        var first = await lease.CompareExchangeAsync(null, Job());
        var second = await lease.CompareExchangeAsync(first, first.Job with { Code = "newer" });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            lease.CompareExchangeAsync(first, first.Job with { Code = "stale" }));
        Assert.Equal("migration-journal-revision-changed", error.Message);
        Assert.Equal(second, await lease.ReadAsync(first.Job.Id));
    }

    [Fact]
    public async Task SeparateStoreInstancesCannotOwnLeaseTogether()
    {
        using var directory = new JournalDirectory();
        var first = new CloudMigrationJournal(directory.Path);
        var second = new CloudMigrationJournal(directory.Path);
        await using (var lease = await first.AcquireAsync())
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await using var competing = await second.AcquireAsync(cancellation.Token);
            });
        }
        await using var released = await second.AcquireAsync();
        Assert.Null(await released.ReadAsync(Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public async Task CorruptJournalIsNeverTreatedAsMissing()
    {
        using var directory = new JournalDirectory();
        var job = Job();
        await File.WriteAllTextAsync(System.IO.Path.Combine(directory.Path, job.Id + ".json"), "{");
        await using var lease = await new CloudMigrationJournal(directory.Path).AcquireAsync();
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => lease.ReadAsync(job.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => lease.CompareExchangeAsync(null, job));
    }

    [Fact]
    public async Task AdmissionPreventsStaleOrdinarySaveAndOnlineCutover()
    {
        using var directory = new JournalDirectory();
        var store = new CloudMigrationStore(new JournalConfiguration(directory.Path));
        var job = Job();
        await store.SaveAsync(job);
        var original = (await store.GetSnapshotAsync(job.Id))!;
        var policy = new CloudLibraryScanPolicy(false, false, false, false, false,
            false, false, false, false, false, []);
        var admission = new CloudMaintenanceAdmission(Guid.NewGuid(), "Music", "/music", "/library/options.xml",
            "/config/plugin.xml", policy, "music", "remote", "Music", new string('a', 64),
            "rclone-check-download-content", new string('b', 64), new string('c', 64), false, "", DateTimeOffset.UtcNow);
        var admitted = job with { MaintenanceAdmission = admission };
        await store.SaveIfUnchangedAsync(original, admitted);
        var stale = await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(job));
        Assert.Equal("migration-maintenance-admitted", stale.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveIfUnchangedAsync(original, job));
        var coordinator = new CloudMigrationCoordinator(store, new CloudMigrationQueue(),
            new JournalConfiguration(directory.Path), null!, null!, null!);
        var online = await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RequestCutoverAsync(job.Id));
        Assert.Equal("maintenance-offline-runner-required", online.Message);
        Assert.NotNull((await store.GetAsync(job.Id))!.MaintenanceAdmission);
        var canceled = await coordinator.CancelMaintenanceAdmissionAsync(job.Id);
        Assert.Null(canceled.MaintenanceAdmission);
        Assert.Equal(CloudMigrationPhases.ReadyForCutover, canceled.Phase);
        Assert.Null(canceled.Maintenance);
    }

    private static CloudMigrationJob Job()
    {
        var now = DateTimeOffset.UtcNow;
        return new(Guid.NewGuid().ToString("N"), "music", "Music", "local-to-vfs",
            CloudMigrationPhases.ReadyForCutover, "verified", "/music", null, null,
            "remote", "Music", "", false, "rclone-check-download-content", now, now);
    }

    private sealed class JournalDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("slipmat-journal-").FullName;
        public void Dispose() => Directory.Delete(Path, true);
    }

    private sealed class JournalConfiguration(string root) : ICloudProjectionConfigurationSource
    {
        public Jellyfin.Plugin.AudioGateway.Configuration.PluginConfiguration GetCurrent() => new() { StoreRoot = root };
    }
}
