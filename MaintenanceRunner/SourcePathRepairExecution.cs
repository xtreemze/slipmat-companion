using System.Text;
using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;

namespace Slipmat.MaintenanceRunner;

/// <summary>Requires the persistent repair startup hook before allowing an offline command to mutate custody.</summary>
internal static class SourcePathRepairStartupHook
{
    internal const string PathOnHost = "/etc/systemd/system/jellyfin.service.d/90-slipmat-source-repair.conf";

    internal static byte[] ExpectedBytes(string runner, string journalDirectory)
    {
        RequireUnitArgument(runner);
        RequireUnitArgument(journalDirectory);
        return Encoding.UTF8.GetBytes($"[Service]\nExecStartPre=+{runner} source-repair-startup {journalDirectory}\n");
    }

    internal static void VerifyInstalled(string journalDirectory)
    {
        var runner = Environment.ProcessPath ?? throw new InvalidOperationException("source-repair-apphost-required");
        var managed = typeof(Program).Assembly.Location;
        if (Path.GetFileName(runner) != "Slipmat.MaintenanceRunner" || managed != runner + ".dll")
            throw new InvalidOperationException("source-repair-apphost-required");
        _ = LinuxTrustedFile.Read(runner, 4 * 1024 * 1024);
        _ = LinuxTrustedFile.Read(managed, 16 * 1024 * 1024);
        _ = LinuxTrustedFile.Read(runner + ".runtimeconfig.json", 64 * 1024);
        _ = LinuxTrustedFile.Read(runner + ".deps.json", 1024 * 1024);
        VerifyBytes(LinuxTrustedFile.Read(PathOnHost), runner, journalDirectory);
    }

    internal static void VerifyBytes(byte[] installed, string runner, string journalDirectory)
    {
        if (!installed.AsSpan().SequenceEqual(ExpectedBytes(runner, journalDirectory)))
            throw new InvalidOperationException("source-repair-startup-hook-mismatch");
    }

    private static void RequireUnitArgument(string path)
    {
        if (!Path.IsPathFullyQualified(path) || Path.GetFullPath(path) != path || path == "/"
            || path.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('/' or '.' or '-' or '_')))
            throw new InvalidOperationException("source-repair-startup-hook-path-invalid");
    }
}

/// <summary>Coordinates the explicitly requested command under the canonical whole-job lease.</summary>
internal sealed class SourcePathRepairExecution(
    CloudMigrationJournalLease lease, LinuxMaintenanceBarrier barrier, string specificationPath, Action verifyHook)
{
    internal async Task<CloudSourcePathRepairDecision> RunAsync(string jobId, string? approvalPath,
        bool rollback, CancellationToken cancellationToken)
    {
        verifyHook();
        var journal = new SourcePathRepairJournal(lease);
        var current = await journal.ReadAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (approvalPath is not null)
        {
            if (current is not null) throw new InvalidOperationException("source-repair-already-admitted");
            current = await new SourcePathRepairAdmission(lease, barrier, specificationPath)
                .PrepareAsync(jobId, approvalPath, cancellationToken).ConfigureAwait(false);
        }
        if (current is null) throw new InvalidOperationException("source-repair-job-unavailable");

        if (rollback && !current.State.RollbackRequested)
        {
            var spec = SourcePathRepairHostSpecification.Read(specificationPath, current.State.Plan);
            await barrier.VerifyMaintenanceAsync("jellyfin.service", spec.BootId, spec.MountNamespace, cancellationToken)
                .ConfigureAwait(false);
            verifyHook();
            _ = await journal.CompareExchangeAsync(current, current.State with { RollbackRequested = true }, cancellationToken)
                .ConfigureAwait(false) ?? throw new InvalidOperationException("source-repair-job-revision-changed");
        }

        verifyHook();
        return await new SourcePathRepairOperations(journal, barrier, specificationPath)
            .RunAsync(jobId, cancellationToken).ConfigureAwait(false);
    }
}
