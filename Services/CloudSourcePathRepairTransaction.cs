using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.AudioGateway.Models;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>
/// Adapter for one existing migration job under its whole-job lease and the persistent stopped-service
/// barrier. Only trusted host code produces observations. Exclusion and the exact revision, plan,
/// archive, mount, path identities, and action must be rechecked at each effect boundary.
/// </summary>
public interface ICloudSourcePathRepairOperations
{
    /// <summary>Read the repair state and revision of the entire authoritative job.</summary>
    Task<CloudSourcePathRepairSnapshot?> ReadAsync(string jobId, CancellationToken cancellationToken);

    /// <summary>
    /// Observe exact live custody under the held lease without changing state. Verification receipts
    /// must describe this snapshot and its freshly verified local namespace and content.
    /// </summary>
    Task<CloudSourcePathRepairObservation> ObserveAsync(CloudSourcePathRepairSnapshot snapshot, CancellationToken cancellationToken);

    /// <summary>Durably replace only the repair state by compare-exchange on the entire job revision.</summary>
    Task<CloudSourcePathRepairSnapshot?> CompareExchangeAsync(CloudSourcePathRepairSnapshot expected,
        CloudSourcePathRepairState replacement, CancellationToken cancellationToken);

    /// <summary>
    /// Perform exactly the admitted effect under the lease. Preservation is immutable and can resume
    /// only an owned archive. Move and restore require atomic no-replace and durable directory
    /// synchronization. Verification reads the complete namespace and all bound preservation content.
    /// The adapter may neither delete data nor change the job, cloud, active baseline, or VFS state here.
    /// </summary>
    Task ExecuteAsync(CloudSourcePathRepairSnapshot expected, CloudSourcePathRepairDecision decision, CancellationToken cancellationToken);
}

/// <summary>Pure fail-closed transitions and a bounded runner for local source path repair.</summary>
public static class CloudSourcePathRepairTransaction
{
    /// <summary>Maximum number of independent ordered path moves in one repair.</summary>
    public const int MaxMoves = 32;

    private static readonly JsonSerializerOptions FingerprintJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Hash the schema-1 typed plan, including its exact order and spellings. The format is
    /// compact web-default JSON of the plan record with an empty fingerprint field.
    /// </summary>
    public static string Fingerprint(CloudSourcePathRepairPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Convert.ToHexStringLower(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(plan with { Fingerprint = string.Empty }, FingerprintJson)));
    }

    /// <summary>A valid durable terminal receipt is custody evidence only and grants no migration authority.</summary>
    public static bool HasStableReceipt(CloudSourcePathRepairState? state)
        => ValidState(state) && (state!.Phase == CloudSourcePathRepairPhase.RolledBack
            || state.Phase == CloudSourcePathRepairPhase.Repaired && !state.RollbackRequested);

    /// <summary>Release ordinary writers only for a stable receipt bound to this exact job and destination.</summary>
    public static bool OwnsJob(CloudMigrationJob job)
    {
        if (job.SourcePathRepair is null) return false;
        if (job.Direction != CloudMigrationDirections.LocalToVfs || !HasStableReceipt(job.SourcePathRepair)) return true;
        var binding = job.SourcePathRepair.Plan.Binding;
        return binding.JobId != job.Id || binding.ProfileId != job.ProfileId
            || binding.LibraryName != job.LibraryName || binding.LocalRoot != job.LocalPath
            || binding.RemoteName != job.RemoteName || binding.RemotePath != job.RemotePath;
    }

    /// <summary>Advance at most one bounded transaction under an externally held exclusive lease.</summary>
    public static async Task<CloudSourcePathRepairDecision> RunAsync(string jobId, ICloudSourcePathRepairOperations operations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operations);
        var current = await operations.ReadAsync(jobId, cancellationToken).ConfigureAwait(false);
        string? lastEffectRevision = null;
        for (var step = 0; step < MaxMoves * 4 + 16; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ValidSnapshot(current) || current!.State.Plan.Binding.JobId != jobId) return Block();
            var observation = await operations.ObserveAsync(current, cancellationToken).ConfigureAwait(false);
            var decision = Decide(current, observation);
            if (decision.Action == CloudSourcePathRepairAction.Blocked) return decision;

            var live = await operations.ReadAsync(jobId, cancellationToken).ConfigureAwait(false);
            if (!SameSnapshot(current, live)) return Block("source-repair-job-revision-changed");
            if (decision.Action == CloudSourcePathRepairAction.Complete) return decision;
            if (decision.Action == CloudSourcePathRepairAction.Persist)
            {
                var next = await operations.CompareExchangeAsync(current, decision.NextState!, cancellationToken).ConfigureAwait(false);
                if (!ValidSnapshot(next) || next!.Revision == current.Revision || !SameState(decision.NextState!, next.State))
                    return Block("source-repair-job-revision-changed");
                current = next;
                continue;
            }

            // A successful return is not an effect receipt. Require a new observation and
            // advance the journal before issuing another effect at the same revision.
            if (lastEffectRevision == current.Revision) return Block("source-repair-effect-not-observed");
            await operations.ExecuteAsync(current, decision, cancellationToken).ConfigureAwait(false);
            lastEffectRevision = current.Revision;
            var after = await operations.ReadAsync(jobId, cancellationToken).ConfigureAwait(false);
            if (!SameSnapshot(current, after)) return Block("source-repair-job-revision-changed");
        }
        return Block("source-repair-step-limit");
    }

    /// <summary>Decide one journal transition or effect from a fresh observation bound to this snapshot.</summary>
    public static CloudSourcePathRepairDecision Decide(CloudSourcePathRepairSnapshot? snapshot,
        CloudSourcePathRepairObservation? observation)
    {
        if (!ValidSnapshot(snapshot) || observation is not { LeaseHeld: true, BindingsMatch: true,
            StartupBarrierHeld: true, NoReplaceSupported: true } || observation.JobRevision != snapshot!.Revision)
            return Block();

        var state = snapshot.State;
        var plan = state.Plan;
        if (!SameDigest(plan.Fingerprint, observation.PlanFingerprint)
            || !ValidArchive(state, observation.Archive)
            || observation.Verification is { } proof && !VerificationMatches(plan, proof, proof.Kind)
            || !TryObservePrefix(state, observation, out var prefix)) return Block();

        switch (state.Phase)
        {
            case CloudSourcePathRepairPhase.Prepared when observation.Archive.State == CloudSourcePathRepairArchiveState.Missing:
                return Persist(state, state.RollbackRequested ? CloudSourcePathRepairPhase.VerifyOriginalIntent
                    : CloudSourcePathRepairPhase.PreservationIntent, 0);
            case CloudSourcePathRepairPhase.PreservationIntent:
                return DecidePreservation(state, observation.Archive);
            case CloudSourcePathRepairPhase.Moving:
                if (state.RollbackRequested) return BeginRestore(state, prefix);
                return Persist(state, prefix == plan.Moves.Length ? CloudSourcePathRepairPhase.VerifyRepairedIntent
                    : CloudSourcePathRepairPhase.MoveIntent, prefix);
            case CloudSourcePathRepairPhase.MoveIntent:
                if (state.RollbackRequested) return BeginRestore(state, prefix);
                return prefix == state.Cursor ? Act(CloudSourcePathRepairAction.Move, state.Cursor)
                    : Persist(state, CloudSourcePathRepairPhase.Moving, prefix);
            case CloudSourcePathRepairPhase.VerifyRepairedIntent:
                if (state.RollbackRequested) return BeginRestore(state, prefix);
                return DecideVerification(state, observation.Verification, CloudSourcePathRepairVerificationKind.Repaired);
            case CloudSourcePathRepairPhase.Repaired:
                if (state.RollbackRequested) return BeginRestore(state, prefix);
                return ObserveTerminal(state, observation.Verification, CloudSourcePathRepairVerificationKind.Repaired);
            case CloudSourcePathRepairPhase.Restoring:
                return Persist(state, prefix == 0 ? CloudSourcePathRepairPhase.VerifyOriginalIntent
                    : CloudSourcePathRepairPhase.RestoreIntent, prefix);
            case CloudSourcePathRepairPhase.RestoreIntent:
                return prefix == state.Cursor ? Act(CloudSourcePathRepairAction.Restore, state.Cursor - 1)
                    : Persist(state, CloudSourcePathRepairPhase.Restoring, prefix);
            case CloudSourcePathRepairPhase.VerifyOriginalIntent:
                return DecideVerification(state, observation.Verification, CloudSourcePathRepairVerificationKind.Original);
            case CloudSourcePathRepairPhase.RolledBack:
                return ObserveTerminal(state, observation.Verification, CloudSourcePathRepairVerificationKind.Original);
            default:
                return Block();
        }
    }

    private static CloudSourcePathRepairDecision DecidePreservation(CloudSourcePathRepairState state,
        CloudSourcePathRepairArchiveObservation archive)
    {
        if (archive.State == CloudSourcePathRepairArchiveState.Verified)
        {
            state = state with { Preservation = new(state.Plan.Fingerprint, state.Plan.PreservationManifestSha256, archive.Identity!) };
            return Persist(state, state.RollbackRequested ? CloudSourcePathRepairPhase.VerifyOriginalIntent
                : CloudSourcePathRepairPhase.Moving, 0);
        }
        return state.RollbackRequested ? Persist(state, CloudSourcePathRepairPhase.VerifyOriginalIntent, 0)
            : Act(CloudSourcePathRepairAction.Preserve);
    }

    private static CloudSourcePathRepairDecision BeginRestore(CloudSourcePathRepairState state, int prefix)
        => Persist(state with { Verification = null }, CloudSourcePathRepairPhase.Restoring, prefix);

    private static CloudSourcePathRepairDecision DecideVerification(CloudSourcePathRepairState state,
        CloudSourcePathRepairVerification? proof, CloudSourcePathRepairVerificationKind kind)
    {
        if (proof is null) return Act(VerificationAction(kind));
        if (!VerificationMatches(state.Plan, proof, kind)) return Block();
        return Persist(state with { Verification = proof }, kind == CloudSourcePathRepairVerificationKind.Repaired
            ? CloudSourcePathRepairPhase.Repaired : CloudSourcePathRepairPhase.RolledBack, state.Cursor);
    }

    private static CloudSourcePathRepairDecision ObserveTerminal(CloudSourcePathRepairState state,
        CloudSourcePathRepairVerification? proof, CloudSourcePathRepairVerificationKind kind)
    {
        // Verification is read-only and may need to be repeated after a restart.
        if (proof is null) return Act(VerificationAction(kind));
        if (!VerificationMatches(state.Plan, proof, kind)) return Block();
        return Act(CloudSourcePathRepairAction.Complete);
    }

    private static CloudSourcePathRepairAction VerificationAction(CloudSourcePathRepairVerificationKind kind)
        => kind == CloudSourcePathRepairVerificationKind.Repaired ? CloudSourcePathRepairAction.VerifyRepaired
            : CloudSourcePathRepairAction.VerifyOriginal;

    private static bool TryObservePrefix(CloudSourcePathRepairState state, CloudSourcePathRepairObservation observation,
        out int prefix)
    {
        prefix = 0;
        if (observation.Paths.IsDefault || observation.Paths.Length != state.Plan.Moves.Length) return false;
        foreach (var path in observation.Paths)
        {
            if (path is { Source: CloudSourcePathRepairPathState.Missing, Destination: CloudSourcePathRepairPathState.Expected })
                prefix++;
            else break;
        }
        for (var index = prefix; index < observation.Paths.Length; index++)
        {
            if (observation.Paths[index] is not { Source: CloudSourcePathRepairPathState.Expected,
                 Destination: CloudSourcePathRepairPathState.Missing }) return false;
        }
        return prefix == state.Cursor
            || state.Phase == CloudSourcePathRepairPhase.MoveIntent && prefix == state.Cursor + 1
            || state.Phase == CloudSourcePathRepairPhase.RestoreIntent && prefix == state.Cursor - 1;
    }

    private static bool ValidSnapshot(CloudSourcePathRepairSnapshot? snapshot)
        => snapshot is not null && !string.IsNullOrWhiteSpace(snapshot.Revision) && ValidState(snapshot.State);

    private static bool SameSnapshot(CloudSourcePathRepairSnapshot previous, CloudSourcePathRepairSnapshot? current)
        => current is not null && previous.Revision == current.Revision && ValidSnapshot(current)
            && SameState(previous.State, current.State);

    private static bool SameState(CloudSourcePathRepairState left, CloudSourcePathRepairState right)
        => SameDigest(left.Plan.Fingerprint, right.Plan.Fingerprint) && left.Phase == right.Phase && left.Cursor == right.Cursor
            && left.RollbackRequested == right.RollbackRequested && left.Preservation == right.Preservation
            && left.Verification == right.Verification;

    private static bool ValidState(CloudSourcePathRepairState? state)
    {
        if (state is null || !ValidPlan(state.Plan) || state.Cursor < 0 || state.Cursor > state.Plan.Moves.Length
            || state.Preservation is { } receipt && !PreservationMatches(state.Plan, receipt)) return false;
        var total = state.Plan.Moves.Length;
        var preserving = state.Preservation is not null;
        var verifying = state.Verification is not null;

        return state.Phase switch
        {
            CloudSourcePathRepairPhase.Prepared or CloudSourcePathRepairPhase.PreservationIntent
                => state.Cursor == 0 && !preserving && !verifying,
            CloudSourcePathRepairPhase.Moving => preserving && !verifying,
            CloudSourcePathRepairPhase.MoveIntent => state.Cursor < total && preserving && !verifying,
            CloudSourcePathRepairPhase.VerifyRepairedIntent => state.Cursor == total && preserving && !verifying,
            CloudSourcePathRepairPhase.Repaired => state.Cursor == total && preserving
                && VerificationMatches(state.Plan, state.Verification, CloudSourcePathRepairVerificationKind.Repaired),
            CloudSourcePathRepairPhase.Restoring => state.RollbackRequested && (state.Cursor == 0 || preserving) && !verifying,
            CloudSourcePathRepairPhase.RestoreIntent => state.RollbackRequested && state.Cursor > 0 && preserving && !verifying,
            CloudSourcePathRepairPhase.VerifyOriginalIntent => state.RollbackRequested && state.Cursor == 0 && !verifying,
            CloudSourcePathRepairPhase.RolledBack => state.RollbackRequested && state.Cursor == 0
                && VerificationMatches(state.Plan, state.Verification, CloudSourcePathRepairVerificationKind.Original),
            _ => false,
        };
    }

    private static bool ValidArchive(CloudSourcePathRepairState state, CloudSourcePathRepairArchiveObservation? archive)
    {
        if (archive is null) return false;
        if (state.Preservation is { } receipt)
            return archive.State == CloudSourcePathRepairArchiveState.Verified && archive.Identity == receipt.ArchiveIdentity
                && SameDigest(archive.ManifestSha256, receipt.ManifestSha256);
        return archive.State switch
        {
            CloudSourcePathRepairArchiveState.Missing => archive.Identity is null && archive.ManifestSha256 is null,
            CloudSourcePathRepairArchiveState.Resumable => ValidLabel(archive.Identity) && archive.ManifestSha256 is null,
            CloudSourcePathRepairArchiveState.Verified => state.Phase == CloudSourcePathRepairPhase.PreservationIntent
                && ValidLabel(archive.Identity) && SameDigest(archive.ManifestSha256, state.Plan.PreservationManifestSha256),
            _ => false,
        };
    }

    private static bool PreservationMatches(CloudSourcePathRepairPlan plan, CloudSourcePathRepairPreservation receipt)
        => SameDigest(receipt.PlanFingerprint, plan.Fingerprint)
            && SameDigest(receipt.ManifestSha256, plan.PreservationManifestSha256) && ValidLabel(receipt.ArchiveIdentity);

    private static bool VerificationMatches(CloudSourcePathRepairPlan plan, CloudSourcePathRepairVerification? proof,
        CloudSourcePathRepairVerificationKind kind)
    {
        if (proof is null || proof.Kind != kind || !SameDigest(proof.PlanFingerprint, plan.Fingerprint)
            || !SameDigest(proof.PreservationManifestSha256, plan.PreservationManifestSha256)) return false;
        return kind switch
        {
            CloudSourcePathRepairVerificationKind.Original => SameDigest(proof.NamespaceSha256, plan.Binding.Source.NamespaceSha256),
            CloudSourcePathRepairVerificationKind.Repaired => SameDigest(proof.NamespaceSha256, plan.TargetNamespaceSha256),
            _ => false,
        };
    }

    internal static bool ValidPlan(CloudSourcePathRepairPlan? plan)
    {
        if (plan is null || plan.SchemaVersion != 1 || plan.Binding is not { } binding
            || !Guid.TryParseExact(binding.JobId, "N", out var jobId) || jobId == Guid.Empty
            || binding.RepairId == Guid.Empty || binding.LibraryId == Guid.Empty || !ValidLabel(binding.LibraryName)
            || !ValidLabel(binding.ProfileId) || binding.LocalRoot is not { Length: > 1 } root || root[0] != '/'
            || !ValidRelative(root[1..]) || !ValidRemoteName(binding.RemoteName) || !ValidRelative(binding.RemotePath)
            || binding.RootIdentity.Inode == 0 || binding.RootIdentity.MountId <= 0
            || !IsDigest(binding.HostSpecificationSha256) || binding.Source is not { } source
            || source.ManifestVersion != CloudMigrationManifestBuilder.CurrentVersion || source.FileCount < 0 || source.TotalBytes < 0
            || !IsDigest(source.ManifestSha256) || !IsDigest(source.NamespaceSha256)
            || plan.Moves.IsDefaultOrEmpty || plan.Moves.Length > MaxMoves || !IsDigest(plan.PreservationManifestSha256)
            || !IsDigest(plan.TargetNamespaceSha256) || !IsDigest(plan.Fingerprint)
            || SameDigest(plan.TargetNamespaceSha256, source.NamespaceSha256)) return false;

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var move in plan.Moves)
        {
            if (move is null || move.Kind is not (CloudSourcePathRepairKind.File or CloudSourcePathRepairKind.Directory)
                || !ValidRelative(move.Source) || !ValidRelative(move.Destination)
                || Parent(move.Source) != Parent(move.Destination)
                || move.Identity.Inode == 0 || move.Identity.Inode == binding.RootIdentity.Inode
                || move.Identity.Device != binding.RootIdentity.Device || move.Identity.MountId != binding.RootIdentity.MountId
                || !paths.Add(move.Source) || !paths.Add(move.Destination)) return false;
        }
        foreach (var path in paths)
            for (var slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
                if (paths.Contains(path[..slash])) return false;
        return SameDigest(plan.Fingerprint, Fingerprint(plan));
    }

    private static bool ValidLabel(string? value) => value is { Length: > 0 and <= 1024 }
        && !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);

    private static bool ValidRemoteName(string? value)
        => value is not null && ValidLabel(value) && !value.Any(ch => ch is ':' or '/' or '\\') && value == value.Trim();

    private static bool ValidRelative(string? value)
    {
        if (value is not { Length: > 0 and <= 4096 } || value.Any(ch => ch is ':' or '\\' || char.IsControl(ch))) return false;
        try { if (!value.IsNormalized(NormalizationForm.FormC)) return false; }
        catch (ArgumentException) { return false; }
        return value.Split('/').All(part => part.Length > 0 && part is not ("." or "..") && !part.EndsWith('.') && !part.EndsWith(' '));
    }

    private static string Parent(string value) => value[..Math.Max(0, value.LastIndexOf('/'))];
    private static bool IsDigest(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool SameDigest(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static CloudSourcePathRepairDecision Persist(CloudSourcePathRepairState state, CloudSourcePathRepairPhase phase, int cursor)
        => new(CloudSourcePathRepairAction.Persist, "source-repair-journal-before-next-step", state with { Phase = phase, Cursor = cursor });
    private static CloudSourcePathRepairDecision Act(CloudSourcePathRepairAction action, int? index = null)
        => new(action, "source-repair-step-ready", MoveIndex: index);
    private static CloudSourcePathRepairDecision Block(string code = "source-repair-custody-ambiguous")
        => new(CloudSourcePathRepairAction.Blocked, code);
}
