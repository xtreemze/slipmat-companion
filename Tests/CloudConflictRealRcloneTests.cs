using System.Text;
using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

/// <summary>Exercises the actual rclone CLI against a private local backend; it does not test Jottacloud.</summary>
[Collection("Shared rclone recovery fence")]
public sealed class CloudConflictRealRcloneTests
{
    [Theory]
    [InlineData("album.nfo")]
    [InlineData("ALBUM.nfo")]
    public async Task LocalBackendPreservesOriginalsBeforeApplyingOnlyApprovedMetadataChanges(string remoteName)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture();
        if (remoteName != "album.nfo") File.Move(fixture.Active("album.nfo"), fixture.Active(remoteName));
        var runner = new LocalBackendRunner(fixture.RemoteRoot);
        var binding = await fixture.BindingAsync();
        await using var lease = await CloudConflictRecoveryFence.Shared.AcquireAsync(binding.JobId,
            CloudConflictRecoveryPlanner.BindingFingerprint(binding));
        var host = new RcloneCloudConflictRecoveryHost(runner, lease, fixture.Cache,
            CloudConflictRecoverySession.CompanionOnlyWriter, runner.Executable);
        var inventory = await host.CaptureAsync(binding, fixture.InventoryPath, default);
        var plan = CloudConflictRecoveryPlanner.Create(binding, inventory);
        Assert.Equal(2, plan.Items.Count);
        Assert.Equal(1, plan.DeferredLocalOnly);

        var state = CloudConflictRecoveryState.Create(plan);
        var complete = false;
        for (var step = 0; step < 32; step++)
        {
            var live = await host.ObserveAsync(plan, state, default);
            var decision = CloudConflictRecoveryTransaction.Decide(plan, state, live);
            if (decision.Action == CloudConflictAction.Complete) { complete = true; break; }
            Assert.NotEqual(CloudConflictAction.Blocked, decision.Action);
            var index = Assert.IsType<int>(decision.ItemIndex);
            var item = plan.Items[index];
            state = decision.Action switch
            {
                CloudConflictAction.PersistArchiveIntent => state.WithStage(index, CloudConflictItemStage.ArchiveIntent),
                CloudConflictAction.PersistArchiveReceipt => state.WithStage(index, CloudConflictItemStage.Archived, item.Remote.Sha256),
                CloudConflictAction.PersistMutationIntent => state.WithStage(index, CloudConflictItemStage.MutationIntent, item.Remote.Sha256),
                CloudConflictAction.PersistResolved => state.WithStage(index, CloudConflictItemStage.Resolved,
                    item.Remote.Sha256, live.Items[index].MutationReceipt),
                _ => state,
            };
            if (decision.Action == CloudConflictAction.Archive) await host.ArchiveAsync(plan, index, default);
            if (decision.Action == CloudConflictAction.Mutate) await host.MutateAsync(plan, index, default);
        }
        Assert.True(complete, "The real rclone command sequence did not complete.");

        Assert.Equal(fixture.RemoteAlbum, await File.ReadAllBytesAsync(fixture.Archived(remoteName)));
        Assert.Equal(fixture.LocalAlbum, await File.ReadAllBytesAsync(fixture.Active(remoteName)));
        Assert.Equal(fixture.RemotePng, await File.ReadAllBytesAsync(fixture.Archived("cover.png")));
        Assert.False(File.Exists(fixture.Active("cover.png")));
        Assert.Equal(fixture.Audio, await File.ReadAllBytesAsync(fixture.Active("track.flac")));
        Assert.False(File.Exists(fixture.Active("local-only.flac")));
        Assert.DoesNotContain(runner.Commands, args => args[0] is "sync" or "move" or "purge" or "delete");
        Assert.All(runner.ActualCommands, args => Assert.Contains("--config=/dev/null", args));
    }

    [Fact]
    public async Task LocalBackendResumesAfterLastDeletionWithVerifiedArchive()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture();
        File.Delete(Path.Combine(fixture.Source, "album.nfo"));
        File.Delete(Path.Combine(fixture.Source, "track.flac"));
        File.Delete(Path.Combine(fixture.Source, "local-only.flac"));
        File.Delete(fixture.Active("album.nfo"));
        File.Delete(fixture.Active("track.flac"));
        var runner = new LocalBackendRunner(fixture.RemoteRoot);
        var binding = await fixture.BindingAsync();
        await using var lease = await CloudConflictRecoveryFence.Shared.AcquireAsync(binding.JobId,
            CloudConflictRecoveryPlanner.BindingFingerprint(binding));
        RcloneCloudConflictRecoveryHost Host() => new(runner, lease, fixture.Cache,
            CloudConflictRecoverySession.CompanionOnlyWriter, runner.Executable);
        var host = Host();
        var plan = CloudConflictRecoveryPlanner.Create(binding, await host.CaptureAsync(binding, fixture.InventoryPath, default));
        var item = Assert.Single(plan.Items);
        var state = CloudConflictRecoveryState.Create(plan).WithStage(0, CloudConflictItemStage.ArchiveIntent);
        await host.ObserveAsync(plan, state, default);
        await host.ArchiveAsync(plan, 0, default);
        state = state.WithStage(0, CloudConflictItemStage.MutationIntent, item.Remote.Sha256);
        await host.ObserveAsync(plan, state, default);
        await host.MutateAsync(plan, 0, default);
        Assert.False(File.Exists(fixture.Active("cover.png")));
        Assert.Equal(fixture.RemotePng, await File.ReadAllBytesAsync(fixture.Archived("cover.png")));

        var live = await Host().ObserveAsync(plan, state, default);
        Assert.Equal(CloudConflictAction.PersistResolved, CloudConflictRecoveryTransaction.Decide(plan, state, live).Action);
        state = state.WithStage(0, CloudConflictItemStage.Resolved, item.Remote.Sha256, Assert.Single(live.Items).MutationReceipt);
        live = await Host().ObserveAsync(plan, state, default);
        Assert.Equal(CloudConflictAction.Complete, CloudConflictRecoveryTransaction.Decide(plan, state, live).Action);
    }

    [Fact]
    public async Task LocalBackendRejectsOccupiedArchiveWithoutChangingActiveObject()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture();
        var runner = new LocalBackendRunner(fixture.RemoteRoot);
        var binding = await fixture.BindingAsync();
        await using var lease = await CloudConflictRecoveryFence.Shared.AcquireAsync(binding.JobId,
            CloudConflictRecoveryPlanner.BindingFingerprint(binding));
        var host = new RcloneCloudConflictRecoveryHost(runner, lease, fixture.Cache,
            CloudConflictRecoverySession.CompanionOnlyWriter, runner.Executable);
        var plan = CloudConflictRecoveryPlanner.Create(binding,
            await host.CaptureAsync(binding, fixture.InventoryPath, default));
        var item = plan.Items.Single(candidate => candidate.RelativePath == "album.nfo");
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Archived("album.nfo"))!);
        await File.WriteAllTextAsync(fixture.Archived("album.nfo"), "wrong archive");
        var state = CloudConflictRecoveryState.Create(plan).WithStage(
            Array.IndexOf(plan.Items.ToArray(), item), CloudConflictItemStage.ArchiveIntent);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.ObserveAsync(plan, state, default));
        Assert.Equal("conflict-archive-target-occupied", error.Message);
        Assert.Equal(fixture.RemoteAlbum, await File.ReadAllBytesAsync(fixture.Active("album.nfo")));
        Assert.DoesNotContain(runner.Commands, args => args[0] == "deletefile"
            || args[0] == "copyto" && args[2] == "cloud:Music/album.nfo");
    }

    private sealed class LocalBackendRunner : IRcloneProcessRunner
    {
        private readonly string _remoteRoot;
        private readonly RcloneProcessRunner _actual = new();
        public string Executable { get; }
        public List<IReadOnlyList<string>> Commands { get; } = [];
        public List<IReadOnlyList<string>> ActualCommands { get; } = [];

        public LocalBackendRunner(string remoteRoot)
        {
            _remoteRoot = remoteRoot;
            Executable = Environment.GetEnvironmentVariable("SLIPMAT_TEST_RCLONE") ?? string.Empty;
            Assert.True(Path.IsPathFullyQualified(Executable) && File.Exists(Executable),
                "SLIPMAT_TEST_RCLONE must point to the installed test rclone binary.");
        }

        public Task<RcloneCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            Commands.Add(arguments.ToArray());
            if (arguments.SequenceEqual(["listremotes", "--long", "--json"]))
                return Task.FromResult(new RcloneCommandResult(true, 0,
                    "[{\"name\":\"cloud\",\"type\":\"jottacloud\"}]", "", false, false));
            // Explicit fake provider capability: actual local rclone remains case-sensitive.
            if (arguments.Count > 1 && arguments[0] == "backend" && arguments[1] == "features")
                return Task.FromResult(new RcloneCommandResult(true, 0,
                    "{\"Features\":{\"CaseInsensitive\":true}}", "", false, false));

            var translated = arguments.Select(Translate).Append("--config=/dev/null").ToArray();
            ActualCommands.Add(translated);
            return _actual.RunAsync(Executable, translated, timeout, cancellationToken);
        }

        private string Translate(string argument)
        {
            if (!argument.StartsWith("cloud:", StringComparison.Ordinal)) return argument;
            var relative = argument["cloud:".Length..];
            Assert.DoesNotContain("..", relative.Split('/'));
            Assert.DoesNotContain('\\', relative);
            var path = Path.GetFullPath(Path.Combine(_remoteRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            Assert.StartsWith(_remoteRoot + Path.DirectorySeparatorChar, path, StringComparison.Ordinal);
            return path;
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "conflict-real-rclone-" + Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(_root, "source");
        public string RemoteRoot => Path.Combine(_root, "remote");
        public string Cache => Path.Combine(_root, "cache");
        public string InventoryPath => Path.Combine(_root, "journal", "inventory.json");
        public byte[] LocalAlbum { get; } = Encoding.UTF8.GetBytes("<album><title>Local</title></album>");
        public byte[] RemoteAlbum { get; } = Encoding.UTF8.GetBytes("<album><title>Old</title></album>");
        public byte[] RemotePng { get; } = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGMQ03ECAADgAIWWkiPwAAAAAElFTkSuQmCC");
        public byte[] Audio { get; } = [1, 2, 3, 4, 5, 6];

        public Fixture()
        {
            Directory.CreateDirectory(Source);
            Directory.CreateDirectory(RemoteRoot);
            Directory.CreateDirectory(Cache);
            Directory.CreateDirectory(Path.Combine(RemoteRoot, "Music"));
            File.WriteAllBytes(Path.Combine(Source, "album.nfo"), LocalAlbum);
            File.WriteAllBytes(Path.Combine(Source, "track.flac"), Audio);
            File.WriteAllBytes(Path.Combine(Source, "local-only.flac"), [7, 8, 9]);
            File.WriteAllBytes(Active("album.nfo"), RemoteAlbum);
            File.WriteAllBytes(Active("cover.png"), RemotePng);
            File.WriteAllBytes(Active("track.flac"), Audio);
        }

        public string Active(string relative) => Path.Combine(RemoteRoot, "Music", relative);
        public string Archived(string relative) => Path.Combine(RemoteRoot, "Archive", "attempt", relative);
        public async Task<CloudConflictRecoveryBinding> BindingAsync()
        {
            var source = await CloudMigrationManifestBuilder.CaptureAsync(Source);
            return new(Guid.NewGuid().ToString("N"), "profile", "Music", Source,
                "cloud", "Music", "Archive/attempt", source.DigestSha256, Guid.NewGuid().ToString("N"));
        }
        public void Dispose() => Directory.Delete(_root, true);
    }
}
