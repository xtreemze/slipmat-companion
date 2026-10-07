using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Configuration;
using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public class CloudMigrationStoreTests
{
    [Fact]
    public async Task Store_RoundTripsDurableMigrationState()
    {
        using var temp = new TemporaryDirectory();
        var source = new FakeConfigurationSource(new PluginConfiguration
        {
            StoreRoot = temp.Path,
        });
        var store = new CloudMigrationStore(source);
        var now = DateTimeOffset.UtcNow;
        var job = new CloudMigrationJob(
            Guid.NewGuid().ToString("N"),
            "music",
            "Music",
            CloudMigrationDirections.LocalToVfs,
            CloudMigrationPhases.ReadyForCutover,
            "verified-ready-for-cutover",
            Path.Combine(temp.Path, "Music"),
            null,
            null,
            "tele2",
            "Media/Music",
            string.Empty,
            false,
            "rclone-check-download-content",
            now,
            now);

        await store.SaveAsync(job, CancellationToken.None);
        var loaded = await store.GetAsync(job.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(job.Id, loaded!.Id);
        Assert.Equal(CloudMigrationPhases.ReadyForCutover, loaded.Phase);
        Assert.Equal(job.Verification, loaded.Verification);
        Assert.Equal(job.ManifestDigestSha256, loaded.ManifestDigestSha256);
        Assert.Equal(job.ManifestFileCount, loaded.ManifestFileCount);
    }

    [Fact]
    public async Task Finalize_RequiresExactRetainedBackupPath()
    {
        using var temp = new TemporaryDirectory();
        var source = new FakeConfigurationSource(new PluginConfiguration
        {
            StoreRoot = temp.Path,
        });
        var store = new CloudMigrationStore(source);
        var queue = new CloudMigrationQueue();
        var backup = Path.Combine(temp.Path, "Music.slipmat-rollback");
        var now = DateTimeOffset.UtcNow;
        var job = new CloudMigrationJob(
            Guid.NewGuid().ToString("N"),
            "music",
            "Music",
            CloudMigrationDirections.LocalToVfs,
            CloudMigrationPhases.ReadyToFinalize,
            "vfs-active-local-backup-retained",
            Path.Combine(temp.Path, "Music"),
            null,
            backup,
            "tele2",
            "Media/Music",
            string.Empty,
            false,
            "rclone-check-download-content",
            now,
            now,
            VfsCertificationState: "passed",
            VfsCertificationCode: "test-certified",
            VfsCertifiedAt: now);
        await store.SaveAsync(job, CancellationToken.None);

        var coordinator = new CloudMigrationCoordinator(
            store,
            queue,
            source,
            null!,
            null!,
            null!);

        var mismatch = await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.RequestFinalizeAsync(
                job.Id,
                new CloudMigrationFinalizeRequest(backup + "-wrong"),
                CancellationToken.None));
        Assert.Equal("finalize-confirmation-mismatch", mismatch.Message);

        var queued = await coordinator.RequestFinalizeAsync(
            job.Id,
            new CloudMigrationFinalizeRequest(backup),
            CancellationToken.None);
        Assert.Equal(CloudMigrationPhases.Finalizing, queued.Phase);
        Assert.Equal("finalize-queued", queued.Code);
    }

    [Fact]
    public async Task Finalize_LocalToVfs_RequiresCertification()
    {
        using var temp = new TemporaryDirectory();
        var source = new FakeConfigurationSource(new PluginConfiguration { StoreRoot = temp.Path });
        var store = new CloudMigrationStore(source);
        var backup = Path.Combine(temp.Path, "Music.slipmat-rollback");
        var now = DateTimeOffset.UtcNow;
        var job = new CloudMigrationJob(
            Guid.NewGuid().ToString("N"), "music", "Music",
            CloudMigrationDirections.LocalToVfs, CloudMigrationPhases.ReadyToFinalize,
            "vfs-active-local-backup-retained-uncertified",
            Path.Combine(temp.Path, "Music"), null, backup, "tele2", "Media/Music",
            string.Empty, false, "rclone-check-download-content", now, now,
            VfsCertificationState: "observing",
            VfsCertificationCode: "mount-and-library-ready");
        await store.SaveAsync(job, CancellationToken.None);

        var coordinator = new CloudMigrationCoordinator(
            store, new CloudMigrationQueue(), source, null!, null!, null!);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.RequestFinalizeAsync(
                job.Id, new CloudMigrationFinalizeRequest(backup), CancellationToken.None));

        Assert.Equal("vfs-certification-required", error.Message);
    }

    private sealed class FakeConfigurationSource : ICloudProjectionConfigurationSource
    {
        private readonly PluginConfiguration _configuration;

        public FakeConfigurationSource(PluginConfiguration configuration)
        {
            _configuration = configuration;
        }

        public PluginConfiguration GetCurrent() => _configuration;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = Directory.CreateTempSubdirectory(
                "audio-gateway-cloud-migration-test").FullName;
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
