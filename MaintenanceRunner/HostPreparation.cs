using System.Security.Cryptography;
using System.Text.Json;
using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;

namespace Slipmat.MaintenanceRunner;

/// <summary>Prepare root-private snapshots and a domain-derived immutable host specification.</summary>
public static class HostPreparation
{
    /// <summary>
    /// The caller must hold the host barrier and journal lease and supply the unchanged admitted job.
    /// This creates new private files only; it never replaces a configuration, media file, or journal.
    /// </summary>
    public static (HostSpecification Specification, string Digest) Prepare(
        HostSpecification draft, CloudMigrationJob job, string specificationPath)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(job);
        var admission = job.MaintenanceAdmission ?? throw new InvalidOperationException("maintenance-admission-missing");
        var bundle = Path.GetDirectoryName(specificationPath);
        if (!Path.IsPathFullyQualified(specificationPath) || Path.GetFullPath(specificationPath) != specificationPath
            || bundle is null || bundle == "/" || Path.GetDirectoryName(draft.Profile.OriginalSnapshotPath) != bundle
            || Path.GetDirectoryName(draft.Policy.OriginalSnapshotPath) != bundle
            || specificationPath == draft.Profile.OriginalSnapshotPath || specificationPath == draft.Policy.OriginalSnapshotPath
            || bundle == admission.LocalPath || bundle.StartsWith(admission.LocalPath + "/", StringComparison.Ordinal)
            || bundle == draft.BackupPath || bundle.StartsWith(draft.BackupPath + "/", StringComparison.Ordinal))
            throw new InvalidOperationException("maintenance-bundle-path-invalid");
        if (job.Id != draft.JobId || job.Phase != CloudMigrationPhases.ReadyForCutover || job.Maintenance is not null
            || job.Direction != CloudMigrationDirections.LocalToVfs || job.LocalPath != admission.LocalPath
            || job.ProfileId != admission.ProfileId || job.LibraryName != admission.LibraryName
            || job.RemoteName != admission.RemoteName || job.RemotePath != admission.RemotePath
            || job.PreviousProjectionPath != admission.PreviousProjectionPath || job.PreviousEnabled != admission.PreviousEnabled
            || job.ManifestDigestSha256 != admission.ManifestDigestSha256
            || job.Verification != admission.Verification || admission.Verification != "rclone-check-download-content"
            || draft.Profile.Path != admission.PluginConfigurationPath || draft.Policy.Path != admission.OptionsPath)
            throw new InvalidOperationException("maintenance-admission-mismatch");
        var profileBytes = LinuxTrustedFile.Read(draft.Profile.Path, 4 * 1024 * 1024);
        var policyBytes = LinuxTrustedFile.Read(draft.Policy.Path, 4 * 1024 * 1024);
        var binding = new CloudMaintenanceProfileBinding(admission.ProfileId, admission.RemoteName,
            admission.RemotePath, admission.LibraryName, admission.PreviousProjectionPath,
            admission.PreviousEnabled, admission.LocalPath);
        var profile = CloudMaintenanceProfileConfiguration.PrepareProfile(profileBytes, admission.PluginConfigurationSha256, binding, true);
        var policy = CloudMaintenanceProfileConfiguration.PreparePolicy(policyBytes, admission.OptionsSha256,
            admission.LocalPath, admission.PreviousLibraryScanPolicy, true);
        var spec = draft with
        {
            Profile = draft.Profile with { BeforeHash = profile.BeforeHash, AfterHash = profile.AfterHash },
            Policy = draft.Policy with { BeforeHash = policy.BeforeHash, AfterHash = policy.AfterHash },
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(spec, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var custody = new CloudMaintenanceCustody(admission.LibraryId.ToString(), admission.LibraryName, admission.LocalPath,
            spec.BackupPath, admission.ProfileId, admission.RemoteName, admission.RemotePath, admission.ManifestDigestSha256,
            admission.Verification, digest);
        var validation = new CloudMaintenanceIntent(job.Id, CloudMaintenanceDirection.Promote,
            CloudMaintenancePhase.Prepared, "pending-host-identity", null, custody);
        HostSpecification.Parse(bytes, validation, admission);
        LinuxTrustedFile.CreateNew(spec.Profile.OriginalSnapshotPath, profileBytes);
        LinuxTrustedFile.CreateNew(spec.Policy.OriginalSnapshotPath, policyBytes);
        LinuxTrustedFile.CreateNew(specificationPath, bytes);
        return (spec, digest);
    }
}
