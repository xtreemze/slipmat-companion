using System.Text.Json;

namespace Slipmat.MaintenanceRunner;

/// <summary>Compatible adjacent projection ownership marker; never overwrite an existing file.</summary>
public static class ProjectionMarker
{
    private sealed record Marker(int Version, string RemoteName, string RemoteType, string RemotePath);

    public static void Ensure(HostSpecification spec, string remoteName, string remoteType, string remotePath)
    {
        var path = spec.Source.Path + ".slipmat-rclone-cloud-mount.json";
        var expected = new Marker(2, remoteName, remoteType, remotePath);
        if (File.Exists(path))
        {
            Marker? actual;
            try { actual = JsonSerializer.Deserialize<Marker>(LinuxTrustedFile.Read(path, 16384)); }
            catch (JsonException) { throw new InvalidOperationException("projection-marker-invalid"); }
            if (actual is null || actual.Version != 2 || !string.Equals(actual.RemoteName, remoteName, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(actual.RemoteType, remoteType, StringComparison.OrdinalIgnoreCase) || actual.RemotePath != remotePath)
                throw new InvalidOperationException("projection-marker-mismatch");
            return;
        }
        LinuxTrustedFile.CreateNew(path, JsonSerializer.SerializeToUtf8Bytes(expected));
    }
}
