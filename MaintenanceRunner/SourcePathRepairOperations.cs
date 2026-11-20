using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;

namespace Slipmat.MaintenanceRunner;

/// <summary>Composes actual Linux observations and effects for the source repair state machine.</summary>
internal sealed class SourcePathRepairOperations(
    SourcePathRepairJournal journal, LinuxMaintenanceBarrier barrier, string specificationPath)
    : ICloudSourcePathRepairOperations
{
    private readonly SourcePathRepairArchiveHost _archive = new(journal, barrier, specificationPath);
    private readonly SourcePathRepairMoveHost _moves = new(journal, barrier, specificationPath);
    private string? _probedPlan;
    private CloudSourcePathRepairVerification? _verification;

    internal async Task<CloudSourcePathRepairDecision> RunAsync(string jobId, CancellationToken cancellationToken)
    {
        var initial = await journal.ReadAsync(jobId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("source-repair-job-unavailable");
        await _moves.ProbeNoReplaceAsync(initial, cancellationToken).ConfigureAwait(false);
        _probedPlan = initial.State.Plan.Fingerprint;
        try { return await CloudSourcePathRepairTransaction.RunAsync(jobId, this, cancellationToken).ConfigureAwait(false); }
        finally { _probedPlan = null; _verification = null; }
    }

    public Task<CloudSourcePathRepairSnapshot?> ReadAsync(string jobId, CancellationToken cancellationToken)
        => journal.ReadAsync(jobId, cancellationToken);

    public Task<CloudSourcePathRepairSnapshot?> CompareExchangeAsync(CloudSourcePathRepairSnapshot expected,
        CloudSourcePathRepairState replacement, CancellationToken cancellationToken)
        => journal.CompareExchangeAsync(expected, replacement, cancellationToken);

    public Task<string> ReserveArchiveAsync(CloudSourcePathRepairSnapshot expected, CancellationToken cancellationToken)
        => _archive.ReserveArchiveAsync(expected, cancellationToken);

    public async Task<CloudSourcePathRepairObservation> ObserveAsync(CloudSourcePathRepairSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var paths = await _moves.ObserveAsync(snapshot, cancellationToken).ConfigureAwait(false);
        var archive = await ObserveArchiveAsync(snapshot, cancellationToken).ConfigureAwait(false);
        return new(snapshot.Revision, snapshot.State.Plan.Fingerprint, true, true, true,
            _probedPlan == snapshot.State.Plan.Fingerprint, paths, archive, _verification);
    }

    private async Task<CloudSourcePathRepairArchiveObservation> ObserveArchiveAsync(CloudSourcePathRepairSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var archive = _archive.InspectArchive(snapshot.State.Plan);
        var reserved = snapshot.State.ReservedArchiveIdentity;
        if (archive is null) return new(CloudSourcePathRepairArchiveState.Missing);
        if (reserved is null || reserved != archive) return new(CloudSourcePathRepairArchiveState.Foreign);
        try
        {
            var identity = await _archive.VerifyArchiveAsync(snapshot, cancellationToken).ConfigureAwait(false);
            return new(CloudSourcePathRepairArchiveState.Verified, identity, snapshot.State.Plan.PreservationManifestSha256);
        }
        catch (InvalidOperationException ex) when (ex.Message == "custody-helper-failed" && snapshot.State.Preservation is null)
        {
            // The copy helper checks every existing entry before allowing any additional writes.
            return new(CloudSourcePathRepairArchiveState.Resumable, archive);
        }
    }

    public async Task ExecuteAsync(CloudSourcePathRepairSnapshot snapshot, CloudSourcePathRepairDecision decision,
        CancellationToken cancellationToken)
    {
        switch (decision.Action)
        {
            case CloudSourcePathRepairAction.Preserve:
                await _archive.CopyArchiveAsync(snapshot, cancellationToken).ConfigureAwait(false);
                return;
            case CloudSourcePathRepairAction.Move:
            case CloudSourcePathRepairAction.Restore:
                _verification = null;
                await _moves.ExecuteAsync(snapshot, decision, cancellationToken).ConfigureAwait(false);
                return;
            case CloudSourcePathRepairAction.VerifyOriginal:
            case CloudSourcePathRepairAction.VerifyRepaired:
                _verification = await VerifyTreeAsync(snapshot, decision.Action == CloudSourcePathRepairAction.VerifyRepaired,
                    cancellationToken).ConfigureAwait(false);
                return;
            default:
                throw new InvalidOperationException("source-repair-effect-invalid");
        }
    }

    private async Task<CloudSourcePathRepairVerification> VerifyTreeAsync(CloudSourcePathRepairSnapshot expected,
        bool repaired, CancellationToken cancellationToken)
    {
        var current = await journal.ReadAsync(expected.State.Plan.Binding.JobId, cancellationToken).ConfigureAwait(false);
        if (current is null || current.Revision != expected.Revision
            || (repaired ? current.State.Phase is not (CloudSourcePathRepairPhase.VerifyRepairedIntent or CloudSourcePathRepairPhase.Repaired)
                         : current.State.Phase is not (CloudSourcePathRepairPhase.VerifyOriginalIntent or CloudSourcePathRepairPhase.RolledBack)))
            throw new InvalidOperationException("source-repair-verification-intent-required");
        var plan = current.State.Plan;
        var specification = SourcePathRepairHostSpecification.Read(specificationPath, plan);
        if (current.State.Preservation is not null)
            await _archive.VerifyArchiveAsync(current, cancellationToken).ConfigureAwait(false);
        await barrier.VerifyMaintenanceAsync("jellyfin.service", specification.BootId, specification.MountNamespace,
            cancellationToken).ConfigureAwait(false);
        await CustodyProcess.VerifySourceTreeAsync(specification, plan, repaired, cancellationToken).ConfigureAwait(false);
        await barrier.VerifyMaintenanceAsync("jellyfin.service", specification.BootId, specification.MountNamespace,
            cancellationToken).ConfigureAwait(false);
        return new(plan.Fingerprint,
            repaired ? CloudSourcePathRepairVerificationKind.Repaired : CloudSourcePathRepairVerificationKind.Original,
            repaired ? plan.TargetNamespaceSha256 : plan.Binding.Source.NamespaceSha256, plan.PreservationManifestSha256);
    }
}
