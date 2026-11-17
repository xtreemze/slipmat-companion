using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.AudioGateway.Models;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>Deterministic, fail-closed plan over a complete server-owned inventory.</summary>
public static class CloudConflictRecoveryPlanner
{
    /// <summary>Maximum observed objects.</summary>
    public const int MaxCandidates = 100_000;
    /// <summary>Maximum durable conflict receipts.</summary>
    public const int MaxConflicts = 10_000;
    private static readonly HashSet<string> Metadata = new(StringComparer.OrdinalIgnoreCase) { ".nfo", ".jpg", ".jpeg", ".png" };
    private static readonly HashSet<string> Artwork = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png" };

    /// <summary>Create a fully validated plan from trusted inventory.</summary>
    public static CloudConflictRecoveryPlan Create(CloudConflictRecoveryBinding binding, CloudConflictInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(inventory);
        if (binding != inventory.Binding || !Guid.TryParseExact(binding.JobId, "N", out var jobId) || jobId == Guid.Empty
            || !Guid.TryParseExact(binding.AttemptId, "N", out var attemptId) || attemptId == Guid.Empty
            || string.IsNullOrWhiteSpace(binding.ProfileId) || string.IsNullOrWhiteSpace(binding.LibraryName)
            || string.IsNullOrWhiteSpace(binding.RemoteName) || !Path.IsPathFullyQualified(binding.LocalPath)
            || !ValidPath(binding.ActiveRoot) || !ValidPath(binding.ArchiveRoot) || Overlap(binding.ActiveRoot, binding.ArchiveRoot)
            || !Digest(binding.SourceManifestSha256) || inventory.Manifest is null || !Path.IsPathFullyQualified(inventory.Manifest.Path)
            || !Digest(inventory.Manifest.Sha256) || inventory.Entries is null || inventory.Entries.Count > MaxCandidates
            || inventory.Manifest.Count != inventory.Entries.Count) throw Invalid();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var conflicts = new List<CloudConflictPlanItem>();
        var deferred = 0;
        foreach (var item in inventory.Entries)
        {
            if (item is null || !ValidPath(item.RelativePath) || !seen.Add(item.RelativePath)
                || item.Local is null && item.Remote is null || !ValidObject(item.Local) || !ValidObject(item.Remote)) throw Invalid();
            if (item.Remote is null) { deferred++; continue; }
            if (item.Local is not null && EqualDigest(item.Local.Sha256, item.Remote.Sha256))
            {
                if (item.Local.Size != item.Remote.Size) throw Invalid();
                continue;
            }
            var extension = Path.GetExtension(item.RelativePath);
            if (item.Local is null ? !Artwork.Contains(extension) : !Metadata.Contains(extension)) throw Invalid();
            var requiredKind = extension.ToLowerInvariant() switch
            {
                ".nfo" => CloudConflictContentKind.XmlMetadata, ".jpg" or ".jpeg" => CloudConflictContentKind.Jpeg,
                ".png" => CloudConflictContentKind.Png, _ => CloudConflictContentKind.Unknown,
            };
            if (requiredKind == CloudConflictContentKind.Unknown || item.Remote.Kind != requiredKind
                || item.Local is not null && item.Local.Kind != requiredKind) throw Invalid();
            conflicts.Add(new(item.RelativePath, binding.ArchiveRoot + "/" + item.RelativePath,
                item.Local is null ? CloudConflictOperation.RemoveRemoteOnly : CloudConflictOperation.ReplaceWithLocal, item.Local, item.Remote));
            if (conflicts.Count > MaxConflicts) throw Invalid();
        }
        foreach (var path in seen)
            for (var slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
                if (seen.Contains(path[..slash])) throw Invalid();
        conflicts.Sort((a, b) => StringComparer.Ordinal.Compare(a.RelativePath, b.RelativePath));
        var withoutFingerprint = new CloudConflictRecoveryPlan(binding, inventory.Manifest, conflicts.ToArray(), deferred, "");
        return withoutFingerprint with { Fingerprint = Fingerprint(withoutFingerprint) };
    }

    /// <summary>Compute the deterministic plan fingerprint.</summary>
    public static string Fingerprint(CloudConflictRecoveryPlan plan)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(plan with { Fingerprint = "" })));
    /// <summary>Hash the complete server-side binding used by the recovery fence.</summary>
    public static string BindingFingerprint(CloudConflictRecoveryBinding binding)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(binding)));
    /// <summary>Validate a SHA-256 hexadecimal digest.</summary>
    public static bool Digest(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    /// <summary>Compare digest text without case sensitivity.</summary>
    public static bool EqualDigest(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static bool ValidObject(CloudConflictObject? value) => value is null || value.Size >= 0 && Digest(value.Sha256) && !string.IsNullOrWhiteSpace(value.Identity);
    private static bool Overlap(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase)
        || a.StartsWith(b + "/", StringComparison.OrdinalIgnoreCase) || b.StartsWith(a + "/", StringComparison.OrdinalIgnoreCase);
    private static bool ValidPath(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 4096 || value != value.Normalize(NormalizationForm.FormC)
            || value.Contains('\\') || value.Contains(':') || value.Any(char.IsControl)) return false;
        return value.Split('/').All(part => part.Length > 0 && part is not ("." or "..") && !part.EndsWith(' ') && !part.EndsWith('.'));
    }
    private static InvalidOperationException Invalid() => new("conflict-plan-invalid");
}
