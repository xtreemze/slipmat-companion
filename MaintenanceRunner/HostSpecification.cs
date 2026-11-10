using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.AudioGateway.Models;

namespace Slipmat.MaintenanceRunner;

/// <summary>A mount instance is scoped to one boot and mount namespace.</summary>
public sealed record MountInstance(int Version, string BootId, string Namespace, int MountId, uint Major, uint Minor, string FileSystem, string Source, string? Unit = null, int ProcessId = 0, ulong ProcessStartTicks = 0, string? Cgroup = null, DirectoryIdentity? MountPointIdentity = null);

/// <summary>Identity of a directory and its containing mount.</summary>
public sealed record DirectoryIdentity(string Path, ulong Device, ulong Inode, int MountId);

/// <summary>Both allowed file states, with no configuration bytes or secrets in the journal.</summary>
public sealed record ConfigurationFileSpecification(string Path, string OriginalSnapshotPath, string BeforeHash, string AfterHash);

/// <summary>Versioned operator-owned immutable inputs; no values are inferred from a production host.</summary>
public sealed record HostSpecification(
    int Version, string JobId, string BootId, string MountNamespace, string Unit,
    DirectoryIdentity Parent, DirectoryIdentity Source, string BackupPath, MountInstance SourceMount,
    string RclonePath, string RcloneSha256, string RcloneConfigPath, uint ServiceUid, uint ServiceGid,
    string PythonPath, string PythonSha256, string CustodyHelperPath, string CustodyHelperSha256,
    string CachePath, int CacheMaxSizeGiB, int CacheMaxAgeHours, int CacheMinFreeGiB, int VerificationBudgetMinutes,
    ConfigurationFileSpecification Profile, ConfigurationFileSpecification Policy)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
    };

    public static bool IsValidHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    /// <summary>Parse immutable bytes and verify the exact spec digest against the journal intent.</summary>
    public static HostSpecification Parse(byte[] bytes, CloudMaintenanceIntent intent, CloudMaintenanceAdmission admission)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(admission);
        if (bytes.Length is 0 or > 65536 || !IsValidHash(intent.Custody.ConfigurationDigest))
            throw new InvalidOperationException("invalid-host-specification");
        var actual = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(actual, intent.Custody.ConfigurationDigest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("host-specification-hash-mismatch");
        HostSpecification spec;
        try { spec = JsonSerializer.Deserialize<HostSpecification>(bytes, JsonOptions) ?? throw new JsonException(); }
        catch (JsonException) { throw new InvalidOperationException("invalid-host-specification"); }
        spec.Validate(intent, admission);
        return spec;
    }

    /// <summary>Validate the immutable specification against an admitted job before intent is persisted.</summary>
    public static HostSpecification ParseForStartup(byte[] bytes, CloudMigrationJob job)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(job);
        if (bytes.Length is 0 or > 65536 || job.Maintenance is not null)
            throw new InvalidOperationException("invalid-host-specification");
        var admission = job.MaintenanceAdmission
            ?? throw new InvalidOperationException("maintenance-admission-missing");
        HostSpecification spec;
        try { spec = JsonSerializer.Deserialize<HostSpecification>(bytes, JsonOptions) ?? throw new JsonException(); }
        catch (JsonException) { throw new InvalidOperationException("invalid-host-specification"); }

        var custody = new CloudMaintenanceCustody(admission.LibraryId.ToString(), admission.LibraryName,
            admission.LocalPath, spec.BackupPath, admission.ProfileId, admission.RemoteName,
            admission.RemotePath, admission.ManifestDigestSha256, admission.Verification,
            Convert.ToHexString(SHA256.HashData(bytes)));
        var intent = new CloudMaintenanceIntent(job.Id, CloudMaintenanceDirection.Promote,
            CloudMaintenancePhase.Prepared, "pending-host-identity", null, custody);
        spec.Validate(intent, admission);
        if (!LeasedOperations.MatchesAdmittedJob(
                new MaintenanceSnapshot(job with { Maintenance = intent }, "startup-admission"), intent))
            throw new InvalidOperationException("maintenance-admission-mismatch");
        return spec;
    }

    private void Validate(CloudMaintenanceIntent intent, CloudMaintenanceAdmission admission)
    {
        if (Version != 1 || JobId != intent.JobId || !Guid.TryParseExact(JobId, "N", out _)
            || !Guid.TryParse(BootId, out _) || !NamespaceValid(MountNamespace)
            || Unit != "jellyfin.service" || Parent is null || Source is null || SourceMount is null || Profile is null || Policy is null
            || !Canonical(Parent.Path) || !Canonical(Source.Path) || !Canonical(BackupPath)
            || Source.Path != intent.Custody.LocalPath || BackupPath != intent.Custody.BackupPath
            || Path.GetDirectoryName(Source.Path) != Parent.Path || Path.GetDirectoryName(BackupPath) != Parent.Path
            || Source.Path == BackupPath || Parent.Inode == 0 || Source.Inode == 0 || Parent.Device != Source.Device
            || Parent.MountId <= 0 || Source.MountId != Parent.MountId || SourceMount.MountId != Source.MountId
            || SourceMount.Version != 1 || SourceMount.BootId != BootId || SourceMount.Namespace != MountNamespace
            || string.IsNullOrWhiteSpace(SourceMount.FileSystem) || string.IsNullOrWhiteSpace(SourceMount.Source)
            || !Canonical(RclonePath) || !IsValidHash(RcloneSha256) || !Canonical(RcloneConfigPath)
            || ServiceUid == 0 || ServiceGid == 0 || !Canonical(PythonPath) || !IsValidHash(PythonSha256)
            || !Canonical(CustodyHelperPath) || !IsValidHash(CustodyHelperSha256) || !Canonical(CachePath)
            || CacheMaxSizeGiB <= 0 || CacheMaxAgeHours <= 0 || CacheMinFreeGiB < 0 || VerificationBudgetMinutes is < 1 or > 10080
            || !ValidConfig(Profile) || !ValidConfig(Policy) || Profile.Path == Policy.Path
            || Profile.OriginalSnapshotPath == Policy.OriginalSnapshotPath
            || Profile.OriginalSnapshotPath == Profile.Path || Policy.OriginalSnapshotPath == Policy.Path
            || admission.LibraryId == Guid.Empty || !Guid.TryParse(intent.Custody.LibraryId, out var libraryId) || libraryId != admission.LibraryId
            || intent.Custody.LibraryName != admission.LibraryName || Source.Path != admission.LocalPath
            || intent.Custody.ProfileId != admission.ProfileId || intent.Custody.RemoteName != admission.RemoteName || intent.Custody.RemotePath != admission.RemotePath
            || !string.Equals(intent.Custody.ManifestDigest, admission.ManifestDigestSha256, StringComparison.OrdinalIgnoreCase)
            || intent.Custody.Verification != admission.Verification || admission.Verification != "rclone-check-download-content"
            || Profile.Path != admission.PluginConfigurationPath || Policy.Path != admission.OptionsPath
            || !string.Equals(Profile.BeforeHash, admission.PluginConfigurationSha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Policy.BeforeHash, admission.OptionsSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("invalid-host-specification");
    }

    private static bool Canonical(string? path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path)
        && Path.GetFullPath(path) == path && path != "/" && !path.Contains('\0');
    private static bool NamespaceValid(string? value) => value is not null && value.StartsWith("mnt:[", StringComparison.Ordinal)
        && value.EndsWith(']') && ulong.TryParse(value.AsSpan(5, value.Length - 6), out var inode) && inode > 0;
    private static bool ValidConfig(ConfigurationFileSpecification value) => Canonical(value.Path) && Canonical(value.OriginalSnapshotPath) && IsValidHash(value.BeforeHash) && IsValidHash(value.AfterHash);
}
