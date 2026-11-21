namespace Slipmat.MaintenanceRunner;

/// <summary>Conservatively keep the root-run cache on a distinct, trusted filesystem.</summary>
public static class LinuxCacheCustody
{
    /// <summary>Require an existing root-controlled, no-symlink cache on a different device from retained media.</summary>
    public static void Verify(string cachePath, ulong sourceDevice)
    {
        var cache = LinuxTrustedFile.InspectDirectory(cachePath);
        if (sourceDevice == 0 || cache.Device == sourceDevice)
            throw new InvalidOperationException("cache-filesystem-overlaps-source");
    }
}
