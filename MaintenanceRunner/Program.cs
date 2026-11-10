using System.Runtime.InteropServices;
using System.Text.Json;
using Jellyfin.Plugin.AudioGateway.Services;

namespace Slipmat.MaintenanceRunner;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 3 || args[0] != "inspect" || !Path.IsPathFullyQualified(args[1]) || !Guid.TryParseExact(args[2], "N", out _))
        {
            Console.Error.WriteLine("Usage: Slipmat.MaintenanceRunner inspect ABSOLUTE_JOURNAL_DIRECTORY JOB_ID");
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
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var lease = await new CloudMigrationJournal(args[1]).AcquireAsync(timeout.Token);
            var snapshot = await lease.ReadAsync(args[2], timeout.Token);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                status = snapshot?.Job.Maintenance is null ? "no-maintenance-intent" : "inspection-only",
                jobId = snapshot?.Job.Id,
                phase = snapshot?.Job.Maintenance?.Phase.ToString(),
                direction = snapshot?.Job.Maintenance?.Direction.ToString(),
                journalRevision = snapshot?.Revision,
                execution = "blocked-host-composition-unavailable",
            }));
            return snapshot is null ? 2 : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or JsonException)
        {
            Console.Error.WriteLine("Journal inspection unavailable.");
            return 2;
        }
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();
}
