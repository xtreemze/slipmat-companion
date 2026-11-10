using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public class CloudMaintenanceTransactionTests
{
    [Theory]
    [InlineData("job")]
    [InlineData("00000000000000000000000000000000")]
    public void RejectsInvalidJobIdentity(string jobId)
    {
        Assert.Equal(CloudMaintenanceAction.Blocked, CloudMaintenanceTransaction.Decide(Intent(CloudMaintenanceDirection.Promote, CloudMaintenancePhase.Prepared) with { JobId = jobId }, Local()).Action);
    }

    [Theory]
    [InlineData("library-id", "/music", "/backup", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "rclone-check-download-content", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    [InlineData("11111111-1111-1111-1111-111111111111", "music", "/backup", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "rclone-check-download-content", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    [InlineData("11111111-1111-1111-1111-111111111111", "/a/../music", "/backup", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "rclone-check-download-content", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    [InlineData("11111111-1111-1111-1111-111111111111", "/music", "/music/backup", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "rclone-check-download-content", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    [InlineData("11111111-1111-1111-1111-111111111111", "/music/", "/music/backup", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "rclone-check-download-content", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    [InlineData("11111111-1111-1111-1111-111111111111", "/music", "/backup", "bad", "rclone-check-download-content", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    [InlineData("11111111-1111-1111-1111-111111111111", "/music", "/backup", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "verified", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    [InlineData("11111111-1111-1111-1111-111111111111", "/music", "/backup", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "rclone-check-download-content", "bad")]
    public void RejectsInvalidCustody(string libraryId, string local, string backup, string manifest, string verification, string config)
    {
        var intent = Intent(CloudMaintenanceDirection.Promote, CloudMaintenancePhase.Prepared);
        intent = intent with { Custody = intent.Custody with { LibraryId = libraryId, LocalPath = local, BackupPath = backup, ManifestDigest = manifest, Verification = verification, ConfigurationDigest = config } };
        Assert.Equal(CloudMaintenanceAction.Blocked, CloudMaintenanceTransaction.Decide(intent, Local()).Action);
    }

    [Fact]
    public void PromotionJournalsBeforeMovingSource()
    {
        var decision = CloudMaintenanceTransaction.Decide(Intent(CloudMaintenanceDirection.Promote, CloudMaintenancePhase.Prepared),
            Observation(CloudMaintenancePathState.Expected, CloudMaintenancePathState.Missing, CloudMaintenanceMountState.Missing));
        Assert.Equal(CloudMaintenanceAction.PersistPhase, decision.Action);
        Assert.Equal(CloudMaintenancePhase.MoveIntent, decision.NextPhase);
    }

    [Fact]
    public void ReplayAfterMoveContinuesWithoutMovingAgain()
    {
        var decision = CloudMaintenanceTransaction.Decide(Intent(CloudMaintenanceDirection.Promote, CloudMaintenancePhase.MoveIntent),
            Observation(CloudMaintenancePathState.Missing, CloudMaintenancePathState.Expected, CloudMaintenanceMountState.Missing));
        Assert.Equal(CloudMaintenanceAction.PersistPhase, decision.Action);
        Assert.Equal(CloudMaintenancePhase.Moved, decision.NextPhase);
    }

    [Fact]
    public void ForeignMountNeverUnmounts()
    {
        var decision = CloudMaintenanceTransaction.Decide(Intent(CloudMaintenanceDirection.Rollback, CloudMaintenancePhase.UnmountIntent),
            Observation(CloudMaintenancePathState.MountPoint, CloudMaintenancePathState.Expected, CloudMaintenanceMountState.Other));
        Assert.Equal(CloudMaintenanceAction.Blocked, decision.Action);
    }

    [Fact]
    public void ReplayAfterLocalRestoreDoesNotReportMissingBackup()
    {
        var decision = CloudMaintenanceTransaction.Decide(Intent(CloudMaintenanceDirection.Rollback, CloudMaintenancePhase.RestoreIntent),
            Observation(CloudMaintenancePathState.Expected, CloudMaintenancePathState.Missing, CloudMaintenanceMountState.Missing));
        Assert.Equal(CloudMaintenanceAction.PersistPhase, decision.Action);
        Assert.Equal(CloudMaintenancePhase.Restored, decision.NextPhase);
    }

    [Fact]
    public void MissingLeaseBlocksEveryAction()
    {
        var observation = Observation(CloudMaintenancePathState.Expected, CloudMaintenancePathState.Missing, CloudMaintenanceMountState.Missing) with { LeaseHeld = false };
        Assert.Equal(CloudMaintenanceAction.Blocked,
            CloudMaintenanceTransaction.Decide(Intent(CloudMaintenanceDirection.Promote, CloudMaintenancePhase.Prepared), observation).Action);
    }

    [Theory]
    [InlineData(CloudMaintenancePhase.Prepared)]
    [InlineData(CloudMaintenancePhase.UnmountIntent)]
    [InlineData(CloudMaintenancePhase.Unmounted)]
    [InlineData(CloudMaintenancePhase.RestoreIntent)]
    [InlineData(CloudMaintenancePhase.Restored)]
    [InlineData(CloudMaintenancePhase.ConfigIntent)]
    [InlineData(CloudMaintenancePhase.Stable)]
    public void ForeignMountBlocksAllRollbackStages(CloudMaintenancePhase phase)
    {
        var result = CloudMaintenanceTransaction.Decide(Intent(CloudMaintenanceDirection.Rollback, phase),
            Observation(CloudMaintenancePathState.MountPoint, CloudMaintenancePathState.Expected, CloudMaintenanceMountState.Other));
        Assert.Equal(CloudMaintenanceAction.Blocked, result.Action);
    }

    [Fact]
    public void UnrecordedMountIdentityCannotBeAdoptedOnReplay()
    {
        var intent = Intent(CloudMaintenanceDirection.Promote, CloudMaintenancePhase.MountIntent) with { MountIdentity = null };
        var result = CloudMaintenanceTransaction.Decide(intent,
            Observation(CloudMaintenancePathState.MountPoint, CloudMaintenancePathState.Expected, CloudMaintenanceMountState.Expected));
        Assert.Equal(CloudMaintenanceAction.Blocked, result.Action);
    }

    [Fact]
    public void PromotionCannotBeginWithAPreexistingMountIdentity()
    {
        var intent = Intent(CloudMaintenanceDirection.Promote, CloudMaintenancePhase.Prepared) with { MountIdentity = "stale-mount" };
        Assert.Equal(CloudMaintenanceAction.Blocked, CloudMaintenanceTransaction.Decide(intent, Local()).Action);
    }

    [Theory]
    [InlineData(CloudMaintenancePhase.Prepared, CloudMaintenanceAction.PersistPhase)]
    [InlineData(CloudMaintenancePhase.UnmountIntent, CloudMaintenanceAction.Unmount)]
    public void OnlyRecordedMountCanBeginRollback(CloudMaintenancePhase phase, CloudMaintenanceAction expected)
    {
        var observation = Observation(CloudMaintenancePathState.MountPoint, CloudMaintenancePathState.Expected, CloudMaintenanceMountState.Expected);
        Assert.Equal(expected, CloudMaintenanceTransaction.Decide(Intent(CloudMaintenanceDirection.Rollback, phase), observation).Action);
        Assert.Equal(CloudMaintenanceAction.Blocked, CloudMaintenanceTransaction.Decide(Intent(CloudMaintenanceDirection.Rollback, phase) with { MountIdentity = null }, observation).Action);
    }

    [Fact]
    public void ConfigurationMustMatchBeforeTransactionCanComplete()
    {
        var intent = Intent(CloudMaintenanceDirection.Rollback, CloudMaintenancePhase.Stable);
        var observation = Observation(CloudMaintenancePathState.Expected, CloudMaintenancePathState.Missing, CloudMaintenanceMountState.Missing);
        Assert.Equal(CloudMaintenanceAction.Blocked, CloudMaintenanceTransaction.Decide(intent, observation).Action);
        Assert.Equal(CloudMaintenanceAction.Complete, CloudMaintenanceTransaction.Decide(intent, observation with { ProfileMatchesTarget = true, PolicyMatchesTarget = true }).Action);
    }

    [Fact]
    public void UnknownUnmountedPathIsNeverRemoved()
    {
        var result = CloudMaintenanceTransaction.Decide(Intent(CloudMaintenanceDirection.Rollback, CloudMaintenancePhase.RestoreIntent),
            Observation(CloudMaintenancePathState.Other, CloudMaintenancePathState.Expected, CloudMaintenanceMountState.Missing));
        Assert.Equal(CloudMaintenanceAction.Blocked, result.Action);
    }

    [Fact]
    public async Task FailedJournalCasNeverMovesMedia()
    {
        var fake = new FakeOperations(Intent(CloudMaintenanceDirection.Promote, CloudMaintenancePhase.Prepared),
            Observation(CloudMaintenancePathState.Expected, CloudMaintenancePathState.Missing, CloudMaintenanceMountState.Missing));
        var result = await CloudMaintenanceTransaction.RunAsync(JobId, fake);
        Assert.Equal(CloudMaintenanceAction.Blocked, result.Action);
        Assert.Equal(0, fake.Effects);
    }

    [Fact]
    public async Task FailedMountIdentityPersistenceStopsWithoutAdoption()
    {
        var fake = new FakeOperations(Intent(CloudMaintenanceDirection.Promote, CloudMaintenancePhase.MountIntent) with { MountIdentity = null },
            Observation(CloudMaintenancePathState.Missing, CloudMaintenancePathState.Expected, CloudMaintenanceMountState.Missing));
        var result = await CloudMaintenanceTransaction.RunAsync(JobId, fake);
        Assert.Equal(CloudMaintenanceAction.Blocked, result.Action);
        Assert.Equal(1, fake.Effects);
    }

    [Theory]
    [InlineData(CloudMaintenanceDirection.Promote, CloudMaintenancePhase.Prepared, "local")]
    [InlineData(CloudMaintenanceDirection.Promote, CloudMaintenancePhase.MoveIntent, "local")]
    [InlineData(CloudMaintenanceDirection.Promote, CloudMaintenancePhase.MoveIntent, "moved")]
    [InlineData(CloudMaintenanceDirection.Promote, CloudMaintenancePhase.Moved, "moved")]
    [InlineData(CloudMaintenanceDirection.Promote, CloudMaintenancePhase.MountIntent, "moved")]
    [InlineData(CloudMaintenanceDirection.Promote, CloudMaintenancePhase.MountIntent, "mounted")]
    [InlineData(CloudMaintenanceDirection.Promote, CloudMaintenancePhase.Mounted, "mounted")]
    [InlineData(CloudMaintenanceDirection.Promote, CloudMaintenancePhase.ConfigIntent, "mounted")]
    [InlineData(CloudMaintenanceDirection.Rollback, CloudMaintenancePhase.Prepared, "mounted")]
    [InlineData(CloudMaintenanceDirection.Rollback, CloudMaintenancePhase.UnmountIntent, "mounted")]
    [InlineData(CloudMaintenanceDirection.Rollback, CloudMaintenancePhase.UnmountIntent, "unmounted")]
    [InlineData(CloudMaintenanceDirection.Rollback, CloudMaintenancePhase.Unmounted, "unmounted")]
    [InlineData(CloudMaintenanceDirection.Rollback, CloudMaintenancePhase.RestoreIntent, "unmounted")]
    [InlineData(CloudMaintenanceDirection.Rollback, CloudMaintenancePhase.RestoreIntent, "local")]
    [InlineData(CloudMaintenanceDirection.Rollback, CloudMaintenancePhase.Restored, "local")]
    [InlineData(CloudMaintenanceDirection.Rollback, CloudMaintenancePhase.ConfigIntent, "local")]
    public async Task ReplayCompletesFromEveryIntentBoundary(CloudMaintenanceDirection direction, CloudMaintenancePhase phase, string state)
    {
        var intent = Intent(direction, phase);
        if (direction == CloudMaintenanceDirection.Promote && state == "mounted") intent = intent with { MountIdentity = "mount-instance" };
        var simulator = new SimulatedOperations(intent, state);
        var result = await CloudMaintenanceTransaction.RunAsync(JobId, simulator);
        Assert.Equal(CloudMaintenanceAction.Complete, result.Action);
        Assert.Equal(CloudMaintenancePhase.Stable, simulator.Current.Phase);
        Assert.True(simulator.Profile && simulator.Policy);
        Assert.Equal(direction == CloudMaintenanceDirection.Promote ? "mounted" : "local", simulator.State);
    }

    [Fact]
    public async Task PartiallyAppliedConfigurationIsReplayedUntilBothMatch()
    {
        var simulator = new SimulatedOperations(Intent(CloudMaintenanceDirection.Rollback, CloudMaintenancePhase.ConfigIntent), "local") { Profile = true };
        var result = await CloudMaintenanceTransaction.RunAsync(JobId, simulator);
        Assert.Equal(CloudMaintenanceAction.Complete, result.Action);
        Assert.True(simulator.Profile && simulator.Policy);
        Assert.Equal(1, simulator.ConfigurationEffects);
    }

    private sealed class SimulatedOperations(CloudMaintenanceIntent intent, string state) : ICloudMaintenanceOperations
    {
        public CloudMaintenanceIntent Current { get; private set; } = intent;
        public string State { get; private set; } = state;
        public bool Profile { get; set; }
        public bool Policy { get; set; }
        public int ConfigurationEffects { get; private set; }
        public Task<CloudMaintenanceIntent?> ReadAsync(string jobId, CancellationToken cancellationToken) => Task.FromResult<CloudMaintenanceIntent?>(Current);
        public Task<CloudMaintenanceObservation> ObserveAsync(CloudMaintenanceIntent current, CancellationToken cancellationToken)
        {
            var observation = State switch
            {
                "local" => Local(),
                "moved" => Observation(CloudMaintenancePathState.Missing, CloudMaintenancePathState.Expected, CloudMaintenanceMountState.Missing),
                "mounted" => Observation(CloudMaintenancePathState.MountPoint, CloudMaintenancePathState.Expected, CloudMaintenanceMountState.Expected),
                "unmounted" => Observation(CloudMaintenancePathState.MountPoint, CloudMaintenancePathState.Expected, CloudMaintenanceMountState.Missing),
                _ => throw new InvalidOperationException(),
            };
            return Task.FromResult(observation with { ProfileMatchesTarget = Profile, PolicyMatchesTarget = Policy });
        }
        public Task<bool> CompareExchangeAsync(CloudMaintenanceIntent expected, CloudMaintenanceIntent replacement, CancellationToken cancellationToken)
        {
            if (Current != expected) return Task.FromResult(false);
            Current = replacement;
            return Task.FromResult(true);
        }
        public Task<string?> ExecuteAsync(CloudMaintenanceIntent current, CloudMaintenanceAction action, CancellationToken cancellationToken)
        {
            switch (action)
            {
                case CloudMaintenanceAction.MoveSourceToBackup: State = "moved"; break;
                case CloudMaintenanceAction.Mount: State = "mounted"; return Task.FromResult<string?>("mount-instance");
                case CloudMaintenanceAction.Unmount: State = "unmounted"; break;
                case CloudMaintenanceAction.RestoreBackup: State = "local"; break;
                case CloudMaintenanceAction.ApplyConfiguration: Profile = true; Policy = true; ConfigurationEffects++; break;
                default: throw new InvalidOperationException();
            }
            return Task.FromResult<string?>(null);
        }
    }

    private sealed class FakeOperations(CloudMaintenanceIntent intent, CloudMaintenanceObservation observation) : ICloudMaintenanceOperations
    {
        public int Effects { get; private set; }
        public Task<CloudMaintenanceIntent?> ReadAsync(string jobId, CancellationToken cancellationToken) => Task.FromResult<CloudMaintenanceIntent?>(intent);
        public Task<CloudMaintenanceObservation> ObserveAsync(CloudMaintenanceIntent current, CancellationToken cancellationToken) => Task.FromResult(observation);
        public Task<bool> CompareExchangeAsync(CloudMaintenanceIntent expected, CloudMaintenanceIntent replacement, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<string?> ExecuteAsync(CloudMaintenanceIntent current, CloudMaintenanceAction action, CancellationToken cancellationToken)
        {
            Effects++;
            return Task.FromResult<string?>("verified-mount-id");
        }
    }

    private const string JobId = "fa72db0c54b74718af63bf4b60c0d7fb";
    private static CloudMaintenanceIntent Intent(CloudMaintenanceDirection direction, CloudMaintenancePhase phase)
        => new(JobId, direction, phase, "local-identity", direction == CloudMaintenanceDirection.Rollback ? "mount-identity" : null, new("11111111-1111-1111-1111-111111111111", "Music", "/music", "/backup", "profile", "remote", "path", new string('a', 64), "rclone-check-download-content", new string('b', 64)));

    private static CloudMaintenanceObservation Local() => Observation(CloudMaintenancePathState.Expected, CloudMaintenancePathState.Missing, CloudMaintenanceMountState.Missing);

    private static CloudMaintenanceObservation Observation(CloudMaintenancePathState source, CloudMaintenancePathState backup, CloudMaintenanceMountState mount)
        => new(true, true, source, backup, mount, false, false);
}
