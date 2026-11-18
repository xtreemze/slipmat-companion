using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public sealed class RcloneCloudConflictRecoveryHostTests
{
    [Fact]
    public async Task EmptyRemoteFailsClosedWhenProviderProbeCannotBeSelfChecked()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture();
        await File.WriteAllBytesAsync(Path.Combine(fixture.Source, "track.flac"), [1, 2, 3, 4]);
        var runner = new FakeRunner(args => args[0] switch
        {
            "listremotes" => Success("[{\"name\":\"cloud\",\"type\":\"jottacloud\"}]"),
            "backend" => Success("{\"Features\":{\"CaseInsensitive\":true}}"),
            "lsjson" => Success("[]"),
            _ => throw new Xunit.Sdk.XunitException("Unexpected rclone command"),
        });
        var binding = await fixture.BindingAsync();
        var fence = new CloudConflictRecoveryFence();
        await using var lease = await fence.AcquireAsync(binding.JobId, CloudConflictRecoveryPlanner.BindingFingerprint(binding));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Host(runner, lease).CaptureAsync(binding, fixture.InventoryPath, default));
        Assert.Equal("conflict-provider-probe-selfcheck-failed", error.Message);
    }

    [Fact]
    public async Task CaptureMatchesUniqueAsciiCaseAliasOnlyAfterFeatureCheckAndFullHashes()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture();
        var bytes = Encoding.UTF8.GetBytes("same audio");
        await File.WriteAllBytesAsync(Path.Combine(fixture.Source, "track.flac"), bytes);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var runner = new FakeRunner(args => args[0] switch
        {
            "listremotes" => Success("[{\"name\":\"cloud\",\"type\":\"jottacloud\"}]"),
            "backend" => Success("{\"Features\":{\"CaseInsensitive\":true}}"),
            "lsjson" => Success("[{\"Name\":\"Track.flac\",\"Path\":\"Track.flac\",\"IsDir\":false,\"Size\":10}]"),
            "hashsum" => Success(sha + "  Track.flac\n"),
            _ => throw new Xunit.Sdk.XunitException("Unexpected rclone command: " + args[0]),
        });
        var binding = await fixture.BindingAsync();
        var fence = new CloudConflictRecoveryFence();
        await using var lease = await fence.AcquireAsync(binding.JobId, CloudConflictRecoveryPlanner.BindingFingerprint(binding));
        var inventory = await fixture.Host(runner, lease).CaptureAsync(binding, fixture.InventoryPath, default);
        var item = Assert.Single(inventory.Entries);
        Assert.Equal("track.flac", item.RelativePath);
        Assert.Equal("Track.flac", item.RemoteRelativePath);
        Assert.Equal(item.Local!.Sha256, item.Remote!.Sha256);
        Assert.Empty(CloudConflictRecoveryPlanner.Create(binding, inventory).Items);
        Assert.Contains(runner.Commands, args => args[0] == "backend" && args[1] == "features");
        Assert.Contains(runner.Commands, args => args[0] == "hashsum" && args.Contains("--download"));
    }

    [Theory]
    [InlineData("{\"Features\":{\"CaseInsensitive\":false}}")]
    [InlineData("{\"Features\":{\"CaseInsensitive\":\"true\"}}")]
    [InlineData("{\"Features\":{}}")]
    public async Task CaptureRejectsUnverifiedProviderFeatureBeforeListingOrHashing(string features)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.Source, "track.flac"), "payload");
        var runner = new FakeRunner(args => args[0] switch
        {
            "listremotes" => Success("[{\"name\":\"cloud\",\"type\":\"jottacloud\"}]"),
            "backend" => Success(features),
            _ => throw new Xunit.Sdk.XunitException("Unexpected command: " + args[0]),
        });
        var binding = await fixture.BindingAsync();
        var fence = new CloudConflictRecoveryFence();
        await using var lease = await fence.AcquireAsync(binding.JobId, CloudConflictRecoveryPlanner.BindingFingerprint(binding));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Host(runner, lease)
            .CaptureAsync(binding, fixture.InventoryPath, default));
        Assert.Equal("conflict-provider-case-unsupported", error.Message);
        Assert.DoesNotContain(runner.Commands, args => args[0] is "lsjson" or "hashsum" or "copyto");
    }

    [Fact]
    public async Task CaptureRejectsAudioDifferenceBeforeAnyProviderWrite()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture();
        var payload = Encoding.UTF8.GetBytes("remote audio");
        var sha = Convert.ToHexStringLower(SHA256.HashData(payload));
        var runner = new FakeRunner(args => args[0] switch
        {
            "listremotes" => Success("[{\"name\":\"cloud\",\"type\":\"jottacloud\"}]"),
            "backend" => Success("{\"Features\":{\"CaseInsensitive\":true}}"),
            "lsjson" => Success("[{\"Name\":\"track.flac\",\"Path\":\"track.flac\",\"IsDir\":false,\"Size\":12}]"),
            "hashsum" => Success(sha + "  track.flac\n"),
            _ => throw new Xunit.Sdk.XunitException("Unexpected rclone command: " + args[0]),
        });
        var binding = await fixture.BindingAsync();
        var fence = new CloudConflictRecoveryFence();
        await using var lease = await fence.AcquireAsync(binding.JobId, CloudConflictRecoveryPlanner.BindingFingerprint(binding));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Host(runner, lease)
            .CaptureAsync(binding, fixture.InventoryPath, default));
        Assert.Equal("conflict-unsupported-content-difference", error.Message);
        Assert.False(File.Exists(fixture.InventoryPath));
        Assert.DoesNotContain(runner.Commands, args => args[0] is "copyto" or "deletefile" or "mkdir");
    }

    [Theory]
    [InlineData("[{\"Name\":\"Cover.jpg\",\"Path\":\"Cover.jpg\",\"IsDir\":false,\"Size\":1},{\"Name\":\"cover.jpg\",\"Path\":\"cover.jpg\",\"IsDir\":false,\"Size\":1}]")]
    [InlineData("[{\"Name\":\"../escape.jpg\",\"Path\":\"../escape.jpg\",\"IsDir\":false,\"Size\":1}]")]
    public async Task CaptureRejectsUnsafeRemoteNamespace(string listing)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture();
        var runner = new FakeRunner(args => args[0] switch
        {
            "listremotes" => Success("[{\"name\":\"cloud\",\"type\":\"jottacloud\"}]"),
            "backend" => Success("{\"Features\":{\"CaseInsensitive\":true}}"),
            "lsjson" => Success(listing),
            _ => throw new Xunit.Sdk.XunitException("Unexpected rclone command: " + args[0]),
        });
        var binding = await fixture.BindingAsync();
        var fence = new CloudConflictRecoveryFence();
        await using var lease = await fence.AcquireAsync(binding.JobId, CloudConflictRecoveryPlanner.BindingFingerprint(binding));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Host(runner, lease)
            .CaptureAsync(binding, fixture.InventoryPath, default));
        Assert.False(File.Exists(fixture.InventoryPath));
    }

    [Fact]
    public async Task CaptureRejectsTruncatedListing()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture();
        var runner = new FakeRunner(args => args[0] switch
        {
            "listremotes" => Success("[{\"name\":\"cloud\",\"type\":\"jottacloud\"}]"),
            "backend" => Success("{\"Features\":{\"CaseInsensitive\":true}}"),
            "lsjson" => Success("[]") with { OutputTruncated = true },
            _ => throw new Xunit.Sdk.XunitException("Unexpected rclone command: " + args[0]),
        });
        var binding = await fixture.BindingAsync();
        var fence = new CloudConflictRecoveryFence();
        await using var lease = await fence.AcquireAsync(binding.JobId, CloudConflictRecoveryPlanner.BindingFingerprint(binding));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Host(runner, lease)
            .CaptureAsync(binding, fixture.InventoryPath, default));
        Assert.Equal("conflict-remote-list-failed", error.Message);
        Assert.False(File.Exists(fixture.InventoryPath));
    }

    [Theory]
    [InlineData("album.nfo")]
    [InlineData("ALBUM.nfo")]
    public async Task ArchiveProofPrecedesActiveMutationAndReplayProducesReceipt(string remoteName)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture();
        var local = Encoding.UTF8.GetBytes("<album><title>Local</title></album>");
        var remote = Encoding.UTF8.GetBytes("<album><title>Remote</title></album>");
        await File.WriteAllBytesAsync(Path.Combine(fixture.Source, "album.nfo"), local);
        var runner = new FakeCloudRunner();
        runner.Files.Add("cloud:Music/" + remoteName, remote);
        var binding = await fixture.BindingAsync();
        var fence = new CloudConflictRecoveryFence();
        await using var lease = await fence.AcquireAsync(binding.JobId, CloudConflictRecoveryPlanner.BindingFingerprint(binding));
        var host = fixture.Host(runner, lease);
        var progress = new List<CloudConflictPreflightProgress>();
        host.ReportProgress = (sample, _) => { progress.Add(sample); return Task.CompletedTask; };
        var inventory = await host.CaptureAsync(binding, fixture.InventoryPath, default);
        var plan = CloudConflictRecoveryPlanner.Create(binding, inventory);
        var item = Assert.Single(plan.Items);
        Assert.Equal(CloudConflictOperation.ReplaceWithLocal, item.Operation);
        Assert.Contains(progress, sample => sample.Phase == "hashing-local" && sample.Files == 1
            && sample.TotalFiles == 1 && sample.BytesRead == local.Length);
        Assert.Contains(progress, sample => sample.Phase == "hashing-cloud" && sample.Files == 1
            && sample.TotalFiles == 1 && sample.BytesRead == remote.Length);
        Assert.Contains(progress, sample => sample.Phase == "verifying-artwork" && sample.Files == 2
            && sample.TotalFiles == 2 && sample.BytesRead == local.Length + remote.Length);

        var state = CloudConflictRecoveryState.Create(plan);
        var observation = await host.ObserveAsync(plan, state, default);
        Assert.Equal(CloudConflictAction.PersistArchiveIntent,
            CloudConflictRecoveryTransaction.Decide(plan, state, observation).Action);
        state = state.WithStage(0, CloudConflictItemStage.ArchiveIntent);
        observation = await host.ObserveAsync(plan, state, default);
        Assert.Equal(CloudConflictAction.Archive, CloudConflictRecoveryTransaction.Decide(plan, state, observation).Action);
        await host.ArchiveAsync(plan, 0, default);
        Assert.Equal(remote, runner.Files["cloud:" + item.ArchivePath]);
        Assert.Equal(remote, runner.Files["cloud:Music/" + remoteName]);

        observation = await host.ObserveAsync(plan, state, default);
        Assert.Equal(CloudConflictAction.PersistArchiveReceipt,
            CloudConflictRecoveryTransaction.Decide(plan, state, observation).Action);
        state = state.WithStage(0, CloudConflictItemStage.Archived, item.Remote.Sha256);
        observation = await host.ObserveAsync(plan, state, default);
        Assert.Equal(CloudConflictAction.PersistMutationIntent,
            CloudConflictRecoveryTransaction.Decide(plan, state, observation).Action);
        state = state.WithStage(0, CloudConflictItemStage.MutationIntent, item.Remote.Sha256);
        observation = await host.ObserveAsync(plan, state, default);
        Assert.Equal(CloudConflictAction.Mutate, CloudConflictRecoveryTransaction.Decide(plan, state, observation).Action);
        await host.MutateAsync(plan, 0, default);
        Assert.Equal(local, runner.Files["cloud:Music/" + remoteName]);
        Assert.Equal(remote, runner.Files["cloud:" + item.ArchivePath]);

        observation = await host.ObserveAsync(plan, state, default);
        Assert.Equal(CloudConflictAction.PersistResolved,
            CloudConflictRecoveryTransaction.Decide(plan, state, observation).Action);
        Assert.Equal(item.Local!.Sha256, Assert.Single(observation.Items).MutationReceipt?.TargetSha256);
        var resumedHost = fixture.Host(runner, lease);
        var resumedObservation = await resumedHost.ObserveAsync(plan, state, default);
        Assert.Equal(CloudConflictAction.PersistResolved,
            CloudConflictRecoveryTransaction.Decide(plan, state, resumedObservation).Action);
        var commands = runner.Commands;
        var archiveWrite = commands.FindIndex(args => args[0] == "copyto" && args[2] == "cloud:" + item.ArchivePath);
        var activeWrite = commands.FindIndex(args => args[0] == "copyto" && args[2] == "cloud:Music/" + remoteName);
        Assert.True(archiveWrite >= 0 && activeWrite > archiveWrite);
        Assert.Contains("--immutable", commands[archiveWrite]);
        Assert.Contains("--jottacloud-no-versions=false", commands[activeWrite]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderResolvedUnicodeAliasBlocksBeforePayloadHashes(bool exactNonAsciiPair)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.Source, "SS.nfo"), "<album />");
        if (exactNonAsciiPair) await File.WriteAllTextAsync(Path.Combine(fixture.Source, "ß.nfo"), "<album />");
        var runner = new FakeRunner(args => args[0] switch
        {
            "listremotes" => Success("[{\"name\":\"cloud\",\"type\":\"jottacloud\"}]"),
            "backend" => Success("{\"Features\":{\"CaseInsensitive\":true}}"),
            "lsjson" when args.Contains("--stat") => Success("{\"Name\":\"SS.nfo\",\"IsDir\":false,\"Size\":1}"),
            "lsjson" => Success("[{\"Name\":\"ß.nfo\",\"Path\":\"ß.nfo\",\"IsDir\":false,\"Size\":1}]"),
            _ => throw new Xunit.Sdk.XunitException("Payload reading must not begin."),
        });
        var binding = await fixture.BindingAsync();
        var fence = new CloudConflictRecoveryFence();
        await using var lease = await fence.AcquireAsync(binding.JobId, CloudConflictRecoveryPlanner.BindingFingerprint(binding));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Host(runner, lease)
            .CaptureAsync(binding, fixture.InventoryPath, default));
        Assert.Equal("conflict-provider-alias-unsupported", error.Message);
        Assert.DoesNotContain(runner.Commands, args => args[0] == "hashsum");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task UnmatchedLocalProbeNeverTreatsErrorsAsAbsence(int failure)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.Source, "deferred.nfo"), "<album />");
        var runner = new FakeCloudRunner();
        runner.Files.Add("cloud:Music/anchor.flac", [1]);
        runner.ProbeOverride = args => args[0] != "lsjson" || !args[1].EndsWith("/deferred.nfo", StringComparison.Ordinal) ? null
            : failure switch
            {
                0 => new RcloneCommandResult(true, 1, "", "authentication failed", false, false),
                1 => Success("not-json"),
                2 => Success("null") with { OutputTruncated = true },
                3 when args.Contains("--stat") => Success("null"),
                3 => new RcloneCommandResult(true, 3, "[", "not-json", false, false),
                _ => new RcloneCommandResult(true, null, "", "", true, false),
            };
        var binding = await fixture.BindingAsync();
        var fence = new CloudConflictRecoveryFence();
        await using var lease = await fence.AcquireAsync(binding.JobId, CloudConflictRecoveryPlanner.BindingFingerprint(binding));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Host(runner, lease)
            .CaptureAsync(binding, fixture.InventoryPath, default));
        Assert.Equal("conflict-provider-alias-probe-failed", error.Message);
        Assert.DoesNotContain(runner.Commands, args => args[0] == "hashsum");
    }

    [Fact]
    public async Task NewEmptyDirectoryUnicodeAliasBlocksOnResume()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.Source, "album.nfo"), "<album>Local</album>");
        Directory.CreateDirectory(Path.Combine(fixture.Source, "ß"));
        await File.WriteAllBytesAsync(Path.Combine(fixture.Source, "ß", "track.flac"), [1]);
        var runner = new FakeCloudRunner();
        runner.Files.Add("cloud:Music/album.nfo", Encoding.UTF8.GetBytes("<album>Remote</album>"));
        runner.Files.Add("cloud:Music/ß/track.flac", [1]);
        var binding = await fixture.BindingAsync();
        var fence = new CloudConflictRecoveryFence();
        await using var lease = await fence.AcquireAsync(binding.JobId, CloudConflictRecoveryPlanner.BindingFingerprint(binding));
        var host = fixture.Host(runner, lease);
        var plan = CloudConflictRecoveryPlanner.Create(binding, await host.CaptureAsync(binding, fixture.InventoryPath, default));
        Directory.CreateDirectory(Path.Combine(fixture.Source, "SS"));
        runner.ProbeOverride = args => args[0] != "lsjson" || !args[1].EndsWith("/SS", StringComparison.Ordinal) ? null
            : args.Contains("--stat") ? Success("null") : Success("[]");
        var resumed = fixture.Host(runner, lease);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => resumed.ObserveAsync(plan, CloudConflictRecoveryState.Create(plan), default));
        Assert.Equal("conflict-provider-alias-unsupported", error.Message);
        Assert.DoesNotContain(runner.Commands, args => args[0] is "deletefile" or "mkdir"
            || args[0] == "copyto" && args[2].StartsWith("cloud:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RemoteSpellingDriftBlocksBeforeArchive()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.Source, "album.nfo"), "<album>Local</album>");
        var runner = new FakeCloudRunner();
        var old = Encoding.UTF8.GetBytes("<album>Remote</album>");
        runner.Files.Add("cloud:Music/ALBUM.nfo", old);
        var binding = await fixture.BindingAsync();
        var fence = new CloudConflictRecoveryFence();
        await using var lease = await fence.AcquireAsync(binding.JobId, CloudConflictRecoveryPlanner.BindingFingerprint(binding));
        var host = fixture.Host(runner, lease);
        var plan = CloudConflictRecoveryPlanner.Create(binding, await host.CaptureAsync(binding, fixture.InventoryPath, default));
        runner.Files.Remove("cloud:Music/ALBUM.nfo");
        runner.Files.Add("cloud:Music/Album.nfo", old);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.ObserveAsync(plan, CloudConflictRecoveryState.Create(plan), default));
        Assert.Equal("conflict-remote-spelling-changed", error.Message);
        Assert.DoesNotContain(runner.Commands, args => args[0] == "copyto" && args[2].StartsWith("cloud:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RemoteOnlyArtworkIsBlockedWhenLocalDirectoryAppearsBeforeResume()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture();
        var runner = new FakeCloudRunner();
        runner.Files.Add("cloud:Music/cover.png", Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGMQ03ECAADgAIWWkiPwAAAAAElFTkSuQmCC"));
        var binding = await fixture.BindingAsync();
        var fence = new CloudConflictRecoveryFence();
        await using var lease = await fence.AcquireAsync(binding.JobId, CloudConflictRecoveryPlanner.BindingFingerprint(binding));
        var host = fixture.Host(runner, lease);
        var plan = CloudConflictRecoveryPlanner.Create(binding, await host.CaptureAsync(binding, fixture.InventoryPath, default));
        Directory.CreateDirectory(Path.Combine(fixture.Source, "COVER.png"));
        var resumed = fixture.Host(runner, lease);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => resumed.ObserveAsync(plan, CloudConflictRecoveryState.Create(plan), default));
        Assert.Equal("conflict-path-collision", error.Message);
        Assert.DoesNotContain(runner.Commands, args => args[0] is "deletefile" or "mkdir"
            || args[0] == "copyto" && args[2].StartsWith("cloud:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(CloudConflictItemStage.MutationIntent, CloudConflictAction.PersistResolved)]
    [InlineData(CloudConflictItemStage.Resolved, CloudConflictAction.Complete)]
    public async Task ResumeAfterLastRemoteDeletionUsesPreservedArchiveForProviderSelfCheck(
        CloudConflictItemStage stage, CloudConflictAction expected)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture();
        var runner = new FakeCloudRunner();
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGMQ03ECAADgAIWWkiPwAAAAAElFTkSuQmCC");
        runner.Files.Add("cloud:Music/cover.png", png);
        var binding = await fixture.BindingAsync();
        var fence = new CloudConflictRecoveryFence();
        await using var lease = await fence.AcquireAsync(binding.JobId, CloudConflictRecoveryPlanner.BindingFingerprint(binding));
        var host = fixture.Host(runner, lease);
        var plan = CloudConflictRecoveryPlanner.Create(binding, await host.CaptureAsync(binding, fixture.InventoryPath, default));
        var item = Assert.Single(plan.Items);
        // Crash after delete and archive verification but before the next journal write.
        runner.Files.Add("cloud:" + item.ArchivePath, png);
        runner.Files.Remove("cloud:Music/cover.png");
        var state = CloudConflictRecoveryState.Create(plan).WithStage(0, CloudConflictItemStage.MutationIntent, item.Remote.Sha256);
        var resumed = fixture.Host(runner, lease);
        var observation = await resumed.ObserveAsync(plan, state, default);
        if (stage == CloudConflictItemStage.Resolved)
        {
            state = state.WithStage(0, stage, item.Remote.Sha256, Assert.Single(observation.Items).MutationReceipt);
            resumed = fixture.Host(runner, lease);
            observation = await resumed.ObserveAsync(plan, state, default);
        }
        Assert.Equal(expected, CloudConflictRecoveryTransaction.Decide(plan, state, observation).Action);
        Assert.Equal(item.Remote.Sha256, Assert.Single(observation.Items).ArchiveSha256);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyActiveResumeRejectsMissingOrChangedArchive(bool wrongArchive)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture();
        var runner = new FakeCloudRunner();
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGMQ03ECAADgAIWWkiPwAAAAAElFTkSuQmCC");
        runner.Files.Add("cloud:Music/cover.png", png);
        var binding = await fixture.BindingAsync();
        var fence = new CloudConflictRecoveryFence();
        await using var lease = await fence.AcquireAsync(binding.JobId, CloudConflictRecoveryPlanner.BindingFingerprint(binding));
        var plan = CloudConflictRecoveryPlanner.Create(binding, await fixture.Host(runner, lease).CaptureAsync(binding, fixture.InventoryPath, default));
        var item = Assert.Single(plan.Items);
        runner.Files.Remove("cloud:Music/cover.png");
        if (wrongArchive) runner.Files.Add("cloud:" + item.ArchivePath, [1, 2, 3]);
        var state = CloudConflictRecoveryState.Create(plan).WithStage(0, CloudConflictItemStage.MutationIntent, item.Remote.Sha256);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Host(runner, lease).ObserveAsync(plan, state, default));
        Assert.Equal(wrongArchive ? "conflict-archive-verification-failed" : "conflict-provider-probe-selfcheck-failed", error.Message);
    }

    [Fact]
    public async Task ExistingWrongArchiveBlocksBeforeActiveMutation()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.Source, "album.nfo"), "<album><title>Local</title></album>");
        var original = Encoding.UTF8.GetBytes("<album><title>Remote</title></album>");
        var runner = new FakeCloudRunner();
        runner.Files.Add("cloud:Music/album.nfo", original);
        var binding = await fixture.BindingAsync();
        var fence = new CloudConflictRecoveryFence();
        await using var lease = await fence.AcquireAsync(binding.JobId, CloudConflictRecoveryPlanner.BindingFingerprint(binding));
        var host = fixture.Host(runner, lease);
        var plan = CloudConflictRecoveryPlanner.Create(binding,
            await host.CaptureAsync(binding, fixture.InventoryPath, default));
        var item = Assert.Single(plan.Items);
        runner.Files.Add("cloud:" + item.ArchivePath, Encoding.UTF8.GetBytes("wrong archive"));

        var state = CloudConflictRecoveryState.Create(plan).WithStage(0, CloudConflictItemStage.ArchiveIntent);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.ObserveAsync(plan, state, default));
        Assert.Equal("conflict-archive-target-occupied", error.Message);
        Assert.Equal(original, runner.Files["cloud:Music/album.nfo"]);
        Assert.DoesNotContain(runner.Commands, args => args[0] == "deletefile"
            || args[0] == "copyto" && args[2] == "cloud:Music/album.nfo");
    }

    [Fact]
    public async Task MultiItemTransactionDoesNotReprobeWholePlanAtEveryJournalTransition()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture();
        const int itemCount = 8;
        var runner = new FakeCloudRunner();
        for (var i = 0; i < itemCount; i++)
        {
            var name = $"album-{i:D2}.nfo";
            await File.WriteAllTextAsync(Path.Combine(fixture.Source, name), $"<album><title>Local {i}</title></album>");
            runner.Files.Add("cloud:Music/" + name, Encoding.UTF8.GetBytes($"<album><title>Remote {i}</title></album>"));
        }
        var binding = await fixture.BindingAsync();
        var fence = new CloudConflictRecoveryFence();
        await using var lease = await fence.AcquireAsync(binding.JobId, CloudConflictRecoveryPlanner.BindingFingerprint(binding));
        var host = fixture.Host(runner, lease);
        var plan = CloudConflictRecoveryPlanner.Create(binding,
            await host.CaptureAsync(binding, fixture.InventoryPath, default));
        Assert.Equal(itemCount, plan.Items.Count);
        var state = CloudConflictRecoveryState.Create(plan);
        var completed = false;
        for (var step = 0; step < itemCount * 8 + 8; step++)
        {
            var observation = await host.ObserveAsync(plan, state, default);
            var decision = CloudConflictRecoveryTransaction.Decide(plan, state, observation);
            if (decision.Action == CloudConflictAction.Complete) { completed = true; break; }
            Assert.NotEqual(CloudConflictAction.Blocked, decision.Action);
            var index = Assert.IsType<int>(decision.ItemIndex);
            var item = plan.Items[index];
            switch (decision.Action)
            {
                case CloudConflictAction.PersistArchiveIntent:
                    state = state.WithStage(index, CloudConflictItemStage.ArchiveIntent); break;
                case CloudConflictAction.Archive:
                    await host.ArchiveAsync(plan, index, default); break;
                case CloudConflictAction.PersistArchiveReceipt:
                    state = state.WithStage(index, CloudConflictItemStage.Archived, item.Remote.Sha256); break;
                case CloudConflictAction.PersistMutationIntent:
                    state = state.WithStage(index, CloudConflictItemStage.MutationIntent, item.Remote.Sha256); break;
                case CloudConflictAction.Mutate:
                    await host.MutateAsync(plan, index, default); break;
                case CloudConflictAction.PersistResolved:
                    state = state.WithStage(index, CloudConflictItemStage.Resolved, item.Remote.Sha256,
                        observation.Items[index].MutationReceipt); break;
                default: throw new Xunit.Sdk.XunitException("Unexpected decision: " + decision.Action);
            }
        }
        Assert.True(completed);
        Assert.True(runner.Commands.Count(args => args[0] == "hashsum") < itemCount * 12,
            "Each journal transition must not rehash every planned cloud object.");
        Assert.True(runner.Commands.Count(args => args[0] == "lsjson") < itemCount * 16,
            "Each journal transition must not relist every planned cloud object.");
        Assert.True(runner.Commands.Count(args => args[0] == "listremotes") <= 2);
    }

    private static RcloneCommandResult Success(string output) => new(true, 0, output, "", false, false);
    private static RcloneCommandResult MissingDirectory() => new(true, 3, "[\n",
        "{\"level\":\"error\",\"msg\":\"error listing: directory not found\"}\n" +
        "{\"level\":\"notice\",\"msg\":\"Failed to lsjson with 2 errors: last error was: error in ListJSON: directory not found\"}\n", false, false);

    private sealed class FakeRunner(Func<IReadOnlyList<string>, RcloneCommandResult> respond) : IRcloneProcessRunner
    {
        public List<IReadOnlyList<string>> Commands { get; } = [];
        private string? _rootListing;
        public Task<RcloneCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(arguments.ToArray());
            if (arguments[0] == "version") return Task.FromResult(Success("rclone v1.74.4\n"));
            if (arguments[0] == "lsjson" && arguments[1].Contains(".slipmat-probe-", StringComparison.Ordinal))
                return Task.FromResult(arguments.Contains("--stat") ? Success("null") : MissingDirectory());
            if (arguments[0] == "lsjson" && arguments.Contains("--stat") && _rootListing is not null)
            {
                var leaf = arguments[1][(arguments[1].LastIndexOf('/') + 1)..];
                using var doc = System.Text.Json.JsonDocument.Parse(_rootListing);
                var found = doc.RootElement.EnumerateArray().FirstOrDefault(value => value.TryGetProperty("Name", out var name) && name.GetString() == leaf);
                if (found.ValueKind == System.Text.Json.JsonValueKind.Object) return Task.FromResult(Success(found.GetRawText()));
            }
            var result = respond(arguments);
            if (arguments[0] == "lsjson" && !arguments.Contains("--stat") && !arguments.Contains("--max-depth"))
                _rootListing = result.StandardOutput;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeCloudRunner : IRcloneProcessRunner
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public Func<IReadOnlyList<string>, RcloneCommandResult?>? ProbeOverride { get; set; }
        public List<IReadOnlyList<string>> Commands { get; } = [];

        public Task<RcloneCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(arguments.ToArray());
            var overridden = ProbeOverride?.Invoke(arguments);
            if (overridden is not null) return Task.FromResult(overridden);
            var result = arguments[0] switch
            {
                "listremotes" => Success("[{\"name\":\"cloud\",\"type\":\"jottacloud\"}]"),
                "version" => Success("rclone v1.74.4\n"),
                "backend" => Success("{\"Features\":{\"CaseInsensitive\":true}}"),
                "lsjson" when arguments.Contains("--stat") => Stat(arguments[1]),
                "lsjson" when arguments.Contains("--max-depth") => Files.Keys.Any(key => key.StartsWith(arguments[1].TrimEnd('/') + "/", StringComparison.Ordinal)) ? Success(Listing(arguments[1])) : MissingDirectory(),
                "lsjson" => Success(Listing(arguments[1])),
                "hashsum" => Hashsum(arguments),
                "mkdir" => Success(""),
                "copyto" => Copy(arguments),
                _ => throw new Xunit.Sdk.XunitException("Unexpected rclone command: " + arguments[0]),
            };
            return Task.FromResult(result);
        }

        private RcloneCommandResult Stat(string path)
        {
            if (!Files.TryGetValue(path, out var payload)) return Success("null");
            return Success(System.Text.Json.JsonSerializer.Serialize(new { Name = path[(path.LastIndexOf('/') + 1)..], IsDir = false, Size = payload.Length }));
        }

        private string Listing(string directory)
        {
            var entries = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var pair in Files)
            {
                if (!pair.Key.StartsWith(directory.TrimEnd('/') + "/", StringComparison.Ordinal)) continue;
                var rest = pair.Key[(directory.TrimEnd('/').Length + 1)..];
                var slash = rest.IndexOf('/');
                var name = slash < 0 ? rest : rest[..slash];
                entries[name] = slash < 0
                    ? new { Name = name, Path = name, IsDir = false, Size = pair.Value.LongLength }
                    : new { Name = name, Path = name, IsDir = true, Size = 0L };
            }
            return System.Text.Json.JsonSerializer.Serialize(entries.Values.ToArray());
        }

        private RcloneCommandResult Hashsum(IReadOnlyList<string> arguments)
        {
            var spec = arguments[2];
            if (!arguments.Contains("--max-depth"))
            {
                if (!Files.TryGetValue(spec, out var payload)) return new(true, 1, "", "missing", false, false);
                var digest = Convert.ToHexStringLower(SHA256.HashData(payload));
                return Success(digest + "  " + spec[(spec.LastIndexOf('/') + 1)..] + "\n");
            }
            Assert.Equal("1", arguments[Array.IndexOf(arguments.ToArray(), "--max-depth") + 1]);
            var rows = Files.Where(pair => pair.Key.StartsWith(spec.TrimEnd('/') + "/", StringComparison.Ordinal))
                .Select(pair => (Name: pair.Key[(spec.TrimEnd('/').Length + 1)..], pair.Value))
                .Where(pair => !pair.Name.Contains('/'))
                .OrderBy(pair => pair.Name, StringComparer.Ordinal)
                .Select(pair => Convert.ToHexStringLower(SHA256.HashData(pair.Value)) + "  " + pair.Name + "\n");
            return Success(string.Concat(rows));
        }

        private RcloneCommandResult Copy(IReadOnlyList<string> args)
        {
            var source = args[1].StartsWith("cloud:", StringComparison.Ordinal)
                ? Files[args[1]] : File.ReadAllBytes(args[1]);
            if (args[2].StartsWith("cloud:", StringComparison.Ordinal))
            {
                if (args.Contains("--immutable") && Files.ContainsKey(args[2]))
                    return new(true, 1, "", "occupied", false, false);
                Files[args[2]] = source.ToArray();
            }
            else File.WriteAllBytes(args[2], source);
            return Success("");
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "conflict-host-" + Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(_root, "source");
        public string Cache => Path.Combine(_root, "cache");
        public string InventoryPath => Path.Combine(_root, "journal", "inventory.json");
        public Fixture()
        {
            Directory.CreateDirectory(Source);
            Directory.CreateDirectory(Cache);
        }
        public async Task<CloudConflictRecoveryBinding> BindingAsync()
        {
            var manifest = await CloudMigrationManifestBuilder.CaptureAsync(Source);
            return new(Guid.NewGuid().ToString("N"), "profile", "Music", Source,
                "cloud", "Music", "Slipmat-backup/repair", manifest.DigestSha256, Guid.NewGuid().ToString("N"));
        }
        public RcloneCloudConflictRecoveryHost Host(IRcloneProcessRunner runner, CloudConflictRecoveryFence.RecoveryLease lease)
            => new(runner, lease, Cache, CloudConflictRecoverySession.CompanionOnlyWriter);
        public void Dispose() => Directory.Delete(_root, true);
    }
}
