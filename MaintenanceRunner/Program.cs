using System.Runtime.InteropServices;
using System.Text.Json;
using Jellyfin.Plugin.AudioGateway.Services;
using Jellyfin.Plugin.AudioGateway.Models;

namespace Slipmat.MaintenanceRunner;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var inspect = args.Length == 3 && args[0] == "inspect";
        var startup = args.Length == 4 && args[0] == "startup" && Path.IsPathFullyQualified(args[3]);
        var run = args.Length == 4 && args[0] == "run" && Path.IsPathFullyQualified(args[3]);
        var sourceRepairStartup = args.Length == 2 && args[0] == "source-repair-startup";
        var sourceRepairCheck = args.Length == 5 && args[0] == "source-repair-check"
            && Path.IsPathFullyQualified(args[3]) && Path.IsPathFullyQualified(args[4]);
        var sourceRepairRun = args.Length is 4 or 5 && args[0] == "source-repair-run"
            && Path.IsPathFullyQualified(args[3]) && (args.Length == 4 || Path.IsPathFullyQualified(args[4]));
        var sourceRepairRollback = args.Length == 4 && args[0] == "source-repair-rollback"
            && Path.IsPathFullyQualified(args[3]);
        if ((!inspect && !run && !startup && !sourceRepairStartup && !sourceRepairCheck && !sourceRepairRun && !sourceRepairRollback) || !Path.IsPathFullyQualified(args[1])
            || (!sourceRepairStartup && !Guid.TryParseExact(args[2], "N", out _)))
        {
            Console.Error.WriteLine("Usage: Slipmat.MaintenanceRunner inspect ABSOLUTE_JOURNAL_DIRECTORY JOB_ID");
            Console.Error.WriteLine("   or: Slipmat.MaintenanceRunner run ABSOLUTE_JOURNAL_DIRECTORY JOB_ID ABSOLUTE_SPEC_PATH");
            Console.Error.WriteLine("   or: Slipmat.MaintenanceRunner startup ABSOLUTE_JOURNAL_DIRECTORY JOB_ID ABSOLUTE_SPEC_PATH");
            Console.Error.WriteLine("   or: Slipmat.MaintenanceRunner source-repair-startup ABSOLUTE_JOURNAL_DIRECTORY");
            Console.Error.WriteLine("   or: Slipmat.MaintenanceRunner source-repair-check ABSOLUTE_JOURNAL_DIRECTORY JOB_ID ABSOLUTE_SPEC_PATH ABSOLUTE_APPROVAL_PATH");
            Console.Error.WriteLine("   or: Slipmat.MaintenanceRunner source-repair-run JOURNAL JOB_ID SPEC [APPROVAL]");
            Console.Error.WriteLine("   or: Slipmat.MaintenanceRunner source-repair-rollback JOURNAL JOB_ID SPEC");
            return 2;
        }
        if (!OperatingSystem.IsLinux() || GetEffectiveUserId() != 0)
        {
            Console.Error.WriteLine("Linux root is required.");
            return 2;
        }
        try
        {
            // Inspection must not create a journal directory or lock file.
            if (!Directory.Exists(args[1]) || !File.Exists(Path.Combine(args[1], ".writer.lock")))
            {
                Console.Error.WriteLine("Existing journal lease unavailable.");
                return 2;
            }
            using var cancellation = new CancellationTokenSource();
            ConsoleCancelEventHandler cancel = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };
            Console.CancelKeyPress += cancel;
            try
            {
                if (sourceRepairStartup)
                {
                    cancellation.CancelAfter(TimeSpan.FromSeconds(10));
                    await using var readOnlyLease = OpenExistingJournalLease(args[1]);
                    var inventory = await readOnlyLease.ReadAllAsync(cancellation.Token).ConfigureAwait(false);
                    var owningRepair = inventory.Any(snapshot => snapshot.Job.SourcePathRepairOwnsJob);
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        status = owningRepair ? "blocked" : "source-repair-startup-clear",
                        code = owningRepair ? "migration-source-repair-owned" : "no-owning-source-repair",
                        execution = "not-requested",
                    }));
                    return owningRepair ? 1 : 0;
                }
                if (sourceRepairCheck)
                {
                    await using var readOnlyLease = OpenExistingJournalLease(args[1]);
                    var admission = new SourcePathRepairAdmission(readOnlyLease,
                        new LinuxMaintenanceBarrier(new LinuxSystemdBarrierQuery()), args[3]);
                    await admission.CheckAsync(args[2], args[4], cancellation.Token).ConfigureAwait(false);
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        status = "source-repair-preflight-verified",
                        jobId = args[2],
                        execution = "not-requested",
                    }));
                    return 0;
                }
                if (sourceRepairRun || sourceRepairRollback)
                {
                    var journalDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[1]));
                    await using var repairLease = OpenExistingJournalLease(journalDirectory);
                    var execution = new SourcePathRepairExecution(repairLease,
                        new LinuxMaintenanceBarrier(new LinuxSystemdBarrierQuery()), args[3],
                        () => SourcePathRepairStartupHook.VerifyInstalled(journalDirectory));
                    var result = await execution.RunAsync(args[2], args.Length == 5 ? args[4] : null,
                        sourceRepairRollback, cancellation.Token).ConfigureAwait(false);
                    var current = await repairLease.ReadAsync(args[2], cancellation.Token).ConfigureAwait(false);
                    var finished = result.Action == CloudSourcePathRepairAction.Complete;
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        status = finished ? "stable" : "blocked",
                        jobId = args[2],
                        phase = current?.Job.SourcePathRepair?.Phase.ToString(),
                        code = result.Code,
                    }));
                    return finished ? 0 : 1;
                }
                if (inspect) cancellation.CancelAfter(TimeSpan.FromSeconds(10));
                await using var lease = await new CloudMigrationJournal(args[1]).AcquireAsync(cancellation.Token);
                if (inspect)
                {
                    var snapshot = await lease.ReadAsync(args[2], cancellation.Token);
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        status = snapshot?.Job.SourcePathRepair is not null ? "source-repair-inspection-only"
                            : snapshot?.Job.Maintenance is null ? "no-maintenance-intent" : "inspection-only",
                        jobId = snapshot?.Job.Id,
                        phase = snapshot?.Job.Maintenance?.Phase.ToString(),
                        direction = snapshot?.Job.Maintenance?.Direction.ToString(),
                        journalRevision = snapshot?.Revision,
                        sourceRepairPhase = snapshot?.Job.SourcePathRepair?.Phase.ToString(),
                        sourceRepairOwned = snapshot?.Job.SourcePathRepairOwnsJob,
                        execution = "not-requested",
                    }));
                    return snapshot is null ? 2 : 0;
                }

                var repairGuard = await lease.ReadAsync(args[2], cancellation.Token).ConfigureAwait(false);
                if (repairGuard is null) throw new InvalidOperationException("migration-not-found");
                if (repairGuard.Job.SourcePathRepairOwnsJob == true)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        status = "blocked",
                        code = "migration-source-repair-owned",
                        jobId = repairGuard.Job.Id,
                        execution = "not-requested",
                    }));
                    return 1;
                }
                var barrier = new LinuxMaintenanceBarrier(
                    new LinuxSystemdBarrierQuery(), startup ? Environment.ProcessId : 0);
                var specifications = new LinuxHostSpecificationProvider(args[3]);
                if (startup)
                {
                    var current = await lease.ReadAsync(args[2], cancellation.Token);
                    if (current is null)
                    {
                        Console.Error.WriteLine("Configured maintenance job is missing.");
                        return 2;
                    }
                    var snapshot = new MaintenanceSnapshot(current.Job, current.Revision);
                    var startupSpec = current.Job.Maintenance is null
                        ? specifications.ReadForStartup(snapshot)
                        : specifications.Read(snapshot);
                    await barrier.VerifyCurrentAsync(startupSpec, cancellation.Token).ConfigureAwait(false);
                    if (current.Job.Maintenance is null)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(new
                        {
                            status = "no-recovery-required",
                            jobId = current.Job.Id,
                            journalRevision = current.Revision,
                        }));
                        return 0;
                    }
                }
                var mountSystem = new LinuxSystemdMountBackend();
                var mountSafety = new LinuxMountSafetyBoundary(barrier);
                var host = new LinuxMaintenanceHost(
                    specifications, barrier, mountSafety, mountSystem);
                var operations = new LeasedOperations(new JournalLeaseAdapter(lease), host);
                var decision = await CloudMaintenanceTransaction.RunAsync(args[2], operations, cancellation.Token);
                var final = await lease.ReadAsync(args[2], cancellation.Token);
                var stable = decision.Action == Jellyfin.Plugin.AudioGateway.Models.CloudMaintenanceAction.Complete;
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    status = stable ? "stable" : "blocked",
                    jobId = final?.Job.Id,
                    phase = final?.Job.Maintenance?.Phase.ToString(),
                    code = decision.Code,
                }));
                return stable ? 0 : 1;
            }
            finally
            {
                Console.CancelKeyPress -= cancel;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or JsonException or InvalidOperationException or PlatformNotSupportedException)
        {
            Console.Error.WriteLine("Maintenance operation unavailable or blocked.");
            return 2;
        }
    }

    private static CloudMigrationJournalLease OpenExistingJournalLease(string journalDirectory)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(journalDirectory));
        CloudMigrationJournal.RejectLink(root);
        var lockPath = Path.Combine(root, ".writer.lock");
        CloudMigrationJournal.RejectLink(lockPath);
        // Keep the canonical writer's lock mode without creating or writing any file.
        var stream = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        if (stream.CanSeek) return new CloudMigrationJournalLease(root, stream);
        stream.Dispose();
        throw new IOException("migration-journal-lease-unavailable");
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();
}
