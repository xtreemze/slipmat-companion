using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;
using Slipmat.MaintenanceRunner;

internal static class PreparationRunnerTests
{
    public static async Task Run(HostSpecification spec, CloudMigrationJob admittedJob)
    {
        var original = new CloudMigrationJournalSnapshot(admittedJob with { Maintenance = null }, "whole-job-revision");
        var host = new Host(spec);
        var saves = 0;
        Task<CloudMigrationJournalSnapshot> Save(CloudMigrationJournalSnapshot expected, CloudMigrationJob replacement, CancellationToken ct)
        {
            if (expected != original || replacement.Maintenance?.Phase != CloudMaintenancePhase.Prepared)
                throw new Exception("wrong CAS input");
            saves++;
            return Task.FromResult(new CloudMigrationJournalSnapshot(replacement, "new-revision"));
        }
        var result = await PreparationRunner.PrepareAsync(original, spec, "/private/spec", host, Save, default);
        if (result.Job.Maintenance?.Direction != CloudMaintenanceDirection.Promote || saves != 1
            || string.Join(",", host.Calls) != "barrier,local,barrier,prepare,barrier,capability,barrier,verify,barrier,remote,barrier,local,barrier")
            throw new Exception("preparation sequence incorrect");
        foreach (var failure in new[] { "capability", "verify", "remote", "local2", "barrier2" })
        {
            host = new Host(spec) { Failure = failure };
            saves = 0;
            try { await PreparationRunner.PrepareAsync(original, spec, "/private/spec", host, Save, default); throw new Exception("failure admitted: " + failure); }
            catch (InvalidOperationException ex) when (ex.Message == "injected") { }
            if (saves != 0) throw new Exception("failure persisted intent: " + failure);
        }
        host = new Host(spec) { CancelAtVerification = true };
        using var cancellation = new CancellationTokenSource(); host.Cancellation = cancellation;
        saves = 0;
        try { await PreparationRunner.PrepareAsync(original, spec, "/private/spec", host, Save, cancellation.Token); throw new Exception("cancellation admitted"); }
        catch (OperationCanceledException) { }
        if (saves != 0) throw new Exception("cancellation persisted intent");
    }

    private sealed class Host(HostSpecification spec) : IPreparationHost
    {
        public readonly List<string> Calls = [];
        public string? Failure;
        public bool CancelAtVerification;
        public CancellationTokenSource? Cancellation;
        private int local, barrier;
        private void Call(string name, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Calls.Add(name);
            var key = name == "local" && ++local == 2 ? "local2" : name == "barrier" && ++barrier == 2 ? "barrier2" : name;
            if (Failure == key) throw new InvalidOperationException("injected");
        }
        public Task BarrierAsync(HostSpecification value, CancellationToken ct) { Call("barrier", ct); return Task.CompletedTask; }
        public Task CheckLocalAsync(HostSpecification value, CloudMigrationJob job, CancellationToken ct) { Call("local", ct); return Task.CompletedTask; }
        public (HostSpecification Specification, string Digest) Prepare(HostSpecification value, CloudMigrationJob job, string output) { Call("prepare", default); return (spec, new string('a', 64)); }
        public Task CapabilityAsync(HostSpecification value, CancellationToken ct) { Call("capability", ct); return Task.CompletedTask; }
        public Task VerifyAsync(CloudMigrationJob job, HostSpecification value, CancellationToken ct) { Call("verify", ct); if (CancelAtVerification) Cancellation!.Cancel(); return Task.CompletedTask; }
        public Task ResolveAsync(HostSpecification value, string remote, CancellationToken ct) { Call("remote", ct); return Task.CompletedTask; }
    }
}
