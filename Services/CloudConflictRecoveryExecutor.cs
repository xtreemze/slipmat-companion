using System.Security.Cryptography;
using System.Text.Json;
using Jellyfin.Plugin.AudioGateway.Models;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>Runs the job-owned conflict plan under the canonical journal lease and Companion command fence.</summary>
public sealed class CloudConflictRecoveryExecutor
{
    private const int MaxArtifactBytes = 128 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly CloudMigrationStore _store;
    private readonly Func<CloudMigrationJob, CloudConflictRecoveryFence.RecoveryLease, ICloudConflictRecoveryHost> _hostFactory;
    private readonly Func<CloudMigrationJob, bool> _bindingsMatch;

    /// <summary>Host and binding observations are server-owned and never accepted from a caller.</summary>
    public CloudConflictRecoveryExecutor(CloudMigrationStore store,
        Func<CloudMigrationJob, CloudConflictRecoveryFence.RecoveryLease, ICloudConflictRecoveryHost> hostFactory,
        Func<CloudMigrationJob, bool> bindingsMatch)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _hostFactory = hostFactory ?? throw new ArgumentNullException(nameof(hostFactory));
        _bindingsMatch = bindingsMatch ?? throw new ArgumentNullException(nameof(bindingsMatch));
    }

    /// <summary>Advance a recovery or retain a blocked, resumable journal on any failure.</summary>
    public async Task<CloudMigrationJob> ExecuteAsync(string jobId, CancellationToken cancellationToken = default)
    {
        await using var journal = await _store.AcquireRecoveryLeaseAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = await journal.ReadAsync(jobId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("migration-not-found");
        var originalAttempt = snapshot.Job.ConflictRecovery?.AttemptId;
        var failureStage = "validating";
        try
        {
            ValidateJob(snapshot.Job);
            failureStage = "validating-source";
            var source = await CloudMigrationManifestBuilder.CaptureAsync(snapshot.Job.LocalPath, cancellationToken).ConfigureAwait(false);
            var binding = Binding(snapshot.Job, source.DigestSha256);
            var directory = _store.ConflictRecoveryDirectory(jobId, binding.AttemptId);
            CloudMigrationStore.RejectRecoverySourceOverlap(snapshot.Job.LocalPath, directory);
            CreateDirectory(directory);
            var planPath = Path.Combine(directory, "plan.json");
            var inventoryPath = Path.Combine(directory, "inventory.json");
            CloudConflictRecoveryPlan? storedPlan = null;
            failureStage = "validating-plan";
            if (snapshot.Job.ConflictRecovery!.Plan is { } storedReference)
            {
                if (storedReference.Path != planPath || !CloudConflictRecoveryPlanner.Digest(storedReference.Sha256)
                    || !CloudConflictRecoveryPlanner.Digest(storedReference.Fingerprint)) throw Invalid();
                var bytes = await ReadBoundedAsync(planPath, cancellationToken).ConfigureAwait(false);
                if (!CloudConflictRecoveryPlanner.EqualDigest(Hash(bytes), storedReference.Sha256)) throw Invalid();
                storedPlan = JsonSerializer.Deserialize<CloudConflictRecoveryPlan>(bytes, JsonOptions) ?? throw Invalid();
                if (storedPlan.Binding != binding || storedPlan.Items is null || storedPlan.Items.Count != storedReference.Count
                    || !CloudConflictRecoveryPlanner.EqualDigest(storedPlan.Fingerprint, storedReference.Fingerprint)
                    || !CloudConflictRecoveryPlanner.EqualDigest(storedPlan.Fingerprint, CloudConflictRecoveryPlanner.Fingerprint(storedPlan))) throw Invalid();
                var inventoryBytes = await ValidateManifestFileAsync(inventoryPath, storedPlan.Manifest, cancellationToken).ConfigureAwait(false);
                var document = JsonSerializer.Deserialize<CloudConflictInventoryDocument>(inventoryBytes, JsonOptions) ?? throw Invalid();
                if (document.Binding != binding || document.Entries is null || document.Entries.Count != storedPlan.Manifest.Count) throw Invalid();
                var rebuilt = CloudConflictRecoveryPlanner.Create(binding, new(binding, storedPlan.Manifest, document.Entries));
                if (!CloudConflictRecoveryPlanner.EqualDigest(rebuilt.Fingerprint, storedPlan.Fingerprint)) throw Invalid();
            }
            var fenceDigest = CloudConflictRecoveryPlanner.BindingFingerprint(binding);
            failureStage = "waiting-for-writer";
            await using var fence = await CloudConflictRecoveryFence.Shared.AcquireAsync(jobId, fenceDigest, cancellationToken).ConfigureAwait(false);
            return await fence.RunAsync(async () =>
            {
                var host = _hostFactory(snapshot.Job, fence) ?? throw new InvalidOperationException("conflict-host-unavailable");
                if (host is ICloudConflictRecoveryProgressHost progressHost)
                {
                    CloudConflictPreflightProgress? last = null;
                    progressHost.ReportProgress = async (sample, token) =>
                    {
                        if (sample.Phase is not ("hashing-local" or "hashing-cloud" or "verifying-artwork")
                            || sample.Files < 0 || sample.TotalFiles < sample.Files
                            || sample.TotalFiles > CloudConflictRecoveryPlanner.MaxCandidates || sample.BytesRead < 0)
                            throw Invalid();
                        failureStage = sample.Phase;
                        if (last is not null && last.Phase == sample.Phase && sample.Files < sample.TotalFiles
                            && sample.Files - last.Files < 32 && sample.ObservedAt - last.ObservedAt < TimeSpan.FromSeconds(1)) return;
                        snapshot = await SaveSessionAsync(journal, snapshot, snapshot.Job.ConflictRecovery! with
                        { Status = "preflighting", Progress = sample }, token).ConfigureAwait(false);
                        last = sample;
                    };
                }
                CloudConflictRecoveryPlan plan;
                if (snapshot.Job.ConflictRecovery!.Plan is null)
                {
                    if (snapshot.Job.ConflictRecovery.Cursor is not null) throw Invalid();
                    failureStage = "preflighting";
                    var inventory = await host.CaptureAsync(binding, inventoryPath, cancellationToken).ConfigureAwait(false);
                    failureStage = "validating-inventory";
                    await ValidateInventoryAsync(binding, inventoryPath, inventory, cancellationToken).ConfigureAwait(false);
                    failureStage = "validating-source";
                    await EnsureCurrentAsync(snapshot.Job, binding, cancellationToken).ConfigureAwait(false);
                    failureStage = "planning";
                    plan = CloudConflictRecoveryPlanner.Create(binding, inventory);
                    var planBytes = JsonSerializer.SerializeToUtf8Bytes(plan, JsonOptions);
                    if (planBytes.Length > MaxArtifactBytes) throw Invalid();
                    failureStage = "persisting-plan";
                    await CreateOrCompareAsync(planPath, planBytes, cancellationToken).ConfigureAwait(false);
                    var reference = new CloudConflictPlanReference(planPath, Hash(planBytes), plan.Fingerprint, plan.Items.Count);
                    snapshot = await SaveSessionAsync(journal, snapshot, snapshot.Job.ConflictRecovery with
                    {
                        Plan = reference, Cursor = new(0, 0), Status = "archiving", Code = null,
                    }, cancellationToken).ConfigureAwait(false);
                }
                else plan = storedPlan ?? throw Invalid();

                var prepared = CloudConflictPreparedPlan.Create(plan);
                plan = prepared.Plan;

                var sourceRecheckedForMutation = false;
                for (var step = 0; step < checked(plan.Items.Count * 8 + 16); step++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ValidateJob(snapshot.Job);
                    var session = snapshot.Job.ConflictRecovery!;
                    var cursor = session.Cursor ?? throw Invalid();
                    var state = Reconstruct(plan, cursor);
                    failureStage = "observing";
                    var live = await host.ObserveAsync(plan, state, cancellationToken).ConfigureAwait(false);
                    var decision = CloudConflictRecoveryTransaction.Decide(prepared, state, live);
                    switch (decision.Action)
                    {
                        case CloudConflictAction.PersistArchiveIntent:
                            RequireIndex(decision, cursor.Archived);
                            snapshot = await SaveSessionAsync(journal, snapshot, session with { Cursor = cursor with { Intent = "archive" }, Status = "archiving" }, cancellationToken).ConfigureAwait(false);
                            break;
                        case CloudConflictAction.Archive:
                            failureStage = "archiving";
                            RequireIndex(decision, cursor.Archived);
                            if (!_bindingsMatch(snapshot.Job)) throw Invalid();
                            await host.ArchiveAsync(plan, cursor.Archived, cancellationToken).ConfigureAwait(false);
                            break;
                        case CloudConflictAction.PersistArchiveReceipt:
                            RequireIndex(decision, cursor.Archived);
                            snapshot = await SaveSessionAsync(journal, snapshot, session with { Cursor = cursor with { Archived = cursor.Archived + 1, Intent = null } }, cancellationToken).ConfigureAwait(false);
                            break;
                        case CloudConflictAction.PersistMutationIntent:
                            RequireIndex(decision, cursor.Resolved);
                            if (!sourceRecheckedForMutation)
                            {
                                await EnsureCurrentAsync(snapshot.Job, binding, cancellationToken).ConfigureAwait(false);
                                sourceRecheckedForMutation = true;
                            }
                            snapshot = await SaveSessionAsync(journal, snapshot, session with { Cursor = cursor with { Intent = "mutation" }, Status = "repairing" }, cancellationToken).ConfigureAwait(false);
                            break;
                        case CloudConflictAction.Mutate:
                            failureStage = "repairing";
                            RequireIndex(decision, cursor.Resolved);
                            if (!sourceRecheckedForMutation)
                            {
                                await EnsureCurrentAsync(snapshot.Job, binding, cancellationToken).ConfigureAwait(false);
                                sourceRecheckedForMutation = true;
                            }
                            if (!_bindingsMatch(snapshot.Job)) throw Invalid();
                            await host.MutateAsync(plan, cursor.Resolved, cancellationToken).ConfigureAwait(false);
                            break;
                        case CloudConflictAction.PersistResolved:
                            RequireIndex(decision, cursor.Resolved);
                            snapshot = await SaveSessionAsync(journal, snapshot, session with { Cursor = cursor with { Resolved = cursor.Resolved + 1, Intent = null } }, cancellationToken).ConfigureAwait(false);
                            break;
                        case CloudConflictAction.Complete:
                            failureStage = "completing";
                            snapshot = await journal.CompareExchangeAsync(snapshot, snapshot.Job with
                            {
                                Phase = CloudMigrationPhases.QueuedRebaseline, Code = "baseline-review-after-conflict-recovery",
                                ConflictRecovery = session with { Status = "completed", Code = null, FailureStage = null }, UpdatedAt = DateTimeOffset.UtcNow,
                            }, cancellationToken).ConfigureAwait(false);
                            return snapshot.Job;
                        default: throw Invalid();
                    }
                }
                throw Invalid();
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            var current = await journal.ReadAsync(jobId, CancellationToken.None).ConfigureAwait(false);
            if (current?.Revision == snapshot.Revision && current.Job.ConflictRecovery is { } session
                && session.AttemptId == originalAttempt && session.OwnsJob)
            {
                snapshot = await journal.CompareExchangeAsync(current, current.Job with
                {
                    Phase = CloudMigrationPhases.Failed, Code = CloudConflictRecoveryFailure.Code(ex),
                    ConflictRecovery = session with
                    { Status = "blocked", Code = CloudConflictRecoveryFailure.Code(ex), FailureStage = failureStage }, UpdatedAt = DateTimeOffset.UtcNow,
                }, CancellationToken.None).ConfigureAwait(false);
            }
            if (ex is OperationCanceledException) throw;
            return snapshot.Job;
        }
    }

    private void ValidateJob(CloudMigrationJob job)
    {
        var session = job.ConflictRecovery;
        if (session is null || !session.OwnsJob || session.WriterPolicy != CloudConflictRecoverySession.CompanionOnlyWriter
            || session.Status is not ("queued" or "preflighting" or "archiving" or "repairing" or "blocked")
            || !Guid.TryParseExact(job.Id, "N", out var jobId) || jobId == Guid.Empty
            || !Guid.TryParseExact(session.AttemptId, "N", out var attemptId) || attemptId == Guid.Empty
            || job.ManifestVersion != CloudMigrationManifestBuilder.CurrentVersion
            || !CloudConflictRecoveryPlanner.Digest(job.ManifestDigestSha256)
            || job.ManifestFileCount is not >= 0 || job.ManifestTotalBytes is not >= 0
            || (job.BaselineHistory?.Count ?? 0) >= CloudMigrationRecovery.MaxBaselineRevisions
            || job.Direction != CloudMigrationDirections.LocalToVfs || job.BackupPath is not null || job.StagingPath is not null
            || job.Maintenance is not null || job.MaintenanceAdmission is not null
            || job.Phase is not (CloudMigrationPhases.Failed or "queued-conflict-recovery")
            || !_bindingsMatch(job)) throw Invalid();
    }

    private static CloudConflictRecoveryBinding Binding(CloudMigrationJob job, string digest)
        => new(job.Id, job.ProfileId, job.LibraryName, job.LocalPath, job.RemoteName, job.RemotePath,
            job.ConflictRecovery!.ArchiveRoot, digest, job.ConflictRecovery.AttemptId);

    private async Task EnsureCurrentAsync(CloudMigrationJob job, CloudConflictRecoveryBinding binding, CancellationToken ct)
    {
        if (!_bindingsMatch(job)) throw Invalid();
        var current = await CloudMigrationManifestBuilder.CaptureAsync(job.LocalPath, ct).ConfigureAwait(false);
        if (!CloudConflictRecoveryPlanner.EqualDigest(current.DigestSha256, binding.SourceManifestSha256) || !_bindingsMatch(job)) throw Invalid();
    }

    private static CloudConflictRecoveryState Reconstruct(CloudConflictRecoveryPlan plan, CloudConflictRecoveryCursor cursor)
    {
        var count = plan.Items.Count;
        if (cursor.Archived < 0 || cursor.Archived > count || cursor.Resolved < 0 || cursor.Resolved > cursor.Archived
            || cursor.Resolved > 0 && cursor.Archived != count
            || cursor.Intent is not (null or "archive" or "mutation")
            || cursor.Intent == "archive" && (cursor.Archived >= count || cursor.Resolved != 0)
            || cursor.Intent == "mutation" && (cursor.Archived != count || cursor.Resolved >= count)) throw Invalid();
        var items = new CloudConflictItemState[count];
        for (var i = 0; i < count; i++)
        {
            var item = plan.Items[i];
            items[i] = i < cursor.Resolved
                ? new(CloudConflictItemStage.Resolved, item.Remote.Sha256,
                    new(plan.Fingerprint, item.RelativePath, item.Operation, item.Local?.Sha256, "canonical-resolved-prefix"))
                : i < cursor.Archived ? new(CloudConflictItemStage.Archived, item.Remote.Sha256)
                : new(CloudConflictItemStage.Planned);
        }
        if (cursor.Intent == "archive") items[cursor.Archived] = new(CloudConflictItemStage.ArchiveIntent);
        if (cursor.Intent == "mutation") items[cursor.Resolved] = new(CloudConflictItemStage.MutationIntent, plan.Items[cursor.Resolved].Remote.Sha256);
        var state = new CloudConflictRecoveryState(plan.Fingerprint, items);
        return state;
    }

    private static async Task<CloudMigrationJournalSnapshot> SaveSessionAsync(CloudMigrationJournalLease lease,
        CloudMigrationJournalSnapshot snapshot, CloudConflictRecoverySession session, CancellationToken ct)
        => await lease.CompareExchangeAsync(snapshot, snapshot.Job with { ConflictRecovery = session with { FailureStage = null }, UpdatedAt = DateTimeOffset.UtcNow }, ct).ConfigureAwait(false);

    private static void RequireIndex(CloudConflictDecision decision, int expected)
    {
        if (decision.ItemIndex != expected) throw Invalid();
    }
    private static async Task ValidateInventoryAsync(CloudConflictRecoveryBinding binding, string path, CloudConflictInventory inventory, CancellationToken ct)
    {
        if (inventory is null || inventory.Binding != binding) throw Invalid();
        var bytes = await ValidateManifestFileAsync(path, inventory.Manifest, ct).ConfigureAwait(false);
        var document = JsonSerializer.Deserialize<CloudConflictInventoryDocument>(bytes, JsonOptions) ?? throw Invalid();
        if (document.Binding != binding || document.Entries is null || inventory.Entries is null
            || document.Entries.Count != inventory.Manifest.Count || !document.Entries.SequenceEqual(inventory.Entries)) throw Invalid();
    }
    private static async Task<byte[]> ValidateManifestFileAsync(string path, CloudConflictManifestDescriptor descriptor, CancellationToken ct)
    {
        if (descriptor is null || descriptor.Path != path || !CloudConflictRecoveryPlanner.Digest(descriptor.Sha256)
            || descriptor.Count is < 0 or > CloudConflictRecoveryPlanner.MaxCandidates) throw Invalid();
        var bytes = await ReadBoundedAsync(path, ct).ConfigureAwait(false);
        if (!CloudConflictRecoveryPlanner.EqualDigest(Hash(bytes), descriptor.Sha256)) throw Invalid();
        return bytes;
    }
    private static async Task<byte[]> ReadBoundedAsync(string path, CancellationToken ct)
    {
        CloudMigrationJournal.RejectLink(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxArtifactBytes) throw Invalid();
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        return bytes;
    }
    private static async Task CreateOrCompareAsync(string path, byte[] bytes, CancellationToken ct)
    {
        CloudMigrationJournal.RejectLink(path);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        FileStream stream;
        try { stream = new FileStream(path, options); }
        catch (IOException) when (File.Exists(path))
        {
            var existing = await ReadBoundedAsync(path, ct).ConfigureAwait(false);
            if (!existing.AsSpan().SequenceEqual(bytes)) throw Invalid();
            return;
        }
        await using (stream)
        {
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        CloudMigrationJournalLease.FlushDirectory(Path.GetDirectoryName(path)!);
    }
    private static void CreateDirectory(string directory)
    {
        var full = Path.GetFullPath(directory);
        var root = Path.GetPathRoot(full) ?? throw Invalid();
        var current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            CloudMigrationJournal.RejectLink(current);
            if (!Directory.Exists(current))
            {
                if (OperatingSystem.IsWindows()) Directory.CreateDirectory(current);
                else Directory.CreateDirectory(current, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                CloudMigrationJournalLease.FlushDirectory(Path.GetDirectoryName(current)!);
            }
            CloudMigrationJournal.RejectLink(current);
        }
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(full);
            if ((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0) throw Invalid();
        }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static InvalidOperationException Invalid() => new("conflict-recovery-invalid");
}
