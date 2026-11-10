using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Jellyfin.Plugin.AudioGateway.Services;

namespace Slipmat.MaintenanceRunner;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var prepare = args.Length == 5 && args[0] == "prepare" && Path.IsPathFullyQualified(args[3]) && Path.IsPathFullyQualified(args[4]);
        var inspect = args.Length == 3 && args[0] == "inspect";
        var startup = args.Length == 4 && args[0] == "startup" && Path.IsPathFullyQualified(args[3]);
        var rollback = args.Length == 4 && args[0] == "rollback" && Path.IsPathFullyQualified(args[3]);
        var run = args.Length == 4 && args[0] == "run" && Path.IsPathFullyQualified(args[3]);
        if ((!inspect && !run && !startup && !prepare && !rollback) || !Path.IsPathFullyQualified(args[1]) || !Guid.TryParseExact(args[2], "N", out _))
        {
            Console.Error.WriteLine("Usage: Slipmat.MaintenanceRunner inspect ABSOLUTE_JOURNAL_DIRECTORY JOB_ID");
            Console.Error.WriteLine("   or: Slipmat.MaintenanceRunner rollback ABSOLUTE_JOURNAL_DIRECTORY JOB_ID ABSOLUTE_SPEC_PATH");
            Console.Error.WriteLine("   or: Slipmat.MaintenanceRunner run ABSOLUTE_JOURNAL_DIRECTORY JOB_ID ABSOLUTE_SPEC_PATH");
            Console.Error.WriteLine("   or: Slipmat.MaintenanceRunner prepare ABSOLUTE_JOURNAL_DIRECTORY JOB_ID ABSOLUTE_DRAFT_SPEC ABSOLUTE_OUTPUT_SPEC");
            Console.Error.WriteLine("   or: Slipmat.MaintenanceRunner startup ABSOLUTE_JOURNAL_DIRECTORY JOB_ID ABSOLUTE_SPEC_PATH");
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
                if (inspect) cancellation.CancelAfter(TimeSpan.FromSeconds(10));
                await using var lease = await new CloudMigrationJournal(args[1]).AcquireAsync(cancellation.Token);
                if (inspect)
                {
                    var snapshot = await lease.ReadAsync(args[2], cancellation.Token);
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        status = snapshot?.Job.Maintenance is null ? "no-maintenance-intent" : "inspection-only",
                        jobId = snapshot?.Job.Id,
                        phase = snapshot?.Job.Maintenance?.Phase.ToString(),
                        direction = snapshot?.Job.Maintenance?.Direction.ToString(),
                        journalRevision = snapshot?.Revision,
                        execution = "not-requested",
                    }));
                    return snapshot is null ? 2 : 0;
                }

                var barrier = new LinuxMaintenanceBarrier(
                    new LinuxSystemdBarrierQuery(), startup ? Environment.ProcessId : 0);
                if (prepare)
                {
                    var current = await lease.ReadAsync(args[2], cancellation.Token)
                        ?? throw new InvalidOperationException("maintenance-job-missing");
                    if (current.Job.Id != args[2]) throw new InvalidOperationException("maintenance-job-binding-mismatch");
                    var draft = PreparationRunner.ReadDraft(args[3]);
                    var preparation = new LinuxPreparationHost(barrier, CustodyProcess.ProbeNoReplaceAsync);
                    var prepared = await PreparationRunner.PrepareAsync(current, draft, args[4], preparation,
                        (expected, replacement, ct) => lease.CompareExchangeAsync(expected, replacement, ct), cancellation.Token);
                    Console.WriteLine(JsonSerializer.Serialize(new { status = "prepared", jobId = prepared.Job.Id, journalRevision = prepared.Revision }));
                    return 0;
                }
                var specifications = new LinuxHostSpecificationProvider(args[3]);
                var mountSystem = new LinuxSystemdMountBackend();
                var mountSafety = new LinuxMountSafetyBoundary(barrier);
                var host = new LinuxMaintenanceHost(specifications, barrier, mountSafety, mountSystem);
                if (startup)
                {
                    var current = await lease.ReadAsync(args[2], cancellation.Token);
                    if (current is null || current.Job.Id != args[2])
                    {
                        Console.Error.WriteLine("Configured maintenance job is missing.");
                        return 2;
                    }
                    var snapshot = new MaintenanceSnapshot(current.Job, current.Revision);
                    var startupSpec = current.Job.Maintenance is null
                        ? specifications.ReadForStartup(snapshot)
                        : specifications.Read(snapshot);
                    await barrier.VerifyCurrentAsync(startupSpec, cancellation.Token).ConfigureAwait(false);
                    var digest = Convert.ToHexString(SHA256.HashData(LinuxTrustedFile.Read(args[3])));
                    var stableAtStartup = await StartupGuard.IsStableAsync(snapshot, startupSpec, digest, host, cancellation.Token);
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        status = stableAtStartup ? "stable" : "blocked",
                        jobId = current.Job.Id,
                        journalRevision = current.Revision,
                    }));
                    return stableAtStartup ? 0 : 1;
                }
                if (rollback)
                {
                    var current = await lease.ReadAsync(args[2], cancellation.Token)
                        ?? throw new InvalidOperationException("maintenance-job-missing");
                    if (current.Job.Id != args[2]) throw new InvalidOperationException("maintenance-job-binding-mismatch");
                    await MaintenanceCompletion.BeginRollbackAsync(current, host,
                        (expected, replacement, ct) => lease.CompareExchangeAsync(expected, replacement, ct), cancellation.Token);
                }
                var operations = new LeasedOperations(new JournalLeaseAdapter(lease), host);
                var decision = await CloudMaintenanceTransaction.RunAsync(args[2], operations, cancellation.Token);
                var final = await lease.ReadAsync(args[2], cancellation.Token);
                var stable = decision.Action == Jellyfin.Plugin.AudioGateway.Models.CloudMaintenanceAction.Complete;
                if (stable && final is not null)
                    final = await MaintenanceCompletion.CompleteAsync(final, host,
                        (expected, replacement, ct) => lease.CompareExchangeAsync(expected, replacement, ct), cancellation.Token);
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

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();
}
