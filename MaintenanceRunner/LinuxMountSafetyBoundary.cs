using System.Diagnostics;
using System.Security.Cryptography;

namespace Slipmat.MaintenanceRunner;

/// <summary>Concrete systemd-barrier and descriptor-pinned mountpoint boundary for managed mounts.</summary>
public sealed class LinuxMountSafetyBoundary(LinuxMaintenanceBarrier barrier) : IMountSafetyBoundary
{
    public Task VerifyAsync(HostSpecification spec, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("linux-required");
        return barrier.VerifyMaintenanceAsync(spec, cancellationToken);
    }

    public async Task<DirectoryIdentity> PrepareEmptyMountpointAsync(HostSpecification spec, CancellationToken cancellationToken)
    {
        await VerifyAsync(spec, cancellationToken).ConfigureAwait(false);
        var identity = await CustodyProcess.CreateEmptyMountpointAsync(spec, cancellationToken).ConfigureAwait(false);
        await VerifyAsync(spec, cancellationToken).ConfigureAwait(false);
        return identity;
    }

    public async Task VerifyEmptyMountpointAsync(HostSpecification spec, DirectoryIdentity expected, CancellationToken cancellationToken)
    {
        await VerifyAsync(spec, cancellationToken).ConfigureAwait(false);
        await CustodyProcess.VerifyEmptyMountpointAsync(spec, expected, cancellationToken).ConfigureAwait(false);
        await VerifyAsync(spec, cancellationToken).ConfigureAwait(false);
    }

    public async Task VerifyServiceReadAsync(HostSpecification spec, CancellationToken cancellationToken)
    {
        await VerifyAsync(spec, cancellationToken).ConfigureAwait(false);
        var python = LinuxTrustedFile.Read(spec.PythonPath, 128 * 1024 * 1024);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(python)), spec.PythonSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("service-read-tool-changed");
        _ = LinuxTrustedFile.Read("/usr/bin/setpriv", 16 * 1024 * 1024);
        var info = new ProcessStartInfo("/usr/bin/setpriv")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.Environment.Clear();
        info.Environment["PATH"] = "/usr/bin:/bin";
        foreach (var argument in new[]
        {
            "--reuid=" + spec.ServiceUid,
            "--regid=" + spec.ServiceGid,
            "--clear-groups",
            "--",
            spec.PythonPath,
            "-I",
            "-c",
            "import os,sys; entries=os.scandir(sys.argv[1]); next(entries,None); entries.close()",
            spec.Source.Path,
        }) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("service-read-start-failed");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
            var error = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException("service-read-failed");
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        await VerifyAsync(spec, cancellationToken).ConfigureAwait(false);
    }
}
