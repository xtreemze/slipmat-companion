using System.Reflection;
using System.Text.Json;
using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;
using Slipmat.MaintenanceRunner;

internal static class SourcePathRepairStartupGuardTests
{
    public static async Task Run(CloudMigrationJob basis)
    {
        if (!OperatingSystem.IsLinux() || Environment.UserName != "root") return;
        var root = Path.Combine("/root", "source-repair-startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repair = JsonSerializer.Deserialize<CloudSourcePathRepairState>("{\"phase\":999}",
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            var job = basis with { Maintenance = null, MaintenanceAdmission = null, SourcePathRepair = repair };
            await using (var lease = await new CloudMigrationJournal(root).AcquireAsync())
                await lease.CompareExchangeAsync(null, job);
            var path = Path.Combine(root, job.Id + ".json");
            var before = await File.ReadAllBytesAsync(path);
            var entry = typeof(LinuxMaintenanceBarrier).Assembly.GetType("Slipmat.MaintenanceRunner.Program")!
                .GetMethod("Main", BindingFlags.Public | BindingFlags.Static)!;
            foreach (var command in new[] { "startup", "run" })
            {
                var originalOut = Console.Out;
                var originalError = Console.Error;
                using var output = new StringWriter();
                using var error = new StringWriter();
                try
                {
                    Console.SetOut(output); Console.SetError(error);
                    var result = await (Task<int>)entry.Invoke(null,
                        [new[] { command, root, job.Id, Path.Combine(root, "unread-spec.json") }])!;
                    if (result != 1) throw new Exception("repair ownership was not the explicit startup/run refusal");
                }
                finally { Console.SetOut(originalOut); Console.SetError(originalError); }
                using var response = JsonDocument.Parse(output.ToString());
                if (response.RootElement.GetProperty("code").GetString() != "migration-source-repair-owned"
                    || response.RootElement.GetProperty("execution").GetString() != "not-requested")
                    throw new Exception("source repair reached the host-spec or no-recovery path");
                var after = await File.ReadAllBytesAsync(path);
                if (!before.AsSpan().SequenceEqual(after))
                    throw new Exception("source repair startup guard changed the canonical journal");
                if (File.Exists(Path.Combine(root, "unread-spec.json")))
                    throw new Exception("source repair startup guard created a host specification");
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
