using System.Text.Json;
using System.Text.Json.Serialization;

namespace Slipmat.MaintenanceRunner;

/// <summary>Strict, boot-scoped durable identity for a previously observed mount instance.</summary>
public static class MountIdentityCodec
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
    };

    public static string Encode(MountInstance identity)
    {
        Validate(identity);
        return JsonSerializer.Serialize(identity, Options);
    }

    public static MountInstance Decode(string value, string bootId, string mountNamespace, string remote, string mountpoint)
    {
        if (value.Length is 0 or > 4096) throw new InvalidOperationException("mount-identity-invalid");
        MountInstance identity;
        try { identity = JsonSerializer.Deserialize<MountInstance>(value, Options) ?? throw new JsonException(); }
        catch (JsonException) { throw new InvalidOperationException("mount-identity-invalid"); }
        Validate(identity);
        if (Encode(identity) != value || identity.BootId != bootId || identity.Namespace != mountNamespace
            || identity.Source != remote || identity.FileSystem != "fuse.rclone"
            || identity.MountPointIdentity!.Path != mountpoint)
            throw new InvalidOperationException("mount-identity-mismatch");
        return identity;
    }

    private static void Validate(MountInstance identity)
    {
        if (identity.Version != 1 || !Guid.TryParse(identity.BootId, out _) || identity.MountId <= 0
            || string.IsNullOrWhiteSpace(identity.Namespace) || string.IsNullOrWhiteSpace(identity.FileSystem)
            || string.IsNullOrWhiteSpace(identity.Source) || string.IsNullOrWhiteSpace(identity.Unit)
            || identity.ProcessId <= 0 || identity.ProcessStartTicks == 0 || string.IsNullOrWhiteSpace(identity.Cgroup)
            || identity.MountPointIdentity is not { Inode: > 0, MountId: > 0 } mountpoint
            || !Path.IsPathFullyQualified(mountpoint.Path) || Path.GetFullPath(mountpoint.Path) != mountpoint.Path
            || mountpoint.Path == "/" || mountpoint.Device == 0)
            throw new InvalidOperationException("mount-identity-invalid");
    }
}
