using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;

namespace Slipmat.MaintenanceRunner;

/// <summary>Bounded host operations for preparing an offline intent under a held journal lease.</summary>
public interface IPreparationHost
{
    Task BarrierAsync(HostSpecification spec, CancellationToken ct);
    Task CheckLocalAsync(HostSpecification spec, CloudMigrationJob job, CancellationToken ct);
    (HostSpecification Specification, string Digest) Prepare(HostSpecification draft, CloudMigrationJob job, string output);
    Task VerifyAsync(CloudMigrationJob job, HostSpecification spec, CancellationToken ct);
    Task ResolveAsync(HostSpecification spec, string remote, CancellationToken ct);
    Task CapabilityAsync(HostSpecification spec, CancellationToken ct);
}

/// <summary>Prepare and admit only; never execute a transaction or mutate media/configuration.</summary>
public static class PreparationRunner
{
    private static readonly JsonSerializerOptions DraftOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
    };

    public static HostSpecification ReadDraft(string path)
    {
        try { return JsonSerializer.Deserialize<HostSpecification>(LinuxTrustedFile.Read(path), DraftOptions) ?? throw new JsonException(); }
        catch (JsonException) { throw new InvalidOperationException("invalid-host-specification"); }
    }

    public static async Task<CloudMigrationJournalSnapshot> PrepareAsync(
        CloudMigrationJournalSnapshot original, HostSpecification draft, string output,
        IPreparationHost host, Func<CloudMigrationJournalSnapshot, CloudMigrationJob, CancellationToken, Task<CloudMigrationJournalSnapshot>> compareExchange,
        CancellationToken ct)
    {
        var job = original.Job;
        if (string.IsNullOrWhiteSpace(original.Revision) || job.Id != draft.JobId || job.Maintenance is not null
            || job.MaintenanceAdmission is null || job.Phase != CloudMigrationPhases.ReadyForCutover)
            throw new InvalidOperationException("maintenance-admission-mismatch");
        await host.BarrierAsync(draft, ct).ConfigureAwait(false);
        await host.CheckLocalAsync(draft, job, ct).ConfigureAwait(false);
        await host.BarrierAsync(draft, ct).ConfigureAwait(false);
        var (spec, digest) = host.Prepare(draft, job, output);
        await host.BarrierAsync(spec, ct).ConfigureAwait(false);
        await host.CapabilityAsync(spec, ct).ConfigureAwait(false);
        await host.BarrierAsync(spec, ct).ConfigureAwait(false);
        await host.VerifyAsync(job, spec, ct).ConfigureAwait(false);
        await host.BarrierAsync(spec, ct).ConfigureAwait(false);
        await host.ResolveAsync(spec, job.MaintenanceAdmission.RemoteName, ct).ConfigureAwait(false);
        await host.BarrierAsync(spec, ct).ConfigureAwait(false);
        await host.CheckLocalAsync(spec, job, ct).ConfigureAwait(false);
        await host.BarrierAsync(spec, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var admission = job.MaintenanceAdmission;
        var custody = new CloudMaintenanceCustody(admission.LibraryId.ToString(), admission.LibraryName,
            admission.LocalPath, spec.BackupPath, admission.ProfileId, admission.RemoteName, admission.RemotePath,
            admission.ManifestDigestSha256, admission.Verification, digest);
        var intent = new CloudMaintenanceIntent(job.Id, CloudMaintenanceDirection.Promote, CloudMaintenancePhase.Prepared,
            $"{spec.Source.Device}:{spec.Source.Inode}:{spec.Source.MountId}", null, custody);
        var nextJob = job with { Maintenance = intent };
        if (!LeasedOperations.MatchesAdmittedJob(new MaintenanceSnapshot(nextJob, original.Revision), intent))
            throw new InvalidOperationException("maintenance-admission-mismatch");
        return await compareExchange(original, nextJob, ct).ConfigureAwait(false);
    }
}

/// <summary>Linux production preparation operations. The caller keeps the exclusive journal lease.</summary>
public sealed class LinuxPreparationHost(LinuxMaintenanceBarrier barrier, Func<HostSpecification, CancellationToken, Task> capability) : IPreparationHost
{
    public Task BarrierAsync(HostSpecification spec, CancellationToken ct) => barrier.VerifyMaintenanceAsync(spec, ct);
    public async Task CheckLocalAsync(HostSpecification spec, CloudMigrationJob job, CancellationToken ct)
    {
        await BarrierAsync(spec, ct).ConfigureAwait(false);
        if (LinuxTrustedFile.InspectDirectory(spec.Parent.Path) != spec.Parent
            || LinuxTrustedFile.InspectDirectory(spec.Source.Path) != spec.Source
            || !SourceMountMatches(spec)
            || LinuxTrustedFile.TryInspectDirectory(spec.BackupPath) is not null
            || await new LinuxSystemdMountBackend().InspectAsync(spec, ct).ConfigureAwait(false) is not null
            || !Matches(spec.Profile.Path, job.MaintenanceAdmission!.PluginConfigurationSha256)
            || !Matches(spec.Policy.Path, job.MaintenanceAdmission.OptionsSha256))
            throw new InvalidOperationException("maintenance-local-state-changed");
        LinuxProtectedSourceTree.Verify(spec.Source, ct);
        await BarrierAsync(spec, ct).ConfigureAwait(false);
    }
    public (HostSpecification Specification, string Digest) Prepare(HostSpecification draft, CloudMigrationJob job, string output)
        => HostPreparation.Prepare(draft, job, output);
    public Task VerifyAsync(CloudMigrationJob job, HostSpecification spec, CancellationToken ct)
        => WithBarrierMonitorAsync(spec, token => HostVerification.VerifyAsync(job, spec, token), ct);
    public async Task ResolveAsync(HostSpecification spec, string remote, CancellationToken ct)
        => _ = await RemoteTypeResolver.ResolveAsync(spec, remote, ct).ConfigureAwait(false);
    public Task CapabilityAsync(HostSpecification spec, CancellationToken ct) => capability(spec, ct);
    private static bool SourceMountMatches(HostSpecification spec)
    {
        foreach (var line in File.ReadLines("/proc/self/mountinfo"))
        {
            var parts = line.Split(" - ", 2, StringSplitOptions.None);
            if (parts.Length != 2) continue;
            var left = parts[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var right = parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (left.Length < 3 || right.Length < 2 || !int.TryParse(left[0], out var id) || id != spec.SourceMount.MountId) continue;
            var device = left[2].Split(':');
            return device.Length == 2 && uint.TryParse(device[0], out var major) && uint.TryParse(device[1], out var minor)
                && major == spec.SourceMount.Major && minor == spec.SourceMount.Minor
                && right[0] == spec.SourceMount.FileSystem && DecodeMount(right[1]) == spec.SourceMount.Source;
        }
        return false;
    }
    private static string DecodeMount(string value) => value.Replace("\\040", " ", StringComparison.Ordinal)
        .Replace("\\011", "\t", StringComparison.Ordinal).Replace("\\012", "\n", StringComparison.Ordinal)
        .Replace("\\134", "\\", StringComparison.Ordinal);
    private static bool Matches(string path, string expected) => string.Equals(
        Convert.ToHexString(SHA256.HashData(LinuxTrustedFile.Read(path, 4 * 1024 * 1024))), expected, StringComparison.OrdinalIgnoreCase);

    private async Task WithBarrierMonitorAsync(HostSpecification spec, Func<CancellationToken, Task> action, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var work = action(linked.Token);
        try
        {
            while (!work.IsCompleted)
            {
                var tick = Task.Delay(TimeSpan.FromSeconds(1), linked.Token);
                if (await Task.WhenAny(work, tick).ConfigureAwait(false) == work) break;
                await tick.ConfigureAwait(false);
                await BarrierAsync(spec, linked.Token).ConfigureAwait(false);
            }
            await work.ConfigureAwait(false);
            await BarrierAsync(spec, ct).ConfigureAwait(false);
        }
        catch
        {
            linked.Cancel();
            try { await work.ConfigureAwait(false); } catch (Exception) { }
            throw;
        }
    }
}
