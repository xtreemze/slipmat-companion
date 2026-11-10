using Slipmat.MaintenanceRunner;

internal static class LinuxCacheCustodyTests
{
    public static void Run()
    {
        if (!OperatingSystem.IsLinux() || Environment.UserName != "root") return;
        var root = Path.Combine("/root", "maintenance-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = LinuxTrustedFile.InspectDirectory(root);
            var different = LinuxTrustedFile.InspectDirectory("/sys");
            if (source.Device == different.Device) throw new Exception("test requires a different filesystem");
            LinuxCacheCustody.Verify(different.Path, source.Device);
            Reject(root, source.Device, "same-device cache");
            var bindAlias = Environment.GetEnvironmentVariable("SLIPMAT_TEST_CACHE_BIND_ALIAS");
            if (!string.IsNullOrEmpty(bindAlias)) Reject(bindAlias, source.Device, "bind-mounted source-device cache");
            var alias = Path.Combine(root, "alias");
            Directory.CreateSymbolicLink(alias, "/sys");
            Reject(alias, source.Device, "symlink cache");
            Reject(Path.Combine(root, "missing"), source.Device, "missing cache");
            var writable = Path.Combine(root, "writable");
            Directory.CreateDirectory(writable);
            File.SetUnixFileMode(writable, (UnixFileMode)0x1ff);
            Reject(writable, different.Device, "writable cache");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void Reject(string path, ulong sourceDevice, string description)
    {
        var rejected = false;
        try { LinuxCacheCustody.Verify(path, sourceDevice); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { rejected = true; }
        if (!rejected) throw new Exception(description + " accepted");
    }
}
