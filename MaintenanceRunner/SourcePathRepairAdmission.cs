using System.Text.Json;
using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;

namespace Slipmat.MaintenanceRunner;

/// <summary>Admits one reviewed repair against the exact failed migration while Jellyfin is masked and stopped.</summary>
internal sealed class SourcePathRepairAdmission(CloudMigrationJournalLease lease, LinuxMaintenanceBarrier barrier, string specificationPath)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    internal async Task CheckAsync(string jobId, string approvalPath, CancellationToken cancellationToken)
        => _ = await ValidateAsync(jobId, approvalPath, cancellationToken).ConfigureAwait(false);

    internal async Task<CloudSourcePathRepairSnapshot> PrepareAsync(string jobId, string approvalPath,
        CancellationToken cancellationToken)
    {
        var (target, plan) = await ValidateAsync(jobId, approvalPath, cancellationToken).ConfigureAwait(false);
        var specification = SourcePathRepairHostSpecification.Read(specificationPath, plan);
        await SourcePathRepairMoveHost.ProbeNoReplaceAsync(plan, specification, barrier, cancellationToken).ConfigureAwait(false);
        var prepared = new CloudSourcePathRepairState(plan, CloudSourcePathRepairPhase.Prepared);
        var saved = await lease.CompareExchangeAsync(target, target.Job with { SourcePathRepair = prepared },
            cancellationToken).ConfigureAwait(false);
        return new(saved.Revision, prepared);
    }

    private async Task<(CloudMigrationJournalSnapshot Target, CloudSourcePathRepairPlan Plan)> ValidateAsync(
        string jobId, string approvalPath, CancellationToken cancellationToken)
    {
        var approval = JsonSerializer.Deserialize<Approval>(LinuxTrustedFile.Read(approvalPath, 2 * 1024 * 1024), Json)
            ?? throw new InvalidOperationException("source-repair-approval-invalid");
        var plan = approval.Plan;
        if (!CloudSourcePathRepairTransaction.ValidPlan(plan) || plan.Binding.JobId != jobId
            || !HostSpecification.IsValidHash(approval.ExpectedJournalRevision))
            throw new InvalidOperationException("source-repair-approval-invalid");
        var inventory = await lease.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        var target = inventory.SingleOrDefault(item => item.Job.Id == jobId);
        if (target is null || target.Revision != approval.ExpectedJournalRevision)
            throw new InvalidOperationException("source-repair-approval-revision-changed");
        var job = target.Job;
        if (job.SourcePathRepair is not null || job.Maintenance is not null || job.MaintenanceAdmission is not null
            || job.Direction != CloudMigrationDirections.LocalToVfs || job.Phase != CloudMigrationPhases.Failed
            || job.Code != "conflict-path-collision"
            || job.ConflictRecovery is not { Status: "blocked", Code: "conflict-path-collision", FailureStage: "listing-cloud", Plan: null, Cursor: null }
            || !SourcePathRepairJournal.MatchesJob(plan.Binding, job))
            throw new InvalidOperationException("source-repair-admission-job-ineligible");
        if (inventory.Any(item => item.Job.Id != jobId && Conflicts(item.Job, job)))
            throw new InvalidOperationException("source-repair-admission-conflicting-job");

        var specification = SourcePathRepairHostSpecification.Read(specificationPath, plan);
        await VerifyBarrierAsync(specification, cancellationToken).ConfigureAwait(false);
        VerifyBoundDirectories(specification, plan);
        await CustodyProcess.VerifySourceTreeAsync(specification, plan, repaired: false, cancellationToken).ConfigureAwait(false);
        var metadata = await CloudMigrationManifestBuilder.CaptureAsync(plan.Binding.LocalRoot, cancellationToken).ConfigureAwait(false);
        var expected = plan.Binding.Source;
        if (metadata.Version != expected.ManifestVersion || metadata.FileCount != expected.FileCount
            || metadata.TotalBytes != expected.TotalBytes
            || !metadata.DigestSha256.Equals(expected.ManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("source-repair-source-baseline-changed");
        var archive = new SourcePathRepairArchiveHost(new SourcePathRepairJournal(lease), barrier, specificationPath);
        if (archive.InspectArchive(plan) is not null)
            throw new InvalidOperationException("source-repair-archive-already-present");
        await VerifyBarrierAsync(specification, cancellationToken).ConfigureAwait(false);
        return (target, plan);
    }

    private static void VerifyBoundDirectories(SourcePathRepairHostSpecification specification, CloudSourcePathRepairPlan plan)
    {
        var source = plan.Binding.LocalRoot;
        var root = plan.Binding.RootIdentity;
        var parents = plan.Moves.Select(move => Path.GetDirectoryName(move.Source) ?? "")
            .Distinct(StringComparer.Ordinal).ToArray();
        if (specification.MoveParents.Count != parents.Length)
            throw new InvalidOperationException("source-repair-move-parent-unbound");
        foreach (var relative in parents)
        {
            if (!specification.MoveParents.TryGetValue(relative, out var identity)
                || identity.Device != root.Device || identity.MountId != root.MountId)
                throw new InvalidOperationException("source-repair-move-parent-unbound");
            RequireIdentity(relative.Length == 0 ? source : Path.Combine(source, relative), identity);
        }

        var archive = Path.Combine(specification.ArchiveParent, specification.ArchiveLeaf);
        var probe = specification.RenameProbePath;
        var probeIdentity = specification.RenameProbeIdentity;
        var archiveIdentity = specification.ArchiveParentIdentity;
        if (probeIdentity.Device != root.Device || probeIdentity.MountId != root.MountId
            || PathsOverlap(probe, source, '/', StringComparison.Ordinal)
            || PathsOverlap(probe, archive, '/', StringComparison.Ordinal)
            || PathsOverlap(archive, source, '/', StringComparison.Ordinal)
            || (archiveIdentity.Device == root.Device && archiveIdentity.MountId != root.MountId))
            throw new InvalidOperationException("source-repair-scratch-or-archive-unbound");
        RequireIdentity(probe, probeIdentity);
        RequireIdentity(specification.ArchiveParent, archiveIdentity);
    }

    private static void RequireIdentity(string path, CloudSourcePathRepairIdentity expected)
    {
        var actual = LinuxTrustedFile.InspectDirectory(path);
        if (new CloudSourcePathRepairIdentity(actual.Device, actual.Inode, actual.MountId) != expected)
            throw new InvalidOperationException("source-repair-directory-identity-changed");
    }

    private Task VerifyBarrierAsync(SourcePathRepairHostSpecification specification, CancellationToken cancellationToken)
        => barrier.VerifyMaintenanceAsync("jellyfin.service", specification.BootId, specification.MountNamespace, cancellationToken);

    private static bool Conflicts(CloudMigrationJob other, CloudMigrationJob job)
    {
        if (other.SourcePathRepairOwnsJob) return true;
        if (other.Phase is CloudMigrationPhases.Completed or CloudMigrationPhases.RolledBack
            && other.Maintenance is null && other.MaintenanceAdmission is null
            && other.ConflictRecovery?.OwnsJob != true) return false;
        return string.Equals(other.ProfileId, job.ProfileId, StringComparison.OrdinalIgnoreCase)
            || PathsOverlap(Path.GetFullPath(other.LocalPath), job.LocalPath, '/', StringComparison.OrdinalIgnoreCase)
            || (other.RemoteName.Equals(job.RemoteName, StringComparison.OrdinalIgnoreCase)
                && PathsOverlap(other.RemotePath, job.RemotePath, '/', StringComparison.OrdinalIgnoreCase));
    }

    private static bool PathsOverlap(string first, string second, char separator, StringComparison comparison)
    {
        first = first.TrimEnd(separator);
        second = second.TrimEnd(separator);
        return first.Length == 0 || second.Length == 0 || first.Equals(second, comparison)
            || first.StartsWith(second + separator, comparison) || second.StartsWith(first + separator, comparison);
    }

    internal sealed record Approval(string ExpectedJournalRevision, CloudSourcePathRepairPlan Plan);
}
