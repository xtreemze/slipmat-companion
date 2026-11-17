using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.AudioGateway.Models;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>Provider operations for content-verified archive and metadata recovery.</summary>
public sealed class RcloneCloudConflictRecoveryHost : ICloudConflictRecoveryHost, ICloudConflictRecoveryProgressHost
{
    private const int MaxListingBytes = 2 * 1024 * 1024;
    private const long MaxCachedBytes = 16L * 1024 * 1024 * 1024;
    private const long CacheReserveBytes = 512L * 1024 * 1024;
    private static readonly TimeSpan ListingTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ObjectTimeout = TimeSpan.FromMinutes(45);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IRcloneProcessRunner _runner;
    private readonly CloudConflictRecoveryFence.RecoveryLease _lease;
    private readonly string _cacheRoot;
    private readonly string _executable;
    private readonly string _writerPolicy;
    private readonly Dictionary<string, CloudConflictObject> _localObjects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CloudConflictObject> _activeObjects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CloudConflictObject> _archiveObjects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _localCachePaths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _remoteCachePaths = new(StringComparer.Ordinal);
    private CloudConflictRecoveryBinding? _binding;
    private CloudConflictRecoveryPlan? _observedPlan;
    private CloudConflictRecoveryState? _observedState;
    private CloudConflictRecoveryObservation? _observed;
    private bool _observationInitialized;
    private bool _providerVerified;
    private bool _allArchivesVerified;

    /// <inheritdoc />
    public Func<CloudConflictPreflightProgress, CancellationToken, Task>? ReportProgress { get; set; }

    /// <summary>Create a host bound to an unforgeable server-created writer lease.</summary>
    public RcloneCloudConflictRecoveryHost(
        IRcloneProcessRunner runner,
        CloudConflictRecoveryFence.RecoveryLease lease,
        string cacheRoot,
        string writerPolicy,
        string? executable = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _lease = lease ?? throw new ArgumentNullException(nameof(lease));
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        if (!Path.IsPathFullyQualified(cacheRoot)) throw new ArgumentException("conflict-cache-root-not-absolute", nameof(cacheRoot));
        _cacheRoot = Path.GetFullPath(cacheRoot);
        _writerPolicy = writerPolicy ?? string.Empty;
        _executable = string.IsNullOrWhiteSpace(executable)
            ? Environment.GetEnvironmentVariable(RcloneCliHost.ExecutableEnvironmentVariable) is { Length: > 0 } configured
                ? configured.Trim() : "rclone"
            : executable.Trim();
    }

    /// <inheritdoc />
    public Task<CloudConflictInventory> CaptureAsync(
        CloudConflictRecoveryBinding binding,
        string manifestPath,
        CancellationToken cancellationToken)
        => _lease.RunAsync(() => CaptureCoreAsync(binding, manifestPath, cancellationToken));

    /// <inheritdoc />
    public Task<CloudConflictRecoveryObservation> ObserveAsync(
        CloudConflictRecoveryPlan plan,
        CloudConflictRecoveryState state,
        CancellationToken cancellationToken)
        => _lease.RunAsync(() => ObserveCoreAsync(plan, state, cancellationToken));

    /// <inheritdoc />
    public Task ArchiveAsync(CloudConflictRecoveryPlan plan, int index, CancellationToken cancellationToken)
        => _lease.RunAsync(async () =>
        {
            var item = RequireObservedIntent(plan, index, CloudConflictItemStage.ArchiveIntent);
            var active = await ObserveRemoteFileAsync(plan.Binding, plan.Binding.ActiveRoot, item.RelativePath, cancellationToken).ConfigureAwait(false);
            if (active is null || !SameContent(active, item.Remote)) throw Invalid("conflict-source-changed");

            var archiveParts = SplitArchivePath(plan.Binding, item.ArchivePath);
            await RunSuccessAsync(["mkdir", RcloneCliHost.BuildRemoteSpec(plan.Binding.RemoteName, archiveParts.Parent)], ListingTimeout, cancellationToken)
                .ConfigureAwait(false);
            var existing = await ObserveRemoteFileAsync(plan.Binding, "", item.ArchivePath, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (!SameContent(existing, item.Remote)) throw Invalid("conflict-archive-target-occupied");
                _archiveObjects[item.RelativePath] = existing;
                return true;
            }

            var sourceSpec = RemoteSpec(plan.Binding, plan.Binding.ActiveRoot, item.RelativePath);
            var destinationSpec = RcloneCliHost.BuildRemoteSpec(plan.Binding.RemoteName, item.ArchivePath);
            await RunSuccessAsync(
                ["copyto", sourceSpec, destinationSpec, "--immutable", "--checksum", "--jottacloud-no-versions=false"],
                ObjectTimeout,
                cancellationToken).ConfigureAwait(false);

            var archived = await HashRemoteFileAsync(plan.Binding, "", item.ArchivePath, cancellationToken).ConfigureAwait(false);
            var original = await HashRemoteFileAsync(plan.Binding, plan.Binding.ActiveRoot, item.RelativePath, cancellationToken).ConfigureAwait(false);
            if (!SameContent(archived, item.Remote) || !SameContent(original, item.Remote)) throw Invalid("conflict-archive-verification-failed");
            _archiveObjects[item.RelativePath] = archived;
            return true;
        });

    /// <inheritdoc />
    public Task MutateAsync(CloudConflictRecoveryPlan plan, int index, CancellationToken cancellationToken)
        => _lease.RunAsync(async () =>
        {
            var item = RequireObservedIntent(plan, index, CloudConflictItemStage.MutationIntent);
            if (!_allArchivesVerified)
            {
                ValidateAllArchives(plan);
                _allArchivesVerified = true;
            }

            var active = await ObserveRemoteFileAsync(plan.Binding, plan.Binding.ActiveRoot, item.RelativePath, cancellationToken).ConfigureAwait(false);
            if (active is null || !SameContent(active, item.Remote)) throw Invalid("conflict-active-object-changed");

            if (item.Operation == CloudConflictOperation.ReplaceWithLocal)
            {
                if (item.Local is null) throw Invalid("conflict-local-object-missing");
                var freshLocal = await CloudConflictLocalContent.ObserveAsync(plan.Binding.LocalPath, item.RelativePath, cancellationToken).ConfigureAwait(false);
                if (!SameContentAndIdentity(freshLocal, item.Local)) throw Invalid("conflict-local-object-changed");
                var cachePath = EnsureLocalCachePath(plan.Binding, item.RelativePath);
                await CloudConflictLocalContent.CopyVerifiedAsync(plan.Binding.LocalPath, item.RelativePath, item.Local, cachePath, cancellationToken).ConfigureAwait(false);
                var cached = await CloudConflictLocalContent.ReadCacheAsync(cachePath, cancellationToken).ConfigureAwait(false);
                if (!SameContent(cached, item.Local) || cached.Kind != item.Local.Kind) throw Invalid("conflict-local-cache-verification-failed");
                await RunSuccessAsync(
                    ["copyto", cachePath, RemoteSpec(plan.Binding, plan.Binding.ActiveRoot, item.RelativePath),
                        "--checksum", "--jottacloud-no-versions=false"],
                    ObjectTimeout,
                    cancellationToken).ConfigureAwait(false);
                var target = await HashRemoteFileAsync(plan.Binding, plan.Binding.ActiveRoot, item.RelativePath, cancellationToken).ConfigureAwait(false);
                if (!SameContent(target, item.Local)) throw Invalid("conflict-mutation-verification-failed");
                _activeObjects[item.RelativePath] = target;
            }
            else if (item.Operation == CloudConflictOperation.RemoveRemoteOnly)
            {
                if (item.Local is not null) throw Invalid("conflict-delete-has-local-object");
                await RunSuccessAsync(
                    ["deletefile", RemoteSpec(plan.Binding, plan.Binding.ActiveRoot, item.RelativePath),
                        "--jottacloud-hard-delete=false", "--jottacloud-no-versions=false"],
                    ObjectTimeout,
                    cancellationToken).ConfigureAwait(false);
                var target = await ObserveRemoteFileAsync(plan.Binding, plan.Binding.ActiveRoot, item.RelativePath, cancellationToken).ConfigureAwait(false);
                if (target is not null) throw Invalid("conflict-delete-verification-failed");
                _activeObjects.Remove(item.RelativePath);
            }
            else throw Invalid("conflict-operation-unsupported");

            _observed = null;
            return true;
        });

    private async Task<CloudConflictInventory> CaptureCoreAsync(
        CloudConflictRecoveryBinding binding,
        string manifestPath,
        CancellationToken cancellationToken)
    {
        ValidateBinding(binding);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        if (!Path.IsPathFullyQualified(manifestPath)) throw Invalid("conflict-manifest-path-invalid");
        EnsureCache(binding);
        await RequireJottacloudAsync(binding.RemoteName, cancellationToken).ConfigureAwait(false);
        _providerVerified = true;

        var local = await CaptureLocalInventoryAsync(binding.LocalPath, cancellationToken).ConfigureAwait(false);
        if (!CloudConflictRecoveryPlanner.EqualDigest(local.SourceManifestSha256, binding.SourceManifestSha256))
            throw Invalid("conflict-local-source-changed");
        var remote = await ListRemoteTreeAsync(binding, binding.ActiveRoot, cancellationToken).ConfigureAwait(false);
        var allPaths = new SortedSet<string>(local.Files.Keys, StringComparer.Ordinal);
        foreach (var path in remote.Keys)
            if (!allPaths.Add(path)) { /* shared path */ }
        if (allPaths.Count > CloudConflictRecoveryPlanner.MaxCandidates)
            throw Invalid("conflict-inventory-limit-exceeded");
        var collisions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in allPaths)
            if (!collisions.Add(path)) throw Invalid("conflict-path-collision");

        var candidates = new List<CloudConflictCandidate>(allPaths.Count);
        long remoteHashed = 0;
        long remoteBytesRead = 0;
        var remoteObjects = new Dictionary<string, CloudConflictObject>(StringComparer.Ordinal);
        foreach (var group in remote.Values.GroupBy(file => Parent(file.RelativePath), StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hashedDirectory = await HashRemoteDirectoryAsync(binding, binding.ActiveRoot, group.Key,
                group.ToArray(), cancellationToken).ConfigureAwait(false);
            foreach (var pair in hashedDirectory) remoteObjects.Add(pair.Key, pair.Value);
            remoteHashed += hashedDirectory.Count;
            remoteBytesRead = checked(remoteBytesRead + hashedDirectory.Values.Sum(value => value.Size));
            await ReportAsync("hashing-cloud", remoteHashed, remote.Count, remoteBytesRead, cancellationToken)
                .ConfigureAwait(false);
        }
        if (remoteHashed != remote.Count) throw Invalid("conflict-remote-hash-unrecognized");
        if (remoteHashed == 0)
            await ReportAsync("hashing-cloud", 0, remote.Count, 0, cancellationToken).ConfigureAwait(false);
        foreach (var path in allPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CloudConflictObject? localObject = local.Files.TryGetValue(path, out var localFile)
                ? localFile.Object : null;
            var remoteObject = remoteObjects.GetValueOrDefault(path);
            candidates.Add(new(path, localObject, remoteObject));
        }

        // Recheck the complete local namespace after payload reads, then confirm the
        // source manifest still matches the job binding before creating artifacts.
        var localAfter = await CaptureLocalMetadataAsync(binding.LocalPath, cancellationToken).ConfigureAwait(false);
        if (!CloudConflictRecoveryPlanner.EqualDigest(localAfter.SourceManifestSha256, binding.SourceManifestSha256)
            || !SameLocalIdentities(local.Files, localAfter.Files)) throw Invalid("conflict-local-source-changed");

        var toVerify = new List<int>();
        long cacheBytes = 0;
        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            var changed = candidate.Local is null || candidate.Remote is null
                || candidate.Local.Size != candidate.Remote.Size
                || !CloudConflictRecoveryPlanner.EqualDigest(candidate.Local.Sha256, candidate.Remote.Sha256);
            if (!changed) continue;
            var extension = Path.GetExtension(candidate.RelativePath).ToLowerInvariant();
            // A local-only path is deferred regardless of content type. It does
            // not authorize an active-root write in this recovery transaction.
            if (candidate.Remote is null) continue;
            var allowed = candidate.Local is null
                ? extension is ".jpg" or ".jpeg" or ".png"
                : extension is ".nfo" or ".jpg" or ".jpeg" or ".png";
            if (!allowed) throw Invalid("conflict-unsupported-content-difference");
            toVerify.Add(i);
            checked { cacheBytes += (candidate.Local?.Size ?? 0) + (candidate.Remote?.Size ?? 0); }
        }
        await EnsureCacheSpaceAsync(cacheBytes, cancellationToken).ConfigureAwait(false);

        var verificationTotal = toVerify.Sum(index =>
            (candidates[index].Local is null ? 0 : 1) + (candidates[index].Remote is null ? 0 : 1));
        long verified = 0;
        long verificationBytesRead = 0;
        foreach (var index in toVerify)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = candidates[index];
            var kind = ExpectedKind(candidate.RelativePath);
            if (candidate.Local is not null)
            {
                var localCache = EnsureLocalCachePath(binding, candidate.RelativePath);
                await CloudConflictLocalContent.CopyVerifiedAsync(binding.LocalPath, candidate.RelativePath,
                    candidate.Local, localCache, cancellationToken).ConfigureAwait(false);
                var cachedLocal = await CloudConflictLocalContent.ReadCacheAsync(localCache, cancellationToken).ConfigureAwait(false);
                if (!SameContent(cachedLocal, candidate.Local) || cachedLocal.Kind != kind)
                    throw Invalid("conflict-local-type-unverified");
                verified++;
                verificationBytesRead = checked(verificationBytesRead + cachedLocal.Size);
                await ReportAsync("verifying-artwork", verified, verificationTotal, verificationBytesRead, cancellationToken)
                    .ConfigureAwait(false);
                candidates[index] = candidates[index] with { Local = candidate.Local with { Kind = kind } };
                _localObjects[candidate.RelativePath] = candidates[index].Local!;
                _localCachePaths[candidate.RelativePath] = localCache;
            }
            if (candidate.Remote is not null)
            {
                var remoteCache = EnsureRemoteCachePath(binding, candidate.RelativePath);
                await DownloadRemoteCacheAsync(binding, candidate.RelativePath, candidate.Remote, remoteCache, cancellationToken).ConfigureAwait(false);
                var cachedRemote = await CloudConflictLocalContent.ReadCacheAsync(remoteCache, cancellationToken).ConfigureAwait(false);
                if (!SameContent(cachedRemote, candidate.Remote) || cachedRemote.Kind != kind)
                    throw Invalid("conflict-remote-type-unverified");
                verified++;
                verificationBytesRead = checked(verificationBytesRead + cachedRemote.Size);
                await ReportAsync("verifying-artwork", verified, verificationTotal, verificationBytesRead, cancellationToken)
                    .ConfigureAwait(false);
                candidates[index] = candidates[index] with { Remote = candidate.Remote with { Kind = kind } };
                _remoteCachePaths[candidate.RelativePath] = remoteCache;
            }
        }
        if (verified == 0)
            await ReportAsync("verifying-artwork", 0, verificationTotal, 0, cancellationToken).ConfigureAwait(false);

        var finalLocal = await CaptureLocalMetadataAsync(binding.LocalPath, cancellationToken).ConfigureAwait(false);
        if (!CloudConflictRecoveryPlanner.EqualDigest(finalLocal.SourceManifestSha256, binding.SourceManifestSha256)
            || !SameLocalIdentities(local.Files, finalLocal.Files)) throw Invalid("conflict-local-source-changed");

        var document = new CloudConflictInventoryDocument(binding, candidates.ToArray());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        if (bytes.Length > 128 * 1024 * 1024) throw Invalid("conflict-inventory-artifact-too-large");
        EnsureNoSymlinkPath(manifestPath);
        var digest = Hash(bytes);
        if (File.Exists(manifestPath))
        {
            var existing = await ReadBoundedFileAsync(manifestPath, 128 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
            if (!existing.AsSpan().SequenceEqual(bytes)) throw Invalid("conflict-inventory-changed");
        }
        else
        {
            await WriteCreateNewAsync(manifestPath, bytes, cancellationToken).ConfigureAwait(false);
        }

        _binding = binding;
        _observationInitialized = false;
        _allArchivesVerified = false;
        _observedPlan = null;
        _observedState = null;
        _observed = null;
        _localObjects.Clear();
        _activeObjects.Clear();
        _archiveObjects.Clear();
        foreach (var candidate in candidates)
        {
            if (candidate.Local is not null) _localObjects[candidate.RelativePath] = candidate.Local;
            if (candidate.Remote is not null) _activeObjects[candidate.RelativePath] = candidate.Remote;
        }
        return new(binding, new(manifestPath, digest, candidates.Count), candidates.ToArray());
    }

    private async Task<CloudConflictRecoveryObservation> ObserveCoreAsync(
        CloudConflictRecoveryPlan plan,
        CloudConflictRecoveryState state,
        CancellationToken cancellationToken)
    {
        if (!_observationInitialized || !ReferenceEquals(_observedPlan, plan)) ValidatePlan(plan);
        ValidateBinding(plan.Binding);
        if (state?.Items is null || state.PlanFingerprint != plan.Fingerprint || state.Items.Count != plan.Items.Count)
            throw Invalid("conflict-state-plan-mismatch");

        if (!_observationInitialized)
        {
            EnsureCache(plan.Binding);
            if (!_providerVerified)
            {
                await RequireJottacloudAsync(plan.Binding.RemoteName, cancellationToken).ConfigureAwait(false);
                _providerVerified = true;
            }
            await ValidateInventoryArtifactAsync(plan, cancellationToken).ConfigureAwait(false);
            var localMetadata = await CaptureLocalMetadataAsync(plan.Binding.LocalPath, cancellationToken).ConfigureAwait(false);
            if (!CloudConflictRecoveryPlanner.EqualDigest(localMetadata.SourceManifestSha256, plan.Binding.SourceManifestSha256))
                throw Invalid("conflict-local-source-changed");
            _localObjects.Clear();
            _activeObjects.Clear();
            _archiveObjects.Clear();
            for (var index = 0; index < plan.Items.Count; index++)
            {
                var item = plan.Items[index];
                var stage = state.Items[index].Stage;
                if (item.Local is not null)
                {
                    var observedLocal = await CloudConflictLocalContent.ObserveAsync(plan.Binding.LocalPath, item.RelativePath, cancellationToken).ConfigureAwait(false);
                    if (!SameContentAndIdentity(observedLocal, item.Local)) throw Invalid("conflict-local-source-changed");
                    _localObjects[item.RelativePath] = observedLocal;
                }
                var active = await ObserveRemoteFileAsync(plan.Binding, plan.Binding.ActiveRoot, item.RelativePath, cancellationToken).ConfigureAwait(false);
                ValidateActiveForStage(item, stage, active);
                SetActive(item.RelativePath, CanonicalActive(item, active));
                if (stage is CloudConflictItemStage.Archived or CloudConflictItemStage.MutationIntent or CloudConflictItemStage.Resolved)
                {
                    var archive = await ObserveRemoteFileAsync(plan.Binding, "", item.ArchivePath, cancellationToken).ConfigureAwait(false);
                    if (archive is null || !SameContent(archive, item.Remote)) throw Invalid("conflict-archive-verification-failed");
                    _archiveObjects[item.RelativePath] = archive;
                }
                else if (stage == CloudConflictItemStage.ArchiveIntent)
                {
                    await EnsureArchiveParentAsync(plan.Binding, item, cancellationToken).ConfigureAwait(false);
                    var archive = await ObserveRemoteFileAsync(plan.Binding, "", item.ArchivePath, cancellationToken).ConfigureAwait(false);
                    if (archive is not null)
                    {
                        if (!SameContent(archive, item.Remote)) throw Invalid("conflict-archive-target-occupied");
                        _archiveObjects[item.RelativePath] = archive;
                    }
                }
            }
            _observationInitialized = true;
        }

        var liveItems = new List<CloudConflictLiveItem>(plan.Items.Count);
        for (var i = 0; i < plan.Items.Count; i++)
        {
            var item = plan.Items[i];
            var stateItem = state.Items[i];
            if (stateItem is null) throw Invalid("conflict-state-plan-mismatch");
            var local = item.Local is null ? null : _localObjects.GetValueOrDefault(item.RelativePath);
            var active = _activeObjects.GetValueOrDefault(item.RelativePath);
            ValidateActiveForStage(item, stateItem.Stage, active);
            if ((stateItem.Stage is CloudConflictItemStage.Archived or CloudConflictItemStage.MutationIntent or CloudConflictItemStage.Resolved)
                && (!_archiveObjects.TryGetValue(item.RelativePath, out var verifiedArchive)
                    || !SameContent(verifiedArchive, item.Remote)))
                throw Invalid("conflict-archive-proof-missing");
            var archive = _archiveObjects.GetValueOrDefault(item.RelativePath)?.Sha256;
            CloudConflictMutationReceipt? receipt = null;
            if (stateItem.Stage == CloudConflictItemStage.MutationIntent && IsMutationTarget(item, active))
                receipt = MakeReceipt(plan, item, active);
            if (stateItem.Stage == CloudConflictItemStage.Resolved)
                receipt = stateItem.MutationReceipt ?? MakeReceipt(plan, item, active);
            liveItems.Add(new(local, active, archive, receipt));
        }

        _binding = plan.Binding;
        _observedPlan = plan;
        _observedState = state;
        _observed = new(plan.Binding, plan.Fingerprint, liveItems,
            new(CloudConflictProtectionKind.ExclusiveWriterLease, LeaseProof(plan)));
        return _observed;
    }

    private CloudConflictPlanItem RequireObservedIntent(
        CloudConflictRecoveryPlan plan,
        int index,
        CloudConflictItemStage expected)
    {
        if (!ReferenceEquals(_observedPlan, plan)) ValidatePlan(plan);
        else ValidateBinding(plan.Binding);
        if (_observedPlan is null || _observedState is null || _observed is null
            || _observedPlan.Fingerprint != plan.Fingerprint || _observedState.Items.Count != plan.Items.Count
            || index < 0 || index >= plan.Items.Count || _observedState.Items[index].Stage != expected
            || _observed.Binding != plan.Binding || _observed.Protection.Kind != CloudConflictProtectionKind.ExclusiveWriterLease
            || _writerPolicy != CloudConflictRecoverySession.CompanionOnlyWriter)
            throw Invalid("conflict-durable-intent-required");
        return plan.Items[index];
    }

    private void ValidateBinding(CloudConflictRecoveryBinding binding)
    {
        if (binding is null || binding.JobId != _lease.JobId
            || _writerPolicy != CloudConflictRecoverySession.CompanionOnlyWriter
            || !CloudConflictRecoveryPlanner.EqualDigest(
                _lease.PlanBindingSha256,
                CloudConflictRecoveryPlanner.BindingFingerprint(binding)))
            throw Invalid("conflict-writer-lease-binding-mismatch");
        if (_binding is not null && _binding != binding) throw Invalid("conflict-binding-changed");
        if (!Path.IsPathFullyQualified(binding.LocalPath) || binding.SourceManifestSha256 is null
            || !CloudConflictRecoveryPlanner.Digest(binding.SourceManifestSha256)
            || !CloudConflictRecoveryPlanner.Digest(CloudConflictRecoveryPlanner.BindingFingerprint(binding)))
            throw Invalid("conflict-binding-invalid");
        _ = RcloneCliHost.NormalizeRemoteName(binding.RemoteName);
        _ = RcloneCliHost.NormalizeRemotePath(binding.ActiveRoot);
        _ = RcloneCliHost.NormalizeRemotePath(binding.ArchiveRoot);
        if (binding.AttemptId.Length != 32 || !Guid.TryParseExact(binding.AttemptId, "N", out _))
            throw Invalid("conflict-binding-invalid");
    }

    private void ValidatePlan(CloudConflictRecoveryPlan plan)
    {
        if (plan is null || plan.Items is null || plan.Items.Count > CloudConflictRecoveryPlanner.MaxConflicts
            || !CloudConflictRecoveryPlanner.EqualDigest(plan.Fingerprint, CloudConflictRecoveryPlanner.Fingerprint(plan)))
            throw Invalid("conflict-plan-invalid");
        ValidateBinding(plan.Binding);
    }

    private async Task ValidateInventoryArtifactAsync(CloudConflictRecoveryPlan plan, CancellationToken cancellationToken)
    {
        var path = plan.Manifest.Path;
        EnsureNoSymlinkPath(path);
        var bytes = await ReadBoundedFileAsync(path, 128 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
        if (!CloudConflictRecoveryPlanner.EqualDigest(Hash(bytes), plan.Manifest.Sha256)) throw Invalid("conflict-inventory-hash-mismatch");
        var document = JsonSerializer.Deserialize<CloudConflictInventoryDocument>(bytes, JsonOptions)
            ?? throw Invalid("conflict-inventory-invalid");
        if (document.Binding != plan.Binding || document.Entries.Count != plan.Manifest.Count) throw Invalid("conflict-inventory-binding-mismatch");
        var rebuilt = CloudConflictRecoveryPlanner.Create(plan.Binding, new(plan.Binding, plan.Manifest, document.Entries));
        if (!CloudConflictRecoveryPlanner.EqualDigest(rebuilt.Fingerprint, plan.Fingerprint)) throw Invalid("conflict-plan-inventory-mismatch");
    }

    private async Task<LocalInventory> CaptureLocalInventoryAsync(string root, CancellationToken cancellationToken)
    {
        var metadata = await CaptureLocalMetadataAsync(root, cancellationToken).ConfigureAwait(false);
        var files = new Dictionary<string, LocalFile>(StringComparer.Ordinal);
        long bytesRead = 0;
        foreach (var entry in metadata.Files.Values.OrderBy(file => file.RelativePath, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateRelative(entry.RelativePath);
            var observed = await CloudConflictLocalContent.ObserveAsync(root, entry.RelativePath, cancellationToken).ConfigureAwait(false);
            if (observed.Size != entry.Size || observed.Identity != entry.Identity)
                throw Invalid("conflict-local-source-changed");
            files.Add(entry.RelativePath, new(observed, entry.Identity));
            bytesRead = checked(bytesRead + observed.Size);
            await ReportAsync("hashing-local", files.Count, metadata.Files.Count, bytesRead, cancellationToken)
                .ConfigureAwait(false);
        }
        if (files.Count == 0)
            await ReportAsync("hashing-local", 0, 0, 0, cancellationToken).ConfigureAwait(false);
        return new(files, metadata.SourceManifestSha256);
    }

    private Task ReportAsync(string phase, long files, long totalFiles, long bytesRead,
        CancellationToken cancellationToken)
        => ReportProgress?.Invoke(new(phase, files, totalFiles, bytesRead, DateTimeOffset.UtcNow), cancellationToken)
            ?? Task.CompletedTask;

    private async Task<LocalMetadata> CaptureLocalMetadataAsync(string root, CancellationToken cancellationToken)
    {
        // The shared Linux-only helper owns descriptor-relative traversal and rejects
        // links, non-regular files, unstable identities and unsupported file systems.
        var files = await CloudConflictLocalContent.CaptureMetadataAsync(root, cancellationToken).ConfigureAwait(false);
        var ordered = files.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in ordered)
        {
            Append(hash, file.RelativePath);
            Append(hash, file.Size.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(hash, file.LastWriteTimeUtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        var entries = ordered.Select(file => new LocalMetadataEntry(file.RelativePath, file.Size,
            file.LastWriteTimeUtcTicks, file.Identity));
        return new(entries.ToDictionary(file => file.RelativePath, StringComparer.Ordinal),
            Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private async Task<Dictionary<string, RemoteFile>> ListRemoteTreeAsync(
        CloudConflictRecoveryBinding binding,
        string root,
        CancellationToken cancellationToken)
    {
        var files = new Dictionary<string, RemoteFile>(StringComparer.Ordinal);
        var collision = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        var namespaceEntries = 0;
        pending.Push(string.Empty);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativeDirectory = pending.Pop();
            var remoteDirectory = JoinRemote(root, relativeDirectory);
            var spec = RcloneCliHost.BuildRemoteSpec(binding.RemoteName, remoteDirectory);
            var result = await RunAsync(["lsjson", spec, "--no-modtime", "--no-mimetype"], ListingTimeout, cancellationToken).ConfigureAwait(false);
            RequireSuccess(result, "conflict-remote-list-failed");
            if (Encoding.UTF8.GetByteCount(result.StandardOutput) > MaxListingBytes) throw Invalid("conflict-remote-list-too-large");
            using var document = JsonDocument.Parse(result.StandardOutput, new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw Invalid("conflict-remote-list-invalid");
            var perDirectory = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.ValueKind != JsonValueKind.Object) throw Invalid("conflict-remote-entry-invalid");
                var name = ReadString(entry, "Name") ?? ReadString(entry, "name");
                var path = ReadString(entry, "Path") ?? ReadString(entry, "path");
                var isDir = ReadBoolean(entry, "IsDir") ?? ReadBoolean(entry, "isDir");
                var size = ReadInt64(entry, "Size") ?? ReadInt64(entry, "size");
                if (string.IsNullOrEmpty(name) || path is not null && path != name || isDir is null
                    || name is "." or ".." || name.Contains('/') || name.Contains('\\') || name.Any(char.IsControl)
                    || name != name.Normalize(NormalizationForm.FormC) || !perDirectory.Add(name))
                    throw Invalid("conflict-remote-entry-invalid");
                var relative = relativeDirectory.Length == 0 ? name : relativeDirectory + "/" + name;
                ValidateRelative(relative);
                if (relative.Count(character => character == '/') > 128
                    || ++namespaceEntries > CloudConflictRecoveryPlanner.MaxCandidates)
                    throw Invalid("conflict-inventory-limit-exceeded");
                if (!collision.Add(relative)) throw Invalid("conflict-remote-path-collision");
                if (isDir.Value)
                {
                    pending.Push(relative);
                }
                else
                {
                    if (size is null or < 0) throw Invalid("conflict-remote-size-invalid");
                    files.Add(relative, new(relative, size.Value));
                    if (files.Count + pending.Count > CloudConflictRecoveryPlanner.MaxCandidates)
                        throw Invalid("conflict-inventory-limit-exceeded");
                }
            }
        }
        return files;
    }

    private async Task<CloudConflictObject?> ObserveRemoteFileAsync(
        CloudConflictRecoveryBinding binding,
        string root,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var fullPath = root.Length == 0 ? relativePath : JoinRemote(root, relativePath);
        var parent = Parent(fullPath);
        var name = Leaf(fullPath);
        var listing = await ReadRemoteDirectoryAsync(binding, parent, cancellationToken).ConfigureAwait(false);
        if (!listing.TryGetValue(name, out var entry)) return null;
        if (entry.IsDir || entry.Size is null) throw Invalid("conflict-remote-entry-invalid");
        return await HashRemoteFileAsync(binding, root, relativePath, cancellationToken, entry.Size).ConfigureAwait(false);
    }

    private async Task<CloudConflictObject> HashRemoteFileAsync(
        CloudConflictRecoveryBinding binding,
        string root,
        string relativePath,
        CancellationToken cancellationToken,
        long? knownSize = null)
    {
        var spec = RemoteSpec(binding, root, relativePath);
        var result = await RunAsync(["hashsum", "SHA256", spec, "--download"], ObjectTimeout, cancellationToken).ConfigureAwait(false);
        RequireSuccess(result, "conflict-remote-hash-failed");
        var lines = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length != 1 || lines[0].Length < 66 || !CloudConflictRecoveryPlanner.Digest(lines[0][..64])
            || lines[0][64] != ' ' || lines[0][65] != ' '
            || !string.Equals(lines[0][66..], Leaf(relativePath), StringComparison.Ordinal))
            throw Invalid("conflict-remote-hash-unrecognized");
        var sha = lines[0][..64].ToLowerInvariant();
        var size = knownSize ?? await GetRemoteSizeAsync(binding, root, relativePath, cancellationToken).ConfigureAwait(false);
        return MakeRemoteObject(size, sha);
    }

    private async Task<Dictionary<string, CloudConflictObject>> HashRemoteDirectoryAsync(
        CloudConflictRecoveryBinding binding, string root, string relativeDirectory,
        IReadOnlyList<RemoteFile> expectedFiles, CancellationToken cancellationToken)
    {
        var directory = relativeDirectory.Length == 0 ? root : JoinRemote(root, relativeDirectory);
        var result = await RunAsync(["hashsum", "SHA256", RcloneCliHost.BuildRemoteSpec(binding.RemoteName, directory),
            "--download", "--max-depth", "1"], ObjectTimeout, cancellationToken).ConfigureAwait(false);
        RequireSuccess(result, "conflict-remote-hash-failed");
        if (Encoding.UTF8.GetByteCount(result.StandardOutput) > MaxListingBytes)
            throw Invalid("conflict-remote-hash-output-too-large");

        var expected = expectedFiles.ToDictionary(file => Leaf(file.RelativePath), StringComparer.Ordinal);
        var observed = new Dictionary<string, CloudConflictObject>(StringComparer.Ordinal);
        var output = result.StandardOutput;
        var lines = output.Split('\n');
        var count = lines.Length - (output.EndsWith('\n') ? 1 : 0);
        if (count != expected.Count) throw Invalid("conflict-remote-hash-unrecognized");
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = lines[i];
            if (line.Length < 67 || !CloudConflictRecoveryPlanner.Digest(line[..64])
                || line[64] != ' ' || line[65] != ' ') throw Invalid("conflict-remote-hash-unrecognized");
            var name = line[66..];
            ValidateRelative(name);
            if (name.Contains('/') || !expected.TryGetValue(name, out var file)
                || observed.ContainsKey(file.RelativePath)) throw Invalid("conflict-remote-hash-unrecognized");
            observed.Add(file.RelativePath, MakeRemoteObject(file.Size, line[..64]));
        }
        return observed;
    }

    private static CloudConflictObject MakeRemoteObject(long size, string sha)
    {
        sha = sha.ToLowerInvariant();
        return new(size, sha, "sha256:" + sha, CloudConflictContentKind.Unknown);
    }

    private async Task<long> GetRemoteSizeAsync(CloudConflictRecoveryBinding binding, string root, string relativePath, CancellationToken cancellationToken)
    {
        var fullPath = root.Length == 0 ? relativePath : JoinRemote(root, relativePath);
        var listing = await ReadRemoteDirectoryAsync(binding, Parent(fullPath), cancellationToken).ConfigureAwait(false);
        if (!listing.TryGetValue(Leaf(fullPath), out var entry) || entry.IsDir || entry.Size is null)
            throw Invalid("conflict-remote-entry-invalid");
        return entry.Size.Value;
    }

    private async Task<Dictionary<string, RemoteDirectoryEntry>> ReadRemoteDirectoryAsync(
        CloudConflictRecoveryBinding binding, string directory, CancellationToken cancellationToken)
    {
        var listing = await RunAsync(["lsjson", RcloneCliHost.BuildRemoteSpec(binding.RemoteName, directory),
            "--no-modtime", "--no-mimetype"], ListingTimeout, cancellationToken).ConfigureAwait(false);
        RequireSuccess(listing, "conflict-remote-list-failed");
        if (Encoding.UTF8.GetByteCount(listing.StandardOutput) > MaxListingBytes)
            throw Invalid("conflict-remote-list-too-large");
        using var document = JsonDocument.Parse(listing.StandardOutput, new JsonDocumentOptions { MaxDepth = 8 });
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw Invalid("conflict-remote-list-invalid");
        var entries = new Dictionary<string, RemoteDirectoryEntry>(StringComparer.Ordinal);
        var collision = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in document.RootElement.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Object) throw Invalid("conflict-remote-entry-invalid");
            var name = ReadString(value, "Name") ?? ReadString(value, "name");
            var path = ReadString(value, "Path") ?? ReadString(value, "path");
            var isDir = ReadBoolean(value, "IsDir") ?? ReadBoolean(value, "isDir");
            var size = ReadInt64(value, "Size") ?? ReadInt64(value, "size");
            if (string.IsNullOrEmpty(name) || path is not null && path != name || isDir is null
                || !collision.Add(name) || (!isDir.Value && size is null or < 0))
                throw Invalid("conflict-remote-entry-invalid");
            ValidateRelative(name);
            entries.Add(name, new(isDir.Value, size));
        }
        return entries;
    }

    private async Task DownloadRemoteCacheAsync(CloudConflictRecoveryBinding binding, string relativePath,
        CloudConflictObject expected, string cachePath, CancellationToken cancellationToken)
    {
        EnsureNoSymlinkPath(cachePath);
        if (File.Exists(cachePath))
        {
            var cached = await CloudConflictLocalContent.ReadCacheAsync(cachePath, cancellationToken).ConfigureAwait(false);
            if (!SameContent(cached, expected)) throw Invalid("conflict-remote-cache-mismatch");
            return;
        }
        await RunSuccessAsync(
            ["copyto", RemoteSpec(binding, binding.ActiveRoot, relativePath), cachePath, "--immutable", "--checksum"],
            ObjectTimeout,
            cancellationToken).ConfigureAwait(false);
        var downloaded = await CloudConflictLocalContent.ReadCacheAsync(cachePath, cancellationToken).ConfigureAwait(false);
        if (!SameContent(downloaded, expected)) throw Invalid("conflict-remote-cache-mismatch");
    }

    private async Task RequireJottacloudAsync(string remoteName, CancellationToken cancellationToken)
    {
        var result = await RunAsync(["listremotes", "--long", "--json"], ListingTimeout, cancellationToken).ConfigureAwait(false);
        RequireSuccess(result, "conflict-remote-config-unavailable");
        if (!RcloneCliHost.TryParseRemotes(result.StandardOutput, out var remotes)) throw Invalid("conflict-remote-config-unavailable");
        var remote = remotes.SingleOrDefault(item => string.Equals(item.Name, RcloneCliHost.NormalizeRemoteName(remoteName), StringComparison.OrdinalIgnoreCase));
        if (remote?.Type is null || !string.Equals(remote.Type, "jottacloud", StringComparison.OrdinalIgnoreCase))
            throw Invalid("conflict-provider-unsupported");
    }

    private async Task RunSuccessAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var result = await RunAsync(arguments, timeout, cancellationToken).ConfigureAwait(false);
        RequireSuccess(result, "conflict-provider-operation-failed");
    }

    private Task<RcloneCommandResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (arguments.Count == 0 || arguments.Any(arg => arg.Contains('\0') || arg.Contains('\r') || arg.Contains('\n')))
            throw Invalid("conflict-command-arguments-invalid");
        return _runner.RunAsync(_executable, arguments, timeout, cancellationToken);
    }

    private static void RequireSuccess(RcloneCommandResult result, string error)
    {
        if (!result.StartSucceeded || result.ExitCode != 0 || result.TimedOut || result.OutputTruncated || result.ErrorCode is not null)
            throw Invalid(error);
    }

    private void ValidateAllArchives(CloudConflictRecoveryPlan plan)
    {
        if (plan.Items.Any(item => !_archiveObjects.TryGetValue(item.RelativePath, out var archived) || !SameContent(archived, item.Remote)))
            throw Invalid("conflict-archive-set-incomplete");
    }

    private async Task EnsureArchiveParentAsync(CloudConflictRecoveryBinding binding,
        CloudConflictPlanItem item, CancellationToken cancellationToken)
    {
        var parts = SplitArchivePath(binding, item.ArchivePath);
        await RunSuccessAsync(["mkdir", RcloneCliHost.BuildRemoteSpec(binding.RemoteName, parts.Parent)],
            ListingTimeout, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateActiveForStage(CloudConflictPlanItem item, CloudConflictItemStage stage,
        CloudConflictObject? active)
    {
        var sourceMatches = active is not null && SameContent(active, item.Remote);
        var targetMatches = IsMutationTarget(item, active);
        if (stage == CloudConflictItemStage.Resolved ? !targetMatches
            : stage == CloudConflictItemStage.MutationIntent ? !sourceMatches && !targetMatches
            : !sourceMatches)
            throw Invalid("conflict-active-object-changed");
    }

    private void SetActive(string relativePath, CloudConflictObject? active)
    {
        if (active is null) _activeObjects.Remove(relativePath);
        else _activeObjects[relativePath] = active;
    }

    private static CloudConflictObject? CanonicalActive(CloudConflictPlanItem item, CloudConflictObject? active)
    {
        if (active is null) return null;
        // The plan's content kind was verified from bytes during capture. A
        // matching digest under the same writer fence retains that evidence.
        if (SameContent(active, item.Remote)) return active with { Kind = item.Remote.Kind };
        if (item.Local is not null && SameContent(active, item.Local)) return active with { Kind = item.Local.Kind };
        return active;
    }

    private string EnsureLocalCachePath(CloudConflictRecoveryBinding binding, string relativePath)
        => CachePath(binding, "local", relativePath);
    private string EnsureRemoteCachePath(CloudConflictRecoveryBinding binding, string relativePath)
        => CachePath(binding, "remote", relativePath);

    private string CachePath(CloudConflictRecoveryBinding binding, string side, string relativePath)
    {
        var directory = EnsureCache(binding);
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(side + "\0" + relativePath))) + ".blob";
        return Path.Combine(directory, key);
    }

    private string EnsureCache(CloudConflictRecoveryBinding binding)
    {
        var fullLocal = Path.TrimEndingDirectorySeparator(Path.GetFullPath(binding.LocalPath));
        var jobDirectory = Path.Combine(_cacheRoot, "cloud-conflict-recovery", binding.JobId, binding.AttemptId);
        var fullCache = Path.TrimEndingDirectorySeparator(Path.GetFullPath(jobDirectory));
        if (IsWithin(fullLocal, fullCache) || IsWithin(fullCache, fullLocal)) throw Invalid("conflict-cache-overlaps-source");
        EnsureNoSymlinkPath(fullCache);
        Directory.CreateDirectory(fullCache);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(fullCache, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var mode = File.GetUnixFileMode(fullCache);
            if ((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
                throw Invalid("conflict-cache-not-private");
        }
        return fullCache;
    }

    private async Task EnsureCacheSpaceAsync(long bytesRequired, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (bytesRequired < 0 || bytesRequired > MaxCachedBytes) throw Invalid("conflict-cache-budget-exceeded");
        var cachePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_cacheRoot));
        var drive = DriveInfo.GetDrives()
            .Where(candidate => IsWithin(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate.RootDirectory.FullName)), cachePath))
            .OrderByDescending(candidate => candidate.RootDirectory.FullName.Length)
            .FirstOrDefault() ?? throw Invalid("conflict-cache-root-invalid");
        if (!drive.IsReady || drive.AvailableFreeSpace < checked(bytesRequired + CacheReserveBytes))
            throw Invalid("conflict-cache-space-insufficient");
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private string LeaseProof(CloudConflictRecoveryPlan plan)
    {
        var material = $"exclusive-writer\0{plan.Fingerprint}\0{plan.Binding.JobId}\0" +
            $"{CloudConflictRecoveryPlanner.BindingFingerprint(plan.Binding)}\0{_lease.SessionId:N}\0{_lease.Generation}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private CloudConflictMutationReceipt MakeReceipt(CloudConflictRecoveryPlan plan, CloudConflictPlanItem item, CloudConflictObject? active)
    {
        var targetSha = item.Operation == CloudConflictOperation.RemoveRemoteOnly ? null : item.Local?.Sha256;
        var material = $"{_lease.SessionId:N}\0{_lease.Generation}\0{plan.Fingerprint}\0{item.RelativePath}\0{item.Operation}\0{targetSha ?? "deleted"}";
        return new(plan.Fingerprint, item.RelativePath, item.Operation, targetSha,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))));
    }

    private static bool IsMutationTarget(CloudConflictPlanItem item, CloudConflictObject? active)
        => item.Operation == CloudConflictOperation.RemoveRemoteOnly ? active is null
            : item.Local is not null && active is not null && SameContent(active, item.Local);

    private static bool SameContent(CloudConflictObject left, CloudConflictObject right)
        => left.Size == right.Size && CloudConflictRecoveryPlanner.EqualDigest(left.Sha256, right.Sha256);
    private static bool SameContentAndIdentity(CloudConflictObject left, CloudConflictObject right)
        => SameContent(left, right) && string.Equals(left.Identity, right.Identity, StringComparison.Ordinal);

    private static bool SameLocalIdentities(IReadOnlyDictionary<string, LocalFile> before,
        IReadOnlyDictionary<string, LocalMetadataEntry> after)
        => before.Count == after.Count && before.All(pair => after.TryGetValue(pair.Key, out var value)
            && pair.Value.Identity == value.Identity && pair.Value.Object.Size == value.Size);

    private static CloudConflictContentKind ExpectedKind(string relativePath)
        => Path.GetExtension(relativePath).ToLowerInvariant() switch
        {
            ".nfo" => CloudConflictContentKind.XmlMetadata,
            ".jpg" or ".jpeg" => CloudConflictContentKind.Jpeg,
            ".png" => CloudConflictContentKind.Png,
            _ => CloudConflictContentKind.Unknown,
        };

    private static void ValidateRelative(string path)
    {
        if (path.Length is 0 or > 4096 || path != path.Normalize(NormalizationForm.FormC)
            || path.Contains('\\') || path.Contains(':') || path.Any(char.IsControl)
            || path.Split('/').Any(part => part.Length == 0 || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.')))
            throw Invalid("conflict-path-invalid");
    }

    private static (string Parent, string Leaf) SplitArchivePath(CloudConflictRecoveryBinding binding, string archivePath)
    {
        var expected = binding.ArchiveRoot + "/";
        if (!archivePath.StartsWith(expected, StringComparison.Ordinal)) throw Invalid("conflict-archive-path-invalid");
        var relative = archivePath[expected.Length..];
        ValidateRelative(relative);
        return (Parent(archivePath), Leaf(archivePath));
    }

    private static string RemoteSpec(CloudConflictRecoveryBinding binding, string root, string relative)
        => RcloneCliHost.BuildRemoteSpec(binding.RemoteName, JoinRemote(root, relative));
    private static string JoinRemote(string root, string relative)
    {
        var normalizedRoot = RcloneCliHost.NormalizeRemotePath(root);
        if (relative.Length == 0) return normalizedRoot;
        ValidateRelative(relative);
        return normalizedRoot.Length == 0 ? relative : normalizedRoot + "/" + relative;
    }
    private static string Parent(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? string.Empty : path[..index];
    }
    private static string Leaf(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? path : path[(index + 1)..];
    }

    private static string? ReadString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool? ReadBoolean(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
    private static long? ReadInt64(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var parsed) ? parsed : null;

    private static void Append(IncrementalHash hash, string value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData([0]);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static bool IsWithin(string root, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return path.Equals(root, comparison) || path.StartsWith(
            root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar, comparison);
    }

    private static void EnsureNoSymlinkPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? throw Invalid("conflict-path-invalid");
        var current = root;
        foreach (var segment in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (File.Exists(current) || Directory.Exists(current) || new FileInfo(current).LinkTarget is not null)
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw Invalid("conflict-path-link-unsupported");
            }
        }
    }

    private static async Task<byte[]> ReadBoundedFileAsync(string path, int maxBytes, CancellationToken cancellationToken)
    {
        EnsureNoSymlinkPath(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        if (stream.Length < 0 || stream.Length > maxBytes) throw Invalid("conflict-artifact-too-large");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    private static async Task WriteCreateNewAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(path)) ?? throw Invalid("conflict-manifest-path-invalid");
        Directory.CreateDirectory(parent);
        EnsureNoSymlinkPath(path);
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 64 * 1024,
            Options = FileOptions.Asynchronous,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var stream = new FileStream(path, options);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
        CloudMigrationJournalLease.FlushDirectory(parent);
    }

    private static InvalidOperationException Invalid(string code) => new(code);

    private sealed record LocalFile(CloudConflictObject Object, string Identity);
    private sealed record LocalInventory(Dictionary<string, LocalFile> Files, string SourceManifestSha256);
    private sealed record LocalMetadata(IReadOnlyDictionary<string, LocalMetadataEntry> Files, string SourceManifestSha256);
    private sealed record LocalMetadataEntry(string RelativePath, long Size, long LastWriteTimeUtcTicks, string Identity);
    private sealed record RemoteFile(string RelativePath, long Size);
    private sealed record RemoteDirectoryEntry(bool IsDir, long? Size);
}
