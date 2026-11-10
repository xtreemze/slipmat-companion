using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Slipmat.MaintenanceRunner;

/// <summary>Read-only systemd and kernel observations; this is not a journal or mount check.</summary>
public sealed record LinuxBarrierSnapshot(bool IsLinuxRoot, bool PersistentMaskTrusted, string ActiveState, int MainPid, int ControlPid, int[] CgroupPids, string BootId, string RunnerNamespace, string Pid1Namespace);

/// <summary>Injectable snapshot provider for the host barrier.</summary>
public interface ILinuxBarrierQuery
{
    Task<LinuxBarrierSnapshot> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>Verifies service isolation; callers must also hold the journal lease and validate custody at each effect.</summary>
public sealed class LinuxMaintenanceBarrier(ILinuxBarrierQuery query)
{
    /// <summary>Verify the persistently masked, stopped maintenance state.</summary>
    public async Task VerifyMaintenanceAsync(HostSpecification spec, CancellationToken cancellationToken)
    {
        var state = await query.ReadAsync(cancellationToken).ConfigureAwait(false);
        Common(spec, state);
        if (!state.PersistentMaskTrusted || state.ActiveState != "inactive" || state.MainPid != 0 || state.ControlPid != 0 || state.CgroupPids.Length != 0)
            throw new InvalidOperationException("maintenance-service-barrier-failed");
    }

    /// <summary>Verify ExecStartPre invocation; stable journal and mounts must be checked independently.</summary>
    public async Task VerifyStartupAsync(HostSpecification spec, int guardPid, CancellationToken cancellationToken)
    {
        var state = await query.ReadAsync(cancellationToken).ConfigureAwait(false);
        Common(spec, state);
        if (guardPid <= 0 || state.ActiveState != "activating" || state.MainPid != 0 || state.ControlPid != guardPid
            || state.CgroupPids.Any(pid => pid != guardPid) || !state.CgroupPids.Contains(guardPid))
            throw new InvalidOperationException("startup-service-barrier-failed");
    }

    private static void Common(HostSpecification spec, LinuxBarrierSnapshot state)
    {
        if (!state.IsLinuxRoot || spec.Unit != "jellyfin.service" || state.BootId != spec.BootId
            || state.RunnerNamespace != spec.MountNamespace || state.Pid1Namespace != spec.MountNamespace)
            throw new InvalidOperationException("host-identity-barrier-failed");
    }
}

/// <summary>Fixed-command Linux systemd query. All failures propagate and must block operations.</summary>
public sealed class LinuxSystemdBarrierQuery : ILinuxBarrierQuery
{
    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    /// <inheritdoc />
    public async Task<LinuxBarrierSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux() || GetEffectiveUserId() != 0) throw new InvalidOperationException("linux-root-required");
        var trusted = await TrustedMaskAsync(cancellationToken).ConfigureAwait(false);
        var output = await RunAsync("/usr/bin/systemctl", ["show", "jellyfin.service", "--property=ActiveState", "--property=MainPID", "--property=ControlPID", "--property=ControlGroup"], cancellationToken).ConfigureAwait(false);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = line.IndexOf('=');
            if (index <= 0 || !fields.TryAdd(line[..index], line[(index + 1)..])) throw new InvalidOperationException("systemd-query-invalid");
        }
        if (!fields.TryGetValue("ActiveState", out var active) || !fields.TryGetValue("MainPID", out var main) || !int.TryParse(main, out var mainPid)
            || !fields.TryGetValue("ControlPID", out var control) || !int.TryParse(control, out var controlPid) || !fields.TryGetValue("ControlGroup", out var group))
            throw new InvalidOperationException("systemd-query-invalid");
        var pids = new List<int>();
        if (group.Length != 0)
        {
            if (!group.StartsWith('/') || group.Contains("..") || group.Contains('\\')) throw new InvalidOperationException("invalid-cgroup");
            var dir = "/sys/fs/cgroup" + group;
            if (!Directory.Exists(dir)) throw new InvalidOperationException("cgroup-missing");
            foreach (var file in Directory.EnumerateFiles(dir, "cgroup.procs", SearchOption.AllDirectories).Prepend(Path.Combine(dir, "cgroup.procs")).Distinct())
                foreach (var line in await File.ReadAllLinesAsync(file, cancellationToken).ConfigureAwait(false))
                    if (int.TryParse(line, out var pid) && pid > 0) pids.Add(pid); else throw new InvalidOperationException("invalid-cgroup-pid");
        }
        return new(true, trusted, active, mainPid, controlPid, pids.Distinct().ToArray(),
            (await File.ReadAllTextAsync("/proc/sys/kernel/random/boot_id", cancellationToken).ConfigureAwait(false)).Trim(),
            new FileInfo("/proc/self/ns/mnt").LinkTarget ?? "", new FileInfo("/proc/1/ns/mnt").LinkTarget ?? "");
    }

    private static async Task<bool> TrustedMaskAsync(CancellationToken ct)
    {
        const string mask = "/etc/systemd/system/jellyfin.service";
        if (new FileInfo(mask).LinkTarget != "/dev/null") return false;
        foreach (var path in new[] { "/", "/etc", "/etc/systemd", "/etc/systemd/system", mask })
        {
            var text = (await RunAsync("/usr/bin/stat", ["-c", "%u %a %F", "--", path], ct).ConfigureAwait(false)).Trim().Split(' ', 3);
            if (text.Length != 3 || text[0] != "0" || !int.TryParse(text[1], System.Globalization.NumberStyles.None, null, out _)) return false;
            int mode;
            try { mode = Convert.ToInt32(text[1], 8); } catch (FormatException) { return false; }
            if (path != mask && (text[2] != "directory" || (mode & 0x12) != 0)) return false;
            if (path == mask && (text[2] != "symbolic link" || new FileInfo(mask).LinkTarget != "/dev/null")) return false;
        }
        return true;
    }

    private static async Task<string> RunAsync(string executable, string[] arguments, CancellationToken ct)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        info.Environment.Clear(); info.Environment["LC_ALL"] = "C"; info.Environment["PATH"] = "/usr/bin:/bin";
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("host-query-unavailable");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException("host-query-failed");
            return await stdout.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { try { process.Kill(true); } catch (InvalidOperationException) { } throw; }
    }
}
