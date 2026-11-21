using System.Diagnostics;
using System.Globalization;

namespace Slipmat.MaintenanceRunner;

/// <summary>Host-owned safeguards; implementations must keep the maintenance barrier held until every child action is settled.</summary>
public interface IMountSafetyBoundary
{
    Task VerifyAsync(HostSpecification spec, CancellationToken cancellationToken);
    Task<DirectoryIdentity> PrepareEmptyMountpointAsync(HostSpecification spec, CancellationToken cancellationToken);
    Task VerifyEmptyMountpointAsync(HostSpecification spec, DirectoryIdentity expected, CancellationToken cancellationToken);
    Task VerifyServiceReadAsync(HostSpecification spec, CancellationToken cancellationToken);
}

/// <summary>OS operations for a named managed foreground rclone service.</summary>
public interface IMountSystemBackend
{
    Task<bool> UnitAbsentAsync(string unit, CancellationToken cancellationToken);
    Task<bool> UnitStoppedAsync(string unit, CancellationToken cancellationToken);
    Task<ManagedMountProcess?> GetManagedProcessAsync(string unit, HostSpecification spec, string remoteSpec, CancellationToken cancellationToken);
    Task<MountInstance?> InspectAsync(HostSpecification spec, CancellationToken cancellationToken);
    Task StartAsync(HostSpecification spec, string unit, string remoteSpec, CancellationToken cancellationToken);
    Task UnmountAsync(string path, CancellationToken cancellationToken);
}

/// <summary>Systemd MainPID scoped to a kernel process lifetime and control group.</summary>
public sealed record ManagedMountProcess(string Unit, int ProcessId, ulong StartTicks, string Cgroup);

/// <summary>Managed foreground mount with exact instance and unit checks. Marker persistence belongs to the caller.</summary>
public sealed class LinuxMountSupervisor(IMountSafetyBoundary safety, IMountSystemBackend system)
{
    public async Task<string> MountAsync(HostSpecification spec, string remoteSpec, CancellationToken cancellationToken = default)
    {
        var unit = UnitName(spec);
        ValidateRemote(remoteSpec);
        await safety.VerifyAsync(spec, cancellationToken).ConfigureAwait(false);
        if (!await system.UnitAbsentAsync(unit, cancellationToken).ConfigureAwait(false)
            || await system.InspectAsync(spec, cancellationToken).ConfigureAwait(false) is not null)
            throw new InvalidOperationException("mount-already-present");
        var mountpoint = await safety.PrepareEmptyMountpointAsync(spec, cancellationToken).ConfigureAwait(false);
        ValidateMountpointIdentity(spec, mountpoint);
        await safety.VerifyAsync(spec, cancellationToken).ConfigureAwait(false);
        if (!await system.UnitAbsentAsync(unit, cancellationToken).ConfigureAwait(false)
            || await system.InspectAsync(spec, cancellationToken).ConfigureAwait(false) is not null)
            throw new InvalidOperationException("mount-state-changed");
        await safety.VerifyEmptyMountpointAsync(spec, mountpoint, cancellationToken).ConfigureAwait(false);
        // Once submitted to PID 1, caller cancellation cannot revoke the start job. Let the
        // bounded helper finish, then inspect under the barrier before releasing the lease.
        cancellationToken.ThrowIfCancellationRequested();
        await system.StartAsync(spec, unit, remoteSpec, CancellationToken.None).ConfigureAwait(false);
        await safety.VerifyAsync(spec, CancellationToken.None).ConfigureAwait(false);
        _ = await system.InspectAsync(spec, CancellationToken.None).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        for (var attempt = 0; attempt < 200; attempt++)
        {
            await safety.VerifyAsync(spec, cancellationToken).ConfigureAwait(false);
            var mount = await system.InspectAsync(spec, cancellationToken).ConfigureAwait(false);
            if (mount is not null)
            {
                var owner = await system.GetManagedProcessAsync(unit, spec, remoteSpec, cancellationToken).ConfigureAwait(false);
                if (mount.BootId != spec.BootId || mount.Namespace != spec.MountNamespace
                    || mount.FileSystem != "fuse.rclone" || mount.Source != remoteSpec || owner is null)
                    throw new InvalidOperationException("mount-identity-mismatch");
                await safety.VerifyServiceReadAsync(spec, cancellationToken).ConfigureAwait(false);
                await safety.VerifyAsync(spec, cancellationToken).ConfigureAwait(false);
                if (await system.InspectAsync(spec, cancellationToken).ConfigureAwait(false) != mount
                    || await system.GetManagedProcessAsync(unit, spec, remoteSpec, cancellationToken).ConfigureAwait(false) != owner)
                    throw new InvalidOperationException("mount-identity-mismatch");
                return MountIdentityCodec.Encode(mount with { Unit = owner.Unit, ProcessId = owner.ProcessId, ProcessStartTicks = owner.StartTicks, Cgroup = owner.Cgroup, MountPointIdentity = mountpoint });
            }
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        throw new InvalidOperationException("mount-not-ready");
    }

    public async Task UnmountAsync(HostSpecification spec, string remoteSpec, string token, CancellationToken cancellationToken = default)
    {
        ValidateRemote(remoteSpec);
        var expected = MountIdentityCodec.Decode(token, spec.BootId, spec.MountNamespace, remoteSpec, spec.Source.Path);
        var expectedMount = expected with { Unit = null, ProcessId = 0, ProcessStartTicks = 0, Cgroup = null, MountPointIdentity = null };
        var expectedOwner = new ManagedMountProcess(UnitName(spec), expected.ProcessId, expected.ProcessStartTicks, expected.Cgroup!);
        if (expected.Unit != expectedOwner.Unit) throw new InvalidOperationException("mount-identity-mismatch");
        await safety.VerifyAsync(spec, cancellationToken).ConfigureAwait(false);
        if (await system.InspectAsync(spec, cancellationToken).ConfigureAwait(false) != expectedMount
            || await system.GetManagedProcessAsync(UnitName(spec), spec, remoteSpec, cancellationToken).ConfigureAwait(false) != expectedOwner)
            throw new InvalidOperationException("mount-identity-mismatch");
        await safety.VerifyAsync(spec, cancellationToken).ConfigureAwait(false);
        if (await system.InspectAsync(spec, cancellationToken).ConfigureAwait(false) != expectedMount
            || await system.GetManagedProcessAsync(UnitName(spec), spec, remoteSpec, cancellationToken).ConfigureAwait(false) != expectedOwner)
            throw new InvalidOperationException("mount-identity-mismatch");
        await system.UnmountAsync(spec.Source.Path, cancellationToken).ConfigureAwait(false);
        await safety.VerifyAsync(spec, cancellationToken).ConfigureAwait(false);
        if (await system.InspectAsync(spec, cancellationToken).ConfigureAwait(false) is not null)
            throw new InvalidOperationException("unmount-not-confirmed");
        for (var attempt = 0; attempt < 200; attempt++)
        {
            await safety.VerifyAsync(spec, cancellationToken).ConfigureAwait(false);
            if (await system.InspectAsync(spec, cancellationToken).ConfigureAwait(false) is not null)
                throw new InvalidOperationException("mount-state-changed");
            if (await system.UnitStoppedAsync(UnitName(spec), cancellationToken).ConfigureAwait(false))
            {
                await safety.VerifyEmptyMountpointAsync(spec, expected.MountPointIdentity!, cancellationToken).ConfigureAwait(false);
                return;
            }
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        throw new InvalidOperationException("mount-unit-still-active");
    }

    private static string UnitName(HostSpecification spec)
    {
        if (!Guid.TryParseExact(spec.JobId, "N", out var id) || id == Guid.Empty) throw new InvalidOperationException("invalid-job-id");
        return $"slipmat-vfs-{id:N}.service";
    }

    private static void ValidateRemote(string remote)
    {
        if (string.IsNullOrWhiteSpace(remote) || remote.StartsWith('-') || remote.IndexOfAny(['\0', '\r', '\n']) >= 0 || !remote.Contains(':'))
            throw new InvalidOperationException("invalid-remote");
    }

    private static void ValidateMountpointIdentity(HostSpecification spec, DirectoryIdentity identity)
    {
        if (identity.Path != spec.Source.Path || identity.Device != spec.Source.Device || identity.Inode == 0
            || identity.MountId != spec.Parent.MountId)
            throw new InvalidOperationException("mountpoint-identity-mismatch");
    }
}

/// <summary>Linux systemd and mountinfo implementation; fixed tools must be verified by the host boundary.</summary>
public sealed class LinuxSystemdMountBackend : IMountSystemBackend
{
    public async Task<bool> UnitAbsentAsync(string unit, CancellationToken cancellationToken)
    {
        var (_, output) = await RunAsync("/usr/bin/systemctl", ["show", unit, "--property=LoadState", "--value"], cancellationToken).ConfigureAwait(false);
        return output.Trim() == "not-found";
    }

    public async Task<bool> UnitStoppedAsync(string unit, CancellationToken cancellationToken)
    {
        var (_, output) = await RunAsync("/usr/bin/systemctl", ["show", unit, "--property=LoadState", "--property=ActiveState", "--property=MainPID"], cancellationToken).ConfigureAwait(false);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Contains("MainPID=0") && (lines.Contains("ActiveState=inactive") || lines.Contains("LoadState=not-found"));
    }

    public async Task<ManagedMountProcess?> GetManagedProcessAsync(string unit, HostSpecification spec, string remoteSpec, CancellationToken cancellationToken)
    {
        var (exit, output) = await RunAsync("/usr/bin/systemctl", ["show", unit, "--property=ActiveState", "--property=MainPID", "--property=Transient", "--property=ControlGroup"], cancellationToken).ConfigureAwait(false);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var pidLine = lines.SingleOrDefault(line => line.StartsWith("MainPID=", StringComparison.Ordinal));
        var cgroupLine = lines.SingleOrDefault(line => line.StartsWith("ControlGroup=", StringComparison.Ordinal));
        if (exit != 0 || !lines.Contains("ActiveState=active") || !lines.Contains("Transient=yes") || pidLine is null
            || !int.TryParse(pidLine.AsSpan(8), out var pid) || pid <= 0 || cgroupLine is null || cgroupLine.Length <= 13) return null;
        try
        {
            var args = (await File.ReadAllTextAsync($"/proc/{pid}/cmdline", cancellationToken).ConfigureAwait(false)).Split('\0', StringSplitOptions.RemoveEmptyEntries);
            var status = await File.ReadAllLinesAsync($"/proc/{pid}/status", cancellationToken).ConfigureAwait(false);
            var environment = (await File.ReadAllTextAsync($"/proc/{pid}/environ", cancellationToken).ConfigureAwait(false)).Split('\0', StringSplitOptions.RemoveEmptyEntries);
            var uid = status.SingleOrDefault(line => line.StartsWith("Uid:", StringComparison.Ordinal));
            var uids = uid?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var cgroup = cgroupLine[13..];
            var procCgroups = await File.ReadAllLinesAsync($"/proc/{pid}/cgroup", cancellationToken).ConfigureAwait(false);
            var stat = await File.ReadAllTextAsync($"/proc/{pid}/stat", cancellationToken).ConfigureAwait(false);
            var endName = stat.LastIndexOf(')');
            var fields = endName < 0 ? [] : stat[(endName + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length <= 19 || !ulong.TryParse(fields[19], out var startTicks) || startTicks == 0
                || !procCgroups.Contains("0::" + cgroup)) return null;
            var matches = File.ResolveLinkTarget($"/proc/{pid}/exe", true)?.FullName == spec.RclonePath
                && File.ResolveLinkTarget($"/proc/{pid}/ns/mnt", false)?.Name == spec.MountNamespace
                && uids is { Length: 5 } && uids.Skip(1).All(value => value == "0")
                && args.Skip(1).SequenceEqual(RcloneArguments(spec, remoteSpec))
                && environment.Count(value => value.StartsWith("RCLONE_CONFIG=", StringComparison.Ordinal)) == 1
                && environment.Contains("RCLONE_CONFIG=" + spec.RcloneConfigPath)
                && !environment.Any(value => (value.StartsWith("RCLONE_", StringComparison.Ordinal) && !value.StartsWith("RCLONE_CONFIG=", StringComparison.Ordinal))
                    || value.StartsWith("LD_", StringComparison.Ordinal));
            return matches ? new ManagedMountProcess(unit, pid, startTicks, cgroup) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public Task<MountInstance?> InspectAsync(HostSpecification spec, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var matches = new List<MountInstance>();
        foreach (var line in File.ReadLines("/proc/self/mountinfo"))
        {
            var parts = line.Split(" - ", 2, StringSplitOptions.None);
            if (parts.Length != 2) continue;
            var left = parts[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var right = parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (left.Length < 5 || right.Length < 2 || Decode(left[4]) != spec.Source.Path) continue;
            var device = left[2].Split(':');
            if (!int.TryParse(left[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0
                || device.Length != 2 || !uint.TryParse(device[0], out var major) || !uint.TryParse(device[1], out var minor))
                throw new InvalidOperationException("invalid-mountinfo");
            matches.Add(new MountInstance(1, spec.BootId, spec.MountNamespace, id, major, minor, right[0], Decode(right[1])));
        }
        if (matches.Count > 1) throw new InvalidOperationException("stacked-mounts");
        return Task.FromResult(matches.SingleOrDefault());
    }

    public async Task StartAsync(HostSpecification spec, string unit, string remoteSpec, CancellationToken cancellationToken)
    {
        var args = new List<string> { "--unit=" + unit, "--property=Type=exec", "--property=Restart=no", "--setenv=RCLONE_CONFIG=" + spec.RcloneConfigPath, "--", spec.RclonePath };
        args.AddRange(RcloneArguments(spec, remoteSpec));
        var (exit, _) = await RunAsync("/usr/bin/systemd-run", args, cancellationToken).ConfigureAwait(false);
        if (exit != 0) throw new InvalidOperationException("mount-start-failed");
    }

    private static List<string> RcloneArguments(HostSpecification spec, string remoteSpec)
        => [
            "mount", remoteSpec, spec.Source.Path, "--devname=" + remoteSpec, "--read-only", "--allow-other", "--uid=" + spec.ServiceUid, "--gid=" + spec.ServiceGid,
            "--vfs-cache-mode=full", "--cache-dir=" + spec.CachePath, "--vfs-cache-max-size=" + spec.CacheMaxSizeGiB + "G", "--vfs-cache-max-age=" + spec.CacheMaxAgeHours + "h",
            "--vfs-cache-min-free-space=" + spec.CacheMinFreeGiB + "G", "--vfs-cache-poll-interval=1m", "--dir-cache-time=5m", "--poll-interval=1m",
            "--buffer-size=0", "--vfs-read-ahead=0", "--vfs-read-chunk-size=4M", "--vfs-read-chunk-size-limit=4M", "--stats=0", "--log-level=ERROR" ];

    public async Task UnmountAsync(string path, CancellationToken cancellationToken)
    {
        var (exit, _) = await RunAsync("/usr/bin/fusermount3", ["-u", path], cancellationToken).ConfigureAwait(false);
        if (exit != 0) throw new InvalidOperationException("unmount-failed-or-busy");
    }

    private static async Task<(int Exit, string Output)> RunAsync(string executable, IEnumerable<string> args, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo { FileName = executable, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        if (!process.Start()) throw new InvalidOperationException("mount-helper-unavailable");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await error.ConfigureAwait(false);
            return (process.ExitCode, await output.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Reap the helper; PID 1 may already have accepted its job, so the
            // caller must still treat a timed-out start as ambiguous custody.
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static string Decode(string value) => value.Replace("\\040", " ", StringComparison.Ordinal).Replace("\\011", "\t", StringComparison.Ordinal).Replace("\\012", "\n", StringComparison.Ordinal).Replace("\\134", "\\", StringComparison.Ordinal);
}
