using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;

namespace Slipmat.MaintenanceRunner;

/// <summary>Read-only downloaded two-way check against a stable admitted source.</summary>
public static class HostVerification
{
    /// <summary>Must run with a continuous host barrier and held whole-job journal lease.</summary>
    public static async Task VerifyAsync(CloudMigrationJob job, HostSpecification spec, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("linux-required");
        if (job.MaintenanceAdmission is not { } admission || job.Id != spec.JobId
            || job.Phase != CloudMigrationPhases.ReadyForCutover || job.Verification != "rclone-check-download-content"
            || job.Direction != CloudMigrationDirections.LocalToVfs || job.LocalPath != admission.LocalPath
            || job.ProfileId != admission.ProfileId || job.LibraryName != admission.LibraryName
            || job.RemoteName != admission.RemoteName || job.RemotePath != admission.RemotePath
            || job.ManifestDigestSha256 != admission.ManifestDigestSha256 || admission.Verification != job.Verification
            || job.ManifestVersion != CloudMigrationManifestBuilder.CurrentVersion || job.ManifestFileCount is null || job.ManifestTotalBytes is null
            || !Regex.IsMatch(admission.RemoteName, @"\A[A-Za-z0-9_][A-Za-z0-9_. -]*\z", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("verification-admission-mismatch");
        _ = LinuxTrustedFile.Read("/usr/bin/setpriv", 16 * 1024 * 1024);
        _ = LinuxTrustedFile.Read(spec.RcloneConfigPath, 4 * 1024 * 1024);
        var actualHash = Convert.ToHexString(SHA256.HashData(LinuxTrustedFile.Read(spec.RclonePath, 256 * 1024 * 1024)));
        if (!string.Equals(actualHash, spec.RcloneSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("rclone-binary-changed");
        var before = await CloudMigrationManifestBuilder.CaptureAsync(admission.LocalPath, cancellationToken).ConfigureAwait(false);
        Match(job, before);
        var info = new ProcessStartInfo("/usr/bin/setpriv") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        info.Environment.Clear();
        info.Environment["PATH"] = "/usr/bin:/bin";
        info.Environment["RCLONE_CONFIG"] = spec.RcloneConfigPath;
        string[] args = ["--reuid=" + spec.ServiceUid, "--regid=" + spec.ServiceGid, "--clear-groups", "--", spec.RclonePath,
            "check", admission.LocalPath, admission.RemoteName + ":" + admission.RemotePath, "--download", "--stats=0", "--log-level=ERROR"];
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("verification-start-failed");
        // Drain both pipes without retaining provider diagnostics or credentials.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(spec.VerificationBudgetMinutes));
        var stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        if (process.ExitCode != 0) throw new InvalidOperationException("download-verification-failed");
        var after = await CloudMigrationManifestBuilder.CaptureAsync(admission.LocalPath, cancellationToken).ConfigureAwait(false);
        Match(job, after);
        if (before.DigestSha256 != after.DigestSha256 || before.FileCount != after.FileCount || before.TotalBytes != after.TotalBytes)
            throw new InvalidOperationException("source-changed-during-verification");
    }

    private static void Match(CloudMigrationJob job, CloudMigrationManifest manifest)
    {
        if (!string.Equals(job.ManifestDigestSha256, manifest.DigestSha256, StringComparison.OrdinalIgnoreCase)
            || job.ManifestFileCount != manifest.FileCount || job.ManifestTotalBytes != manifest.TotalBytes
            || job.ManifestVersion != manifest.Version)
            throw new InvalidOperationException("source-manifest-changed");
    }
}
