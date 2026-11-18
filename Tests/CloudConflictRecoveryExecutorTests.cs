using System.Security.Cryptography;
using System.Text.Json;
using Jellyfin.Plugin.AudioGateway.Configuration;
using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

[Collection("Shared rclone recovery fence")]
public sealed class CloudConflictRecoveryExecutorTests
{
    private const string A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Theory]
    [InlineData("listing-cloud", 4181, 0, false, true)]
    [InlineData("hashing-cloud", 1, 0, false, false)]
    [InlineData("listing-cloud", 100001, 0, false, false)]
    [InlineData("listing-cloud", 2, 1, true, false)]
    [InlineData("unknown-provider-phase", 0, 0, false, false)]
    public async Task UnknownTotalProgressIsBoundedAndOnlyAllowedForListing(
        string phase, long files, long total, bool known, bool accepted)
    {
        using var fixture = await Fixture.CreateAsync();
        var progress = new CloudConflictPreflightProgress(phase, files, total, 0, DateTimeOffset.UtcNow, known);
        fixture.Host.CaptureProgress = progress;
        var result = await fixture.Executor.ExecuteAsync(fixture.Job.Id);
        Assert.Equal(accepted ? "completed" : "blocked", result.ConflictRecovery!.Status);
        if (accepted) Assert.Equal(progress, result.ConflictRecovery.Progress);
        else
        {
            Assert.Null(result.ConflictRecovery.Progress);
            Assert.Equal(0, fixture.Host.ArchiveCount);
            Assert.Equal(0, fixture.Host.MutationCount);
        }
    }

    [Theory]
    [InlineData("conflict-remote-hash-failed")]
    [InlineData("conflict-provider-features-unavailable")]
    [InlineData("conflict-provider-case-unsupported")]
    [InlineData("conflict-provider-alias-unsupported")]
    [InlineData("conflict-provider-alias-probe-failed")]
    [InlineData("conflict-provider-version-unsupported")]
    [InlineData("conflict-provider-probe-selfcheck-failed")]
    [InlineData("conflict-remote-spelling-changed")]
    public async Task KnownHostFailurePersistsOnlyAllowlistedCauseAndOperationStage(string code)
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Host.CaptureFailure = new InvalidOperationException(code);
        var result = await fixture.Executor.ExecuteAsync(fixture.Job.Id);
        Assert.Equal(code, result.Code);
        Assert.Equal(code, result.ConflictRecovery!.Code);
        Assert.Equal("preflighting", FailureStage(result));
        Assert.Equal(0, fixture.Host.ArchiveCount);
        Assert.Equal(0, fixture.Host.MutationCount);
        Assert.Equal("blocked", (await fixture.Store.GetAsync(fixture.Job.Id))!.ConflictRecovery!.Status);
    }

    [Theory]
    [InlineData("https://provider.invalid/?access_token=secret-token /private/media/path")]
    [InlineData("conflict-remote-hash-failed https://provider.invalid/?access_token=secret-token")]
    public async Task UnknownFailurePersistsGenericCodeAndStageWithoutExceptionDetails(string message)
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Host.ArchiveFailure = new InvalidOperationException(message);
        var result = await fixture.Executor.ExecuteAsync(fixture.Job.Id);
        Assert.Equal("conflict-recovery-blocked", result.Code);
        Assert.Equal("conflict-recovery-blocked", result.ConflictRecovery!.Code);
        Assert.Equal("archiving", FailureStage(result));
        Assert.Equal("archive", result.ConflictRecovery.Cursor!.Intent);
        Assert.NotNull(result.ConflictRecovery.Plan);
        Assert.Equal(0, fixture.Host.MutationCount);
        var persisted = JsonSerializer.Serialize(await fixture.Store.GetAsync(fixture.Job.Id));
        Assert.DoesNotContain("secret-token", persisted);
        Assert.DoesNotContain("provider.invalid", persisted);
        Assert.DoesNotContain("/private/media/path", persisted);
    }

    private static string? FailureStage(CloudMigrationJob job)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(job.ConflictRecovery, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return json.RootElement.TryGetProperty("failureStage", out var stage) ? stage.GetString() : null;
    }

    [Fact]
    public async Task StoreInsideSourceBlocksBeforeCreatingRecoveryArtifacts()
    {
        using var fixture = await Fixture.CreateAsync(storeInsideSource: true);
        var result = await fixture.Executor.ExecuteAsync(fixture.Job.Id);
        Assert.Equal("blocked", result.ConflictRecovery!.Status);
        Assert.Equal(0, fixture.Host.CaptureCount);
        Assert.False(Directory.Exists(Path.Combine(fixture.Job.LocalPath, "cloud-migrations", "conflict-recovery")));
        Assert.Equal("content", await File.ReadAllTextAsync(Path.Combine(fixture.Job.LocalPath, "track")));
    }

    [Fact]
    public async Task UnsupportedAudioConflictBlocksBeforeArchiveOrMutationAndPreservesJob()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Host.Entries = [new("album.nfo", new(1, A, "local-a", CloudConflictContentKind.XmlMetadata), new(1, B, "remote-a", CloudConflictContentKind.XmlMetadata)),
            new("song.flac", new(1, A, "local", CloudConflictContentKind.Audio), new(1, B, "remote", CloudConflictContentKind.Audio))];
        var result = await fixture.Executor.ExecuteAsync(fixture.Job.Id);
        Assert.Equal(CloudMigrationPhases.Failed, result.Phase);
        Assert.Equal("blocked", result.ConflictRecovery!.Status);
        Assert.Equal(0, fixture.Host.ArchiveCount);
        Assert.Equal(0, fixture.Host.MutationCount);
        Assert.Equal(fixture.Job.ManifestDigestSha256, result.ManifestDigestSha256);
        Assert.Equal(fixture.Job.BaselineHistory, result.BaselineHistory);
    }

    [Fact]
    public async Task ArchiveAllBeforeAnyMutationThenQueueCanonicalRebaseline()
    {
        using var fixture = await Fixture.CreateAsync();
        var result = await fixture.Executor.ExecuteAsync(fixture.Job.Id);
        Assert.Equal("queued-rebaseline", result.Phase);
        Assert.Equal("completed", result.ConflictRecovery!.Status);
        Assert.Equal(2, fixture.Host.ArchiveCount);
        Assert.Equal(2, fixture.Host.MutationCount);
        Assert.True(fixture.Host.ArchivedBeforeFirstMutation);
        Assert.Equal(fixture.Job.ManifestDigestSha256, result.ManifestDigestSha256);
        if (!OperatingSystem.IsWindows())
        {
            var path = result.ConflictRecovery.Plan!.Path;
            var unsafeBits = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            Assert.Equal(0, (int)(File.GetUnixFileMode(path) & unsafeBits));
            Assert.Equal(0, (int)(File.GetUnixFileMode(Path.GetDirectoryName(path)!) & unsafeBits));
        }
    }

    [Fact]
    public async Task RetryAfterArchiveBeforeReceiptDoesNotCopyTwice()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Host.CrashAfterArchive = true;
        var failed = await fixture.Executor.ExecuteAsync(fixture.Job.Id);
        Assert.Equal("blocked", failed.ConflictRecovery!.Status);
        Assert.Equal("archive", failed.ConflictRecovery.Cursor!.Intent);
        Assert.Equal(0, fixture.Host.MutationCount);
        var resumed = await fixture.Executor.ExecuteAsync(fixture.Job.Id);
        Assert.Equal("completed", resumed.ConflictRecovery!.Status);
        Assert.Null(FailureStage(resumed));
        Assert.Null(resumed.ConflictRecovery.Code);
        Assert.Equal(2, fixture.Host.ArchiveCount);
        Assert.Equal(2, fixture.Host.MutationCount);
    }

    [Fact]
    public async Task RetryAfterMutationRequiresBoundReceiptAndDoesNotRepeatMutation()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Host.CrashAfterMutation = true;
        var failed = await fixture.Executor.ExecuteAsync(fixture.Job.Id);
        Assert.Equal("blocked", failed.ConflictRecovery!.Status);
        Assert.Equal("mutation", failed.ConflictRecovery.Cursor!.Intent);
        var resumed = await fixture.Executor.ExecuteAsync(fixture.Job.Id);
        Assert.Equal("completed", resumed.ConflictRecovery!.Status);
        Assert.Equal(2, fixture.Host.MutationCount);
    }

    [Fact]
    public async Task StaleBindingBlocksBeforeAnyHostEffect()
    {
        using var fixture = await Fixture.CreateAsync();
        var executor = new CloudConflictRecoveryExecutor(fixture.Store, (_, _) => fixture.Host, _ => false);
        var result = await executor.ExecuteAsync(fixture.Job.Id);
        Assert.Equal("blocked", result.ConflictRecovery!.Status);
        Assert.Equal(0, fixture.Host.ArchiveCount);
        Assert.Equal(0, fixture.Host.MutationCount);
    }

    [Fact]
    public async Task LostArchiveProofBlocksBeforeAnyMutation()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Host.LoseFirstArchiveAfterSecond = true;
        var result = await fixture.Executor.ExecuteAsync(fixture.Job.Id);
        Assert.Equal("blocked", result.ConflictRecovery!.Status);
        Assert.Equal(0, fixture.Host.MutationCount);
    }

    [Fact]
    public async Task WrongOperationReceiptBlocksFurtherMutation()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Host.WrongReceipt = true;
        var result = await fixture.Executor.ExecuteAsync(fixture.Job.Id);
        Assert.Equal("blocked", result.ConflictRecovery!.Status);
        Assert.Equal(1, fixture.Host.MutationCount);
        Assert.Equal("mutation", result.ConflictRecovery.Cursor!.Intent);
    }

    [Fact]
    public async Task CancellationPersistsIntentAndBlocksWithoutLosingPlan()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Host.CancelAfterArchive = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Executor.ExecuteAsync(fixture.Job.Id));
        var result = (await fixture.Store.GetAsync(fixture.Job.Id))!;
        Assert.Equal("blocked", result.ConflictRecovery!.Status);
        Assert.NotNull(result.ConflictRecovery.Plan);
        Assert.Equal("archive", result.ConflictRecovery.Cursor!.Intent);
        Assert.Equal("conflict-recovery-cancelled", result.ConflictRecovery.Code);
        Assert.Equal("archiving", FailureStage(result));
        Assert.Equal(0, fixture.Host.MutationCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrphanPlanIsAdmittedOnlyWhenFreshRevalidationMatches(bool corrupt)
    {
        using var fixture = await Fixture.CreateAsync();
        var session = fixture.Job.ConflictRecovery!;
        var source = await CloudMigrationManifestBuilder.CaptureAsync(fixture.Job.LocalPath);
        var binding = new CloudConflictRecoveryBinding(fixture.Job.Id, fixture.Job.ProfileId, fixture.Job.LibraryName,
            fixture.Job.LocalPath, fixture.Job.RemoteName, fixture.Job.RemotePath, session.ArchiveRoot, source.DigestSha256, session.AttemptId);
        var directory = Path.Combine(fixture.Root, "cloud-migrations", "conflict-recovery", fixture.Job.Id, session.AttemptId);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var inventory = await fixture.Host.CaptureAsync(binding, Path.Combine(directory, "inventory.json"), default);
        var plan = CloudConflictRecoveryPlanner.Create(binding, inventory);
        var planBytes = JsonSerializer.SerializeToUtf8Bytes(plan, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var planPath = Path.Combine(directory, "plan.json");
        await File.WriteAllBytesAsync(planPath, corrupt ? [1, 2, 3] : planBytes);
        var result = await fixture.Executor.ExecuteAsync(fixture.Job.Id);
        Assert.Equal(corrupt ? "blocked" : "completed", result.ConflictRecovery!.Status);
        Assert.True(File.Exists(planPath));
        if (corrupt)
        {
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(planPath));
            Assert.Null(result.ConflictRecovery.Plan);
            Assert.Equal(0, fixture.Host.ArchiveCount);
        }
    }

    [Fact]
    public async Task TamperedPlanBlocksBeforeFurtherEffects()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Host.CrashAfterArchive = true;
        var first = await fixture.Executor.ExecuteAsync(fixture.Job.Id);
        var plan = first.ConflictRecovery!.Plan!;
        await File.AppendAllTextAsync(plan.Path, "tamper");
        var result = await fixture.Executor.ExecuteAsync(fixture.Job.Id);
        Assert.Equal("blocked", result.ConflictRecovery!.Status);
        Assert.Equal(1, fixture.Host.ArchiveCount);
        Assert.Equal(0, fixture.Host.MutationCount);
    }

    [Fact]
    public async Task TamperedInventoryBlocksResumeBeforeFurtherEffects()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Host.CrashAfterArchive = true;
        var first = await fixture.Executor.ExecuteAsync(fixture.Job.Id);
        var inventory = Path.Combine(Path.GetDirectoryName(first.ConflictRecovery!.Plan!.Path)!, "inventory.json");
        await File.AppendAllTextAsync(inventory, "tamper");
        var result = await fixture.Executor.ExecuteAsync(fixture.Job.Id);
        Assert.Equal("blocked", result.ConflictRecovery!.Status);
        Assert.Equal(1, fixture.Host.ArchiveCount);
        Assert.Equal(0, fixture.Host.MutationCount);
    }

    [Fact]
    public async Task ChangedSourceBlocksResumeBeforeFurtherEffects()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Host.CrashAfterArchive = true;
        await fixture.Executor.ExecuteAsync(fixture.Job.Id);
        await File.AppendAllTextAsync(Path.Combine(fixture.Job.LocalPath, "track"), "changed");
        var result = await fixture.Executor.ExecuteAsync(fixture.Job.Id);
        Assert.Equal("blocked", result.ConflictRecovery!.Status);
        Assert.Equal(1, fixture.Host.ArchiveCount);
        Assert.Equal(0, fixture.Host.MutationCount);
    }

    [Fact]
    public async Task UnproductiveArchiveIsBoundedAndNeverMutatesActiveObjects()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Host.IgnoreArchive = true;
        var result = await fixture.Executor.ExecuteAsync(fixture.Job.Id);
        Assert.Equal("blocked", result.ConflictRecovery!.Status);
        Assert.InRange(fixture.Host.ArchiveCount, 1, 32);
        Assert.Equal(0, fixture.Host.MutationCount);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("conflict-executor-").FullName;
        public CloudMigrationStore Store { get; private set; } = null!;
        public CloudMigrationJob Job { get; private set; } = null!;
        public FakeHost Host { get; } = new();
        public CloudConflictRecoveryExecutor Executor { get; private set; } = null!;
        public static async Task<Fixture> CreateAsync(bool storeInsideSource = false)
        {
            var f = new Fixture(); var source = Path.Combine(f.Root, "source"); Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "track"), "content");
            var now = DateTimeOffset.UtcNow;
            f.Store = new(new Config(storeInsideSource ? source : f.Root));
            f.Job = new CloudMigrationJob(Guid.NewGuid().ToString("N"), "music", "Music", "local-to-vfs", "failed", "baseline-verification-failed", source, null, null, "tele2", "Slipmat", "", false, null, now, now,
                ManifestVersion: 1, ManifestDigestSha256: A, ManifestFileCount: 1, ManifestTotalBytes: 7,
                ConflictRecovery: new(Guid.NewGuid().ToString("N"), "Archive/run1", CloudConflictRecoverySession.CompanionOnlyWriter, "queued", now));
            await f.Store.SaveAsync(f.Job);
            f.Executor = new(f.Store, (_, _) => f.Host, _ => true);
            return f;
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
    private sealed class Config(string root) : ICloudProjectionConfigurationSource
    {
        public PluginConfiguration GetCurrent() => new() { StoreRoot = root };
    }
    private sealed class FakeHost : ICloudConflictRecoveryHost, ICloudConflictRecoveryProgressHost
    {
        public CloudConflictCandidate[] Entries = [
            new("album.nfo", new(1, A, "local-a", CloudConflictContentKind.XmlMetadata), new(1, B, "remote-a", CloudConflictContentKind.XmlMetadata)),
            new("cover.jpg", null, new(1, B, "remote-b", CloudConflictContentKind.Jpeg))];
        public int ArchiveCount, MutationCount, CaptureCount;
        public bool CrashAfterArchive, CrashAfterMutation, LoseFirstArchiveAfterSecond, WrongReceipt, CancelAfterArchive, IgnoreArchive;
        public bool ArchivedBeforeFirstMutation;
        public Exception? CaptureFailure, ArchiveFailure;
        public CloudConflictPreflightProgress? CaptureProgress;
        public Func<CloudConflictPreflightProgress, CancellationToken, Task>? ReportProgress { get; set; }
        private CloudConflictLiveItem[]? _items;
        public async Task<CloudConflictInventory> CaptureAsync(CloudConflictRecoveryBinding binding, string manifestPath, CancellationToken cancellationToken)
        {
            CaptureCount++;
            if (CaptureProgress is not null && ReportProgress is not null)
                await ReportProgress(CaptureProgress, cancellationToken);
            if (CaptureFailure is not null) throw CaptureFailure;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new CloudConflictInventoryDocument(binding, Entries), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (File.Exists(manifestPath))
            {
                if (!(await File.ReadAllBytesAsync(manifestPath, cancellationToken)).AsSpan().SequenceEqual(bytes)) throw new InvalidOperationException("stale inventory");
            }
            else await File.WriteAllBytesAsync(manifestPath, bytes, cancellationToken);
            return new(binding, new(manifestPath, Convert.ToHexString(SHA256.HashData(bytes)), Entries.Length), Entries);
        }
        public Task<CloudConflictRecoveryObservation> ObserveAsync(CloudConflictRecoveryPlan plan, CloudConflictRecoveryState state, CancellationToken cancellationToken)
        {
            _items ??= CloudConflictRecoveryObservation.Original(plan).Items.ToArray();
            return Task.FromResult(new CloudConflictRecoveryObservation(plan.Binding, plan.Fingerprint, _items.ToArray(), new(CloudConflictProtectionKind.ExclusiveWriterLease, "trusted-server-fence")));
        }
        public Task ArchiveAsync(CloudConflictRecoveryPlan plan, int index, CancellationToken cancellationToken)
        {
            ArchiveCount++;
            if (ArchiveFailure is not null) throw ArchiveFailure;
            if (IgnoreArchive) return Task.CompletedTask;
            _items![index] = _items[index] with { ArchiveSha256 = plan.Items[index].Remote.Sha256 };
            if (LoseFirstArchiveAfterSecond && index == 1) _items[0] = _items[0] with { ArchiveSha256 = null };
            if (CancelAfterArchive) throw new OperationCanceledException();
            if (CrashAfterArchive) { CrashAfterArchive = false; throw new InvalidOperationException("simulated interruption"); }
            return Task.CompletedTask;
        }
        public Task MutateAsync(CloudConflictRecoveryPlan plan, int index, CancellationToken cancellationToken)
        {
            if (MutationCount == 0) ArchivedBeforeFirstMutation = ArchiveCount == plan.Items.Count;
            MutationCount++; var item = plan.Items[index];
            _items![index] = _items[index] with { Active = item.Local,
                MutationReceipt = new(plan.Fingerprint, WrongReceipt ? "other-path" : item.RelativePath, item.Operation, item.Local?.Sha256, "verified") };
            if (CrashAfterMutation) { CrashAfterMutation = false; throw new InvalidOperationException("simulated interruption"); }
            return Task.CompletedTask;
        }
    }
}
