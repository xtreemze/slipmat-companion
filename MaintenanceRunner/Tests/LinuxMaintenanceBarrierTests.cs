using Slipmat.MaintenanceRunner;

internal static class LinuxMaintenanceBarrierTests
{
    public static async Task Run(HostSpecification spec)
    {
        var normal = new LinuxBarrierSnapshot(true, true, "inactive", 0, 0, [], spec.BootId, spec.MountNamespace, spec.MountNamespace);
        await new LinuxMaintenanceBarrier(new Query(normal)).VerifyMaintenanceAsync(spec, default);
        foreach (var invalid in new[] { normal with { PersistentMaskTrusted = false }, normal with { ActiveState = "active" }, normal with { MainPid = 3 }, normal with { ControlPid = 4 }, normal with { CgroupPids = [5] }, normal with { BootId = Guid.NewGuid().ToString() }, normal with { Pid1Namespace = "mnt:[99]" }, normal with { IsLinuxRoot = false } })
        {
            try { await new LinuxMaintenanceBarrier(new Query(invalid)).VerifyMaintenanceAsync(spec, default); throw new Exception("invalid maintenance state accepted"); }
            catch (InvalidOperationException) { }
        }
        var startup = normal with { PersistentMaskTrusted = false, ActiveState = "activating", ControlPid = 42, CgroupPids = [42] };
        await new LinuxMaintenanceBarrier(new Query(startup)).VerifyStartupAsync(spec, 42, default);
        foreach (var invalid in new[] { startup with { ActiveState = "inactive" }, startup with { MainPid = 1 }, startup with { ControlPid = 43 }, startup with { CgroupPids = [42, 44] } })
        {
            try { await new LinuxMaintenanceBarrier(new Query(invalid)).VerifyStartupAsync(spec, 42, default); throw new Exception("invalid startup state accepted"); }
            catch (InvalidOperationException) { }
        }
    }
    private sealed class Query(LinuxBarrierSnapshot snapshot) : ILinuxBarrierQuery
    {
        public Task<LinuxBarrierSnapshot> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(snapshot);
    }
}
