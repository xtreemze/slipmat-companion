using Jellyfin.Plugin.AudioGateway.Services;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

[CollectionDefinition("Shared rclone recovery fence", DisableParallelization = true)]
public sealed class SharedRcloneRecoveryFenceCollection;

[Collection("Shared rclone recovery fence")]
public sealed class CloudConflictRcloneFenceIntegrationTests
{
    [Fact]
    public async Task RealRunnerRejectsOtherCommandsBeforeStartingProcess()
    {
        await using var lease = await CloudConflictRecoveryFence.Shared.AcquireAsync(
            Guid.NewGuid().ToString("N"), new string('a', 64));
        var result = await new RcloneProcessRunner().RunAsync("missing-recovery-test-executable",
            ["lsjson", "test:music"], TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.False(result.StartSucceeded);
        Assert.Equal("remote-recovery-in-progress", result.ErrorCode);
    }

    [Fact]
    public async Task BusyCommandIsReportedAsRecoveryRatherThanUnavailableRclone()
    {
        await using var lease = await CloudConflictRecoveryFence.Shared.AcquireAsync(
            Guid.NewGuid().ToString("N"), new string('b', 64));
        var result = await new RcloneCliHost(runner: new RcloneProcessRunner()).ProbeAsync("test", "music");
        Assert.False(result.Success);
        Assert.Equal("remote-recovery-in-progress", result.ErrorCode);
    }
}
