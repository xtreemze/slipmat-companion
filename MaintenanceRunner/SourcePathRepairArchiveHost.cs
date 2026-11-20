using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;

namespace Slipmat.MaintenanceRunner;

/// <summary>Runs only preservation effects for a journal-bound repair under the stopped-service barrier.</summary>
internal sealed class SourcePathRepairArchiveHost(SourcePathRepairJournal journal, LinuxMaintenanceBarrier barrier, string specificationPath)
{
    internal Task<string> ReserveArchiveAsync(CloudSourcePathRepairSnapshot expected, CancellationToken cancellationToken)
        => RunAsync(expected, ArchiveOperation.Reserve, cancellationToken);

    internal Task<string> CopyArchiveAsync(CloudSourcePathRepairSnapshot expected, CancellationToken cancellationToken)
        => RunAsync(expected, ArchiveOperation.Copy, cancellationToken);

    internal Task<string> VerifyArchiveAsync(CloudSourcePathRepairSnapshot expected, CancellationToken cancellationToken)
        => RunAsync(expected, ArchiveOperation.Verify, cancellationToken);

    internal string? InspectArchive(CloudSourcePathRepairPlan plan)
    {
        var specification = SourcePathRepairHostSpecification.Read(specificationPath, plan);
        var identity = LinuxTrustedFile.TryInspectDirectory(Path.Combine(specification.ArchiveParent, specification.ArchiveLeaf));
        return identity is null ? null : FormattableString.Invariant($"{identity.Device}:{identity.Inode}:{identity.MountId}");
    }

    private enum ArchiveOperation { Reserve, Copy, Verify }

    private async Task<string> RunAsync(CloudSourcePathRepairSnapshot expected, ArchiveOperation operation, CancellationToken cancellationToken)
    {
        var expectedPlan = expected.State.Plan;
        var current = await journal.ReadAsync(expectedPlan.Binding.JobId, cancellationToken).ConfigureAwait(false);
        var reserve = operation == ArchiveOperation.Reserve;
        var verify = operation == ArchiveOperation.Verify;
        if (current is null || current.Revision != expected.Revision || current.State.Plan.Fingerprint != expectedPlan.Fingerprint
            || (!verify && (current.State.Phase != CloudSourcePathRepairPhase.PreservationIntent || current.State.RollbackRequested))
            || reserve != (current.State.ReservedArchiveIdentity is null))
            throw new InvalidOperationException("source-repair-archive-intent-required");

        var plan = current.State.Plan;
        var specification = SourcePathRepairHostSpecification.Read(specificationPath, plan);
        // A second mount of the same device can disguise a source subtree as an archive path.
        if (specification.ArchiveParentIdentity.Device == plan.Binding.RootIdentity.Device
            && specification.ArchiveParentIdentity.MountId != plan.Binding.RootIdentity.MountId)
            throw new InvalidOperationException("source-repair-archive-mount-alias");
        var manifest = LinuxTrustedFile.Read(specification.ManifestPath, 4 * 1024 * 1024);
        await barrier.VerifyMaintenanceAsync("jellyfin.service", specification.BootId, specification.MountNamespace,
            cancellationToken).ConfigureAwait(false);
        CloudSourcePathRepairIdentity? archive = reserve ? null : ParseIdentity(current.State.ReservedArchiveIdentity!);
        var identity = await CustodyProcess.ArchiveAsync(specification.PythonPath, specification.PythonSha256,
            specification.HelperPath, specification.HelperSha256, specification.ArchiveParent, specification.ArchiveLeaf,
            specification.ArchiveParentIdentity, archive, manifest, plan.PreservationManifestSha256, verify ? null : current.State.Plan,
            cancellationToken).ConfigureAwait(false);
        await barrier.VerifyMaintenanceAsync("jellyfin.service", specification.BootId, specification.MountNamespace,
            cancellationToken).ConfigureAwait(false);
        return FormattableString.Invariant($"{identity.Device}:{identity.Inode}:{identity.MountId}");
    }

    private static CloudSourcePathRepairIdentity ParseIdentity(string value)
    {
        var parts = value.Split(':');
        if (parts.Length == 3
            && ulong.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var device) && device != 0
            && ulong.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var inode) && inode != 0
            && long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var mount) && mount > 0
            && value == FormattableString.Invariant($"{device}:{inode}:{mount}"))
            return new(device, inode, mount);
        throw new InvalidOperationException("source-repair-archive-reservation-invalid");
    }
}

internal sealed record SourcePathRepairHostSpecification(string BootId, string MountNamespace,
    string PythonPath, string PythonSha256, string HelperPath, string HelperSha256, string ManifestPath,
    string ArchiveParent, string ArchiveLeaf, CloudSourcePathRepairIdentity ArchiveParentIdentity,
    Dictionary<string, CloudSourcePathRepairIdentity> MoveParents,
    string RenameProbePath, CloudSourcePathRepairIdentity RenameProbeIdentity)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    internal static SourcePathRepairHostSpecification Read(string specificationPath, CloudSourcePathRepairPlan plan)
    {
        var bytes = LinuxTrustedFile.Read(specificationPath, 64 * 1024);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(plan.Binding.HostSpecificationSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("source-repair-host-specification-changed");
        var specification = JsonSerializer.Deserialize<SourcePathRepairHostSpecification>(bytes, Json)
            ?? throw new InvalidOperationException("source-repair-host-specification-invalid");
        return specification;
    }
}
