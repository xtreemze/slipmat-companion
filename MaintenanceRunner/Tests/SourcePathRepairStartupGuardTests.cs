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
            await VerifyInventoryGuardAsync(Path.Combine(root, "inventory"), basis);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task VerifyInventoryGuardAsync(string root, CloudMigrationJob basis)
    {
        await ExpectStartupAsync(root, 2);
        if (Directory.Exists(root)) throw new Exception("startup guard created a missing journal");
        Directory.CreateDirectory(root);
        await ExpectStartupAsync(root, 2);
        var lockPath = Path.Combine(root, ".writer.lock");
        if (File.Exists(lockPath)) throw new Exception("startup guard created the writer lock");

        var ordinary = basis with { Id = Guid.NewGuid().ToString("N"), Maintenance = null, MaintenanceAdmission = null };
        var repairJob = ordinary with { Id = Guid.NewGuid().ToString("N") };
        var binding = new CloudSourcePathRepairBinding(repairJob.Id, Guid.NewGuid(), Guid.NewGuid(),
            repairJob.LibraryName, repairJob.ProfileId, repairJob.LocalPath, repairJob.RemoteName, repairJob.RemotePath,
            new(1, 2, 3), new(CloudMigrationManifestBuilder.CurrentVersion, new string('a', 64), 1, 1, new string('b', 64)),
            new string('c', 64));
        var plan = new CloudSourcePathRepairPlan(1, binding,
            [new(CloudSourcePathRepairKind.File, "Track.flac", "Track retained.flac", new(1, 4, 3))],
            new string('d', 64), new string('e', 64), string.Empty);
        plan = plan with { Fingerprint = CloudSourcePathRepairTransaction.Fingerprint(plan) };
        var journal = new CloudMigrationJournal(root);
        await using (var lease = await journal.AcquireAsync())
        {
            await lease.CompareExchangeAsync(null, ordinary);
            await ExpectStartupAsync(root, 2);
        }
        await ExpectStartupAsync(root, 0);

        var active = repairJob with { SourcePathRepair = new(plan, CloudSourcePathRepairPhase.Prepared) };
        CloudMigrationJournalSnapshot admitted;
        await using (var lease = await journal.AcquireAsync())
            admitted = await lease.CompareExchangeAsync(null, active);
        var path = Path.Combine(root, repairJob.Id + ".json");
        var before = await File.ReadAllBytesAsync(path);
        await ExpectStartupAsync(root, 1, "migration-source-repair-owned");
        var after = await File.ReadAllBytesAsync(path);
        if (!before.AsSpan().SequenceEqual(after))
            throw new Exception("startup guard changed the owning job");

        var repaired = new CloudSourcePathRepairState(plan, CloudSourcePathRepairPhase.Repaired,
            Cursor: plan.Moves.Length,
            Preservation: new(plan.Fingerprint, plan.PreservationManifestSha256, "reserved-identity"),
            Verification: new(plan.Fingerprint, CloudSourcePathRepairVerificationKind.Repaired,
                plan.TargetNamespaceSha256, plan.PreservationManifestSha256),
            ReservedArchiveIdentity: "reserved-identity");
        CloudMigrationJournalSnapshot completed;
        await using (var lease = await journal.AcquireAsync())
            completed = await lease.CompareExchangeAsync(admitted, active with { SourcePathRepair = repaired });
        await ExpectStartupAsync(root, 0);

        var rolledBack = repaired with
        {
            Phase = CloudSourcePathRepairPhase.RolledBack,
            RollbackRequested = true,
            Cursor = 0,
            Verification = new(plan.Fingerprint, CloudSourcePathRepairVerificationKind.Original,
                plan.Binding.Source.NamespaceSha256, plan.PreservationManifestSha256),
        };
        await using (var lease = await journal.AcquireAsync())
            await lease.CompareExchangeAsync(completed, active with { SourcePathRepair = rolledBack });
        await ExpectStartupAsync(root, 0);

        var corrupt = Path.Combine(root, Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(corrupt, "{\"phase\":");
        await ExpectStartupAsync(root, 2);
        if (await File.ReadAllTextAsync(corrupt) != "{\"phase\":")
            throw new Exception("startup guard changed the corrupt job");
    }

    private static async Task ExpectStartupAsync(string root, int expected, string? code = null)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var result = await Slipmat.MaintenanceRunner.Program.Main(["source-repair-startup", root]);
            if (result != expected)
                throw new Exception($"source repair inventory startup guard returned {result}, expected {expected}");
            if (code is not null)
            {
                using var response = JsonDocument.Parse(output.ToString());
                if (response.RootElement.GetProperty("code").GetString() != code)
                    throw new Exception("startup guard did not report the owning repair");
            }
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }
}
