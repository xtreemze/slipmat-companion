using Slipmat.MaintenanceRunner;

internal static class LinuxProtectedSourceTreeTests
{
    public static void Run()
    {
        if (!OperatingSystem.IsLinux() || Environment.UserName != "root") return;
        var root = Path.Combine("/root", "maintenance-protected-tree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var child = Path.Combine(root, "child"); Directory.CreateDirectory(child);
            var file = Path.Combine(child, "track"); File.WriteAllText(file, "test");
            var identity = LinuxTrustedFile.InspectDirectory(root);
            LinuxProtectedSourceTree.Verify(identity, default);
            File.SetUnixFileMode(file, (UnixFileMode)0x1ff);
            Reject(identity, "world-writable file");
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.SetUnixFileMode(child, (UnixFileMode)0x1ff);
            Reject(identity, "world-writable directory");
            File.SetUnixFileMode(child, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.CreateSymbolicLink(Path.Combine(child, "link"), file);
            Reject(identity, "symlink");
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            try { LinuxProtectedSourceTree.Verify(identity, canceled.Token); throw new Exception("canceled scan succeeded"); }
            catch (OperationCanceledException) { }
        }
        finally { Directory.Delete(root, true); }
    }
    private static void Reject(DirectoryIdentity identity, string description)
    {
        try { LinuxProtectedSourceTree.Verify(identity, default); throw new Exception(description + " accepted"); }
        catch (InvalidOperationException ex) when (ex.Message == "source-tree-unprotected") { }
    }
}
