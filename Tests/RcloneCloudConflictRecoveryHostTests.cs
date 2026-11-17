using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public sealed class RcloneCloudConflictRecoveryHostTests
{
    [Fact]
    public async Task CaptureDefersLocalOnlyAudioAndWritesBoundInventory()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture();
        await File.WriteAllBytesAsync(Path.Combine(fixture.Source, "track.flac"), [1, 2, 3, 4]);
        var runner = new FakeRunner((args) => args[0] switch
        {
            "listremotes" => Success("[{\"name\":\"cloud\",\"type\":\"jottacloud\"}]"),
            "lsjson" => Success("[]"),
            _ => throw new Xunit.Sdk.XunitException("Unexpected rclone command: " + args[0]),
        });
        var binding = await fixture.BindingAsync();
        var fence = new CloudConflictRecoveryFence();
        await using var lease = await fence.AcquireAsync(binding.JobId, CloudConflictRecoveryPlanner.BindingFingerprint(binding));
        var host = fixture.Host(runner, lease);
        var progress = new List<CloudConflictPreflightProgress>();
        host.ReportProgress = (sample, _) => { progress.Add(sample); return Task.CompletedTask; };

        var inventory = await host.CaptureAsync(binding, fixture.InventoryPath, default);
        var candidate = Assert.Single(inventory.Entries);
        Assert.Equal("track.flac", candidate.RelativePath);
        Assert.NotNull(candidate.Local);
        Assert.Null(candidate.Remote);
        Assert.True(File.Exists(fixture.InventoryPath));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(fixture.InventoryPath))), inventory.Manifest.Sha256);
        Assert.Equal(1, CloudConflictRecoveryPlanner.Create(binding, inventory).DeferredLocalOnly);
        Assert.Equal(("hashing-local", 1L, 1L, 4L),
            (progress[0].Phase, progress[0].Files, progress[0].TotalFiles, progress[0].BytesRead));
        Assert.Contains(progress, sample => sample.Phase == "hashing-cloud" && sample.Files == 0);
        Assert.Contains(progress, sample => sample.Phase == "verifying-artwork" && sample.Files == 0);
        Assert.DoesNotContain(runner.Commands, args => args[0] is "copyto" or "deletefile" or "mkdir");
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

    [Fact]
    public async Task ArchiveProofPrecedesActiveMutationAndReplayProducesReceipt()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture();
        var local = Encoding.UTF8.GetBytes("<album><title>Local</title></album>");
        var remote = Encoding.UTF8.GetBytes("<album><title>Remote</title></album>");
        await File.WriteAllBytesAsync(Path.Combine(fixture.Source, "album.nfo"), local);
        var runner = new FakeCloudRunner();
        runner.Files.Add("cloud:Music/album.nfo", remote);
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
        Assert.Equal(remote, runner.Files["cloud:Music/album.nfo"]);

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
        Assert.Equal(local, runner.Files["cloud:Music/album.nfo"]);
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
        var activeWrite = commands.FindIndex(args => args[0] == "copyto" && args[2] == "cloud:Music/album.nfo");
        Assert.True(archiveWrite >= 0 && activeWrite > archiveWrite);
        Assert.Contains("--immutable", commands[archiveWrite]);
        Assert.Contains("--jottacloud-no-versions=false", commands[activeWrite]);
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

    private sealed class FakeRunner(Func<IReadOnlyList<string>, RcloneCommandResult> respond) : IRcloneProcessRunner
    {
        public List<IReadOnlyList<string>> Commands { get; } = [];
        public Task<RcloneCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(arguments.ToArray());
            return Task.FromResult(respond(arguments));
        }
    }

    private sealed class FakeCloudRunner : IRcloneProcessRunner
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public List<IReadOnlyList<string>> Commands { get; } = [];

        public Task<RcloneCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(arguments.ToArray());
            var result = arguments[0] switch
            {
                "listremotes" => Success("[{\"name\":\"cloud\",\"type\":\"jottacloud\"}]"),
                "lsjson" => Success(Listing(arguments[1])),
                "hashsum" => Hashsum(arguments),
                "mkdir" => Success(""),
                "copyto" => Copy(arguments),
                _ => throw new Xunit.Sdk.XunitException("Unexpected rclone command: " + arguments[0]),
            };
            return Task.FromResult(result);
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
