using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Api;
using Jellyfin.Plugin.AudioGateway.Configuration;
using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public sealed class CloudMigrationActionsTests
{
    [Fact]
    public async Task FailedBaselineListDoesNotOfferTheRejectedOriginalResume()
    {
        var root = Directory.CreateTempSubdirectory("migration-actions-").FullName;
        try
        {
            var store = new CloudMigrationStore(new Source(new() { StoreRoot = root }));
            var job = new CloudMigrationJob(Guid.NewGuid().ToString("N"), "legacy", "Music",
                CloudMigrationDirections.LocalToVfs, CloudMigrationPhases.Failed, "baseline-verification-failed",
                "/music", null, null, "tele2", "Slipmat", "", false, null,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, ManifestVersion: 1,
                ManifestDigestSha256: new string('a', 64), ManifestFileCount: 1, ManifestTotalBytes: 5);
            await store.SaveAsync(job);
            var coordinator = new CloudMigrationCoordinator(store, new(), null!, null!, null!, null!);
            var response = await new CloudMigrationController(coordinator).List(CancellationToken.None);
            var body = Assert.IsType<CloudMigrationListResponse>(Assert.IsType<OkObjectResult>(response.Result).Value);
            Assert.False(body.Actions![job.Id].Resume);
            Assert.True(body.Actions[job.Id].Rebaseline);
            Assert.True(body.Actions[job.Id].Diagnose);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RequestResumeAsync(job.Id));
            Assert.Equal("migration-not-resumable", error.Message);
            Assert.Equal(job, await store.GetAsync(job.Id));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class Source(PluginConfiguration configuration) : ICloudProjectionConfigurationSource
    {
        public PluginConfiguration GetCurrent() => configuration;
    }
}
