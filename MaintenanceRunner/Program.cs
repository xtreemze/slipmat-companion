using System.Runtime.InteropServices;
using System.Text.Json;
using Jellyfin.Plugin.AudioGateway.Services;

namespace Slipmat.MaintenanceRunner;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var inspect = args.Length == 3 && args[0] == "inspect";
        var run = args.Length == 4 && args[0] == "run" && Path.IsPathFullyQualified(args[3]);
        if ((!inspect && !run) || !Path.IsPathFullyQualified(args[1]) || !Guid.TryParseExact(args[2], "N", out _))
        {
            Console.Error.WriteLine("Usage: Slipmat.MaintenanceRunner inspect ABSOLUTE_JOURNAL_DIRECTORY JOB_ID");
            Console.Error.WriteLine("   or: Slipmat.MaintenanceRunner run ABSOLUTE_JOURNAL_DIRECTORY JOB_ID ABSOLUTE_SPEC_PATH");
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

                var barrier = new LinuxMaintenanceBarrier(new LinuxSystemdBarrierQuery());
                var mountSystem = new LinuxSystemdMountBackend();
                var mountSafety = new LinuxMountSafetyBoundary(barrier);
                var host = new LinuxMaintenanceHost(
                    new LinuxHostSpecificationProvider(args[3]), barrier, mountSafety, mountSystem);
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

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();
}
