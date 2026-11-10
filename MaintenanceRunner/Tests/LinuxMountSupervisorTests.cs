using Slipmat.MaintenanceRunner;

internal static class LinuxMountSupervisorTests
{
    public static async Task Run(HostSpecification spec)
    {
        var expected = new MountInstance(1, spec.BootId, spec.MountNamespace, 44, 0, 22, "fuse.rclone", "remote:path");
        var safety = new Safety();
        var backend = new Backend(expected);
        var supervisor = new LinuxMountSupervisor(safety, backend);
        safety.Identity = new DirectoryIdentity(spec.Source.Path, spec.Source.Device, 99, spec.Parent.MountId);
        backend.Mount = expected;
        try { await supervisor.MountAsync(spec, "remote:path"); throw new Exception("existing mount accepted"); }
        catch (InvalidOperationException ex) when (ex.Message == "mount-already-present") { }
        if (backend.Starts != 0) throw new Exception("started over existing mount");
        backend.Mount = null;
        safety.Nonempty = true;
        try { await supervisor.MountAsync(spec, "remote:path"); throw new Exception("nonempty mountpoint accepted"); }
        catch (InvalidOperationException ex) when (ex.Message == "mountpoint-not-empty") { }
        if (backend.Starts != 0) throw new Exception("started over nonempty path");
        safety.Nonempty = false;
        var token = await supervisor.MountAsync(spec, "remote:path");
        var recorded = MountIdentityCodec.Decode(token, spec.BootId, spec.MountNamespace, "remote:path", spec.Source.Path);
        if (recorded != expected with { Unit = $"slipmat-vfs-{spec.JobId}.service", ProcessId = 123, ProcessStartTicks = 456, Cgroup = "/system.slice/test", MountPointIdentity = safety.Identity } || backend.Starts != 1 || safety.Reads != 1)
            throw new Exception($"managed mount failed: recorded={recorded}; expected={expected with { Unit = $"slipmat-vfs-{spec.JobId}.service", ProcessId = 123, ProcessStartTicks = 456, Cgroup = "/system.slice/test", MountPointIdentity = safety.Identity }}; starts={backend.Starts}; reads={safety.Reads}");
        if (recorded.MountPointIdentity != safety.Identity)
            throw new Exception("mountpoint directory identity was not durably recorded");
        backend.Mount = expected with { MountId = 45 };
        try { await supervisor.UnmountAsync(spec, "remote:path", token); throw new Exception("foreign mount accepted"); }
        catch (InvalidOperationException ex) when (ex.Message == "mount-identity-mismatch") { }
        if (backend.Unmounts != 0) throw new Exception("unmounted foreign mount");
        backend.Mount = expected;
        backend.ProcessTicks = 457;
        try { await supervisor.UnmountAsync(spec, "remote:path", token); throw new Exception("replacement process accepted"); }
        catch (InvalidOperationException ex) when (ex.Message == "mount-identity-mismatch") { }
        if (backend.Unmounts != 0) throw new Exception("unmounted after process replacement");
        backend.ProcessTicks = 456;
        backend.Owned = false;
        try { await supervisor.UnmountAsync(spec, "remote:path", token); throw new Exception("foreign unit accepted"); }
        catch (InvalidOperationException ex) when (ex.Message == "mount-identity-mismatch") { }
        backend.Owned = true;
        backend.Busy = true;
        try { await supervisor.UnmountAsync(spec, "remote:path", token); throw new Exception("busy mount accepted"); }
        catch (InvalidOperationException ex) when (ex.Message == "unmount-failed-or-busy") { }
        if (backend.Mount != expected) throw new Exception("busy mount was removed");
        backend.Busy = false;
        await supervisor.UnmountAsync(spec, "remote:path", token);
        if (backend.Mount is not null) throw new Exception("mount not removed");
        if (safety.EmptyChecks != 2) throw new Exception("underlying mountpoint was not checked before mount and after unmount");
        var replacementToken = await supervisor.MountAsync(spec, "remote:path");
        backend.AfterUnmount = () => safety.Identity = safety.Identity! with { Inode = 100 };
        try { await supervisor.UnmountAsync(spec, "remote:path", replacementToken); throw new Exception("replacement mountpoint accepted after unmount"); }
        catch (InvalidOperationException ex) when (ex.Message == "mountpoint-identity-mismatch") { }
        if (backend.Unmounts != 3) throw new Exception($"replacement mountpoint was not rejected after unmount: unmounts={backend.Unmounts}");
        if (backend.Mount is not null) throw new Exception("replacement mount unexpectedly remains mounted after rejection");
    }

    private sealed class Safety : IMountSafetyBoundary
    {
        public int Reads { get; private set; }
        public int EmptyChecks { get; private set; }
        public bool Nonempty { get; set; }
        public DirectoryIdentity? Identity { get; set; }
        public Task VerifyAsync(HostSpecification spec, CancellationToken ct) => Task.CompletedTask;
        public Task<DirectoryIdentity> PrepareEmptyMountpointAsync(HostSpecification spec, CancellationToken ct)
            => Nonempty
                ? throw new InvalidOperationException("mountpoint-not-empty")
                : Task.FromResult(Identity ?? throw new InvalidOperationException("mountpoint-identity-unavailable"));
        public Task VerifyEmptyMountpointAsync(HostSpecification spec, DirectoryIdentity expected, CancellationToken ct)
        {
            EmptyChecks++;
            if (Nonempty || Identity != expected) throw new InvalidOperationException("mountpoint-identity-mismatch");
            return Task.CompletedTask;
        }
        public Task VerifyServiceReadAsync(HostSpecification spec, CancellationToken ct) { Reads++; return Task.CompletedTask; }
    }

    private sealed class Backend(MountInstance expected) : IMountSystemBackend
    {
        public MountInstance? Mount { get; set; }
        public bool Owned { get; set; } = true;
        public bool Busy { get; set; }
        public ulong ProcessTicks { get; set; } = 456;
        public int Starts { get; private set; }
        public int Unmounts { get; private set; }
        public Action? AfterUnmount { get; set; }
        public Task<bool> UnitAbsentAsync(string unit, CancellationToken ct) => Task.FromResult(Mount is null);
        public Task<bool> UnitStoppedAsync(string unit, CancellationToken ct) => Task.FromResult(Mount is null);
        public Task<ManagedMountProcess?> GetManagedProcessAsync(string unit, HostSpecification spec, string remoteSpec, CancellationToken ct)
            => Task.FromResult(Owned ? new ManagedMountProcess(unit, 123, ProcessTicks, "/system.slice/test") : null);
        public Task<MountInstance?> InspectAsync(HostSpecification spec, CancellationToken ct) => Task.FromResult(Mount);
        public Task StartAsync(HostSpecification spec, string unit, string remoteSpec, CancellationToken ct) { Starts++; Mount = expected; return Task.CompletedTask; }
        public Task UnmountAsync(string path, CancellationToken ct)
        {
            Unmounts++;
            if (Busy) throw new InvalidOperationException("unmount-failed-or-busy");
            Mount = null;
            AfterUnmount?.Invoke();
            return Task.CompletedTask;
        }
    }
}
