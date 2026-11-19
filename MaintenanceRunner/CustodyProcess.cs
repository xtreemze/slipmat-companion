using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Jellyfin.Plugin.AudioGateway.Models;

namespace Slipmat.MaintenanceRunner;

/// <summary>Invokes the root-controlled descriptor-pinned custody helper with bounded JSON input.</summary>
public static class CustodyProcess
{
    /// <summary>The caller must hold the journal lease and revalidate host barrier immediately before invocation.</summary>
    public static async Task RenameAsync(HostSpecification spec, string source, string destination, CancellationToken cancellationToken)
    {
        var localLeaf = Path.GetFileName(spec.Source.Path);
        var backupLeaf = Path.GetFileName(spec.BackupPath);
        if (!IsLeaf(source) || !IsLeaf(destination)
            || !((source == localLeaf && destination == backupLeaf) || (source == backupLeaf && destination == localLeaf)))
            throw new InvalidOperationException("invalid-custody-leaf");
        _ = await InvokeAsync(spec, new { action = "rename", parent = spec.Parent.Path, source, destination, device = spec.Source.Device, inode = spec.Source.Inode }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The caller must rederive replacement bytes and hold the journal lease and host barrier.</summary>
    public static async Task ReplaceConfigurationAsync(HostSpecification spec, ConfigurationFileSpecification config, byte[] replacement, bool promote, CancellationToken cancellationToken)
    {
        if (config != spec.Profile && config != spec.Policy) throw new InvalidOperationException("configuration-not-bound");
        var before = promote ? config.BeforeHash : config.AfterHash;
        var after = promote ? config.AfterHash : config.BeforeHash;
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(replacement)), after, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("configuration-target-hash-mismatch");
        _ = await InvokeAsync(spec, new { action = "replace-config", parent = Path.GetDirectoryName(config.Path), name = Path.GetFileName(config.Path), beforeHash = before, afterHash = after, replacementBase64 = Convert.ToBase64String(replacement) }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Remove an exact managed marker and empty unmounted projection under the host barrier.</summary>
    public static async Task RemoveEmptyProjectionAsync(HostSpecification spec, string remoteName, string remoteType, string remotePath, CancellationToken cancellationToken)
    {
        var source = Path.GetFileName(spec.Source.Path);
        if (!IsLeaf(source)) throw new InvalidOperationException("invalid-custody-leaf");
        _ = await InvokeAsync(spec, new { action = "remove-empty-projection", parent = spec.Parent.Path, source,
            marker = source + ".slipmat-rclone-cloud-mount.json", remoteName, remoteType, remotePath }, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<DirectoryIdentity> CreateEmptyMountpointAsync(HostSpecification spec, CancellationToken cancellationToken)
    {
        var identity = await InvokeIdentityAsync(spec, new
        {
            action = "create-empty-mountpoint", parent = spec.Parent.Path,
            source = Path.GetFileName(spec.Source.Path), device = spec.Source.Device, mountId = spec.Parent.MountId,
        }, cancellationToken).ConfigureAwait(false);
        ValidateMountpoint(spec, identity);
        return identity;
    }

    public static async Task VerifyEmptyMountpointAsync(HostSpecification spec, DirectoryIdentity expected, CancellationToken cancellationToken)
    {
        ValidateMountpoint(spec, expected);
        var actual = await InvokeIdentityAsync(spec, new
        {
            action = "verify-empty-mountpoint", parent = spec.Parent.Path,
            source = Path.GetFileName(spec.Source.Path), device = expected.Device,
            mountId = expected.MountId, inode = expected.Inode,
        }, cancellationToken).ConfigureAwait(false);
        if (actual != expected) throw new InvalidOperationException("mountpoint-identity-mismatch");
    }

    private static async Task<DirectoryIdentity> InvokeIdentityAsync(HostSpecification spec, object request, CancellationToken cancellationToken)
    {
        var output = await InvokeAsync(spec, request, cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(output);
            if (document.RootElement.GetProperty("status").GetString() != "ok") throw new JsonException();
            return JsonSerializer.Deserialize<DirectoryIdentity>(document.RootElement.GetProperty("identity").GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new JsonException();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new InvalidOperationException("mountpoint-identity-invalid");
        }
    }

    private static void ValidateMountpoint(HostSpecification spec, DirectoryIdentity identity)
    {
        if (identity.Path != spec.Source.Path || identity.Device != spec.Source.Device || identity.Inode == 0
            || identity.MountId != spec.Parent.MountId)
            throw new InvalidOperationException("mountpoint-identity-mismatch");
    }

    // The offline transaction must bind these inputs to the journal and hold the host barrier.
    internal static async Task<CloudSourcePathRepairIdentity> ArchiveAsync(
        string pythonPath, string pythonSha256, string helperPath, string helperSha256,
        string archiveParent, string archiveLeaf, CloudSourcePathRepairIdentity archiveParentIdentity,
        CloudSourcePathRepairIdentity? archiveIdentity, byte[] manifest, string manifestSha256,
        string? sourceRoot, CloudSourcePathRepairIdentity? sourceIdentity, CancellationToken cancellationToken)
    {
        var copy = sourceRoot is not null;
        if (copy != sourceIdentity.HasValue || (!copy && archiveIdentity is null)
            || manifest.Length is 0 or > 4 * 1024 * 1024 || !HostSpecification.IsValidHash(manifestSha256)
            || !Convert.ToHexString(SHA256.HashData(manifest)).Equals(manifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("source-repair-archive-request-invalid");

        var request = new Dictionary<string, object?>
        {
            ["action"] = copy ? "source-repair-archive-create-or-resume" : "source-repair-archive-verify",
            ["archiveParent"] = archiveParent,
            ["archiveLeaf"] = archiveLeaf,
            ["archiveParentIdentity"] = archiveParentIdentity,
            ["archiveIdentity"] = archiveIdentity,
            ["manifestBase64"] = Convert.ToBase64String(manifest),
            ["manifestSha256"] = manifestSha256,
        };
        if (copy)
        {
            request["sourceRoot"] = sourceRoot;
            request["sourceIdentity"] = sourceIdentity;
        }
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var payload = JsonSerializer.SerializeToUtf8Bytes(request, options);
        if (payload.Length > 6 * 1024 * 1024)
            throw new InvalidOperationException("source-repair-archive-request-too-large");
        var output = await InvokeAsync(pythonPath, pythonSha256, helperPath, helperSha256, payload,
            cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(output);
            if (document.RootElement.GetProperty("status").GetString() != "ok") throw new JsonException();
            var actual = document.RootElement.GetProperty("identity").Deserialize<CloudSourcePathRepairIdentity>(options);
            if (actual.Inode == 0 || actual.MountId <= 0 || (archiveIdentity.HasValue && archiveIdentity.Value != actual))
                throw new JsonException();
            return actual;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new InvalidOperationException("source-repair-archive-identity-invalid", ex);
        }
    }

    private static Task<string> InvokeAsync(HostSpecification spec, object request, CancellationToken cancellationToken)
        => InvokeAsync(spec.PythonPath, spec.PythonSha256, spec.CustodyHelperPath, spec.CustodyHelperSha256,
            JsonSerializer.SerializeToUtf8Bytes(request), cancellationToken);

    private static async Task<string> InvokeAsync(string pythonPath, string pythonSha256, string helperPath,
        string helperSha256, byte[] request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CheckHash(pythonPath, pythonSha256, 128 * 1024 * 1024);
        CheckHash(helperPath, helperSha256, 1024 * 1024);
        var info = new ProcessStartInfo(pythonPath) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.Environment.Clear();
        info.Environment["PATH"] = "/usr/bin:/bin";
        info.ArgumentList.Add("-I");
        info.ArgumentList.Add(helperPath);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("custody-helper-start-failed");
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.BaseStream.CopyToAsync(Stream.Null, cancellationToken);
            await process.StandardInput.BaseStream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException("custody-helper-failed");
            return await output.ConfigureAwait(false);
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            // The caller may release the journal lease when we return. Wait for the
            // helper to exit even if the original request was canceled.
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static void CheckHash(string path, string expected, int limit)
    {
        var actual = Convert.ToHexString(SHA256.HashData(LinuxTrustedFile.Read(path, limit)));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("custody-tool-changed");
    }

    private static bool IsLeaf(string value) => !string.IsNullOrEmpty(value) && value is not "." and not ".." && !value.Contains('/') && !value.Contains('\0');
}
