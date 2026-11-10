using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Slipmat.MaintenanceRunner;

/// <summary>Resolve the admitted remote type without returning credentials or provider logs.</summary>
public static class RemoteTypeResolver
{
    public static async Task<string> ResolveAsync(HostSpecification spec, string remoteName, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("linux-required");
        _ = LinuxTrustedFile.Read("/usr/bin/setpriv", 16 * 1024 * 1024);
        _ = LinuxTrustedFile.Read(spec.RcloneConfigPath, 4 * 1024 * 1024);
        var binaryHash = Convert.ToHexString(SHA256.HashData(LinuxTrustedFile.Read(spec.RclonePath, 256 * 1024 * 1024)));
        if (!string.Equals(binaryHash, spec.RcloneSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("rclone-binary-changed");
        var info = new ProcessStartInfo("/usr/bin/setpriv") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        info.Environment.Clear(); info.Environment["PATH"] = "/usr/bin:/bin"; info.Environment["RCLONE_CONFIG"] = spec.RcloneConfigPath;
        foreach (var arg in new[] { "--reuid=" + spec.ServiceUid, "--regid=" + spec.ServiceGid, "--clear-groups", "--", spec.RclonePath, "listremotes", "--long", "--json" }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("remote-type-query-failed");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
            using var result = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await process.StandardOutput.BaseStream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
            {
                if (result.Length + count > 2 * 1024 * 1024) throw new InvalidOperationException("remote-type-output-too-large");
                result.Write(buffer, 0, count);
            }
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException("remote-type-query-failed");
            using var json = JsonDocument.Parse(result.ToArray());
            var root = json.RootElement;
            var array = root.ValueKind == JsonValueKind.Array ? root : root.GetProperty("remotes");
            var matches = array.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                && string.Equals(name.GetString()?.TrimEnd(':'), remoteName, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            if (matches.Length != 1 || !matches[0].TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(type.GetString())) throw new InvalidOperationException("remote-type-unavailable");
            return type.GetString()!;
        }
        catch (JsonException) { throw new InvalidOperationException("remote-type-query-failed"); }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
    }
}
