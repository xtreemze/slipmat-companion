using System.Security.Cryptography;
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
        await new LinuxMaintenanceBarrier(new Query(startup), startupGuardPid: 42).VerifyCurrentAsync(spec, default);
        foreach (var invalid in new[] { startup with { ActiveState = "inactive" }, startup with { MainPid = 1 }, startup with { ControlPid = 43 }, startup with { CgroupPids = [42, 44] } })
        {
            try { await new LinuxMaintenanceBarrier(new Query(invalid)).VerifyStartupAsync(spec, 42, default); throw new Exception("invalid startup state accepted"); }
            catch (InvalidOperationException) { }
        }

        if (OperatingSystem.IsLinux() && Environment.UserName == "root")
        {
            var root = Path.Combine("/srv", "maintenance-mountpoint-retry-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var parent = LinuxTrustedFile.InspectDirectory(root);
                var sourcePath = Path.Combine(root, "music");
                var pythonPath = File.ResolveLinkTarget("/usr/bin/python3", true)?.FullName
                    ?? throw new Exception("python interpreter unavailable");
                var helperPath = Path.Combine(root, "custody.py");
                File.WriteAllBytes(helperPath, File.ReadAllBytes(Path.GetFullPath("MaintenanceRunner/custody.py")));
                var testSpec = spec with
                {
                    CachePath = "/sys",
                    Parent = parent,
                    Source = new(sourcePath, parent.Device, 123, parent.MountId),
                    BackupPath = Path.Combine(root, "music.backup"),
                    PythonPath = pythonPath,
                    PythonSha256 = Convert.ToHexString(SHA256.HashData(LinuxTrustedFile.Read(pythonPath, 128 * 1024 * 1024))),
                    CustodyHelperPath = helperPath,
                    CustodyHelperSha256 = Convert.ToHexString(SHA256.HashData(LinuxTrustedFile.Read(helperPath, 1024 * 1024))),
                };
                var concreteSafety = new LinuxMountSafetyBoundary(new LinuxMaintenanceBarrier(new Query(normal)));
                var first = await concreteSafety.PrepareEmptyMountpointAsync(testSpec, default);
                var retry = await concreteSafety.PrepareEmptyMountpointAsync(testSpec, default);
                if (retry != first) throw new Exception("mountpoint retry changed the owned empty directory identity");
                File.WriteAllText(Path.Combine(sourcePath, "unexpected"), "preserve");
                try { await concreteSafety.PrepareEmptyMountpointAsync(testSpec, default); throw new Exception("nonempty recovered mountpoint accepted"); }
                catch (InvalidOperationException) { }
            }
            finally { Directory.Delete(root, recursive: true); }
        }
    }
    private sealed class Query(LinuxBarrierSnapshot snapshot) : ILinuxBarrierQuery
    {
        public Task<LinuxBarrierSnapshot> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(snapshot);
    }
}
