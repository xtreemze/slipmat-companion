using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Jellyfin.Plugin.AudioGateway.Models;
using Slipmat.MaintenanceRunner;

internal static class ArchiveAdapterTests
{
    internal static async Task RunAsync()
    {
        if (!OperatingSystem.IsLinux() || Environment.UserName != "root") return;
        var root = Directory.CreateDirectory(Path.Combine("/root", "slipmat-archive-test-" + Guid.NewGuid().ToString("N")),
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var python = new FileInfo("/usr/bin/python3").ResolveLinkTarget(true)?.FullName ?? "/usr/bin/python3";
            var helper = Path.Combine(root.FullName, "custody.py");
            File.Copy(Path.GetFullPath("MaintenanceRunner/custody.py"), helper);
            var source = Directory.CreateDirectory(Path.Combine(root.FullName, "source"));
            byte[] contents = [1, 2, 3];
            await File.WriteAllBytesAsync(Path.Combine(source.FullName, "Track.flac"), contents);
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                fileCount = 1,
                totalBytes = contents.Length,
                entries = new[] { new { path = "Track.flac", kind = "file", size = contents.Length,
                    sha256 = Hash(contents) } },
            });
            var method = typeof(CustodyProcess).GetMethod("ArchiveAsync", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new Exception("archive adapter unavailable");
            var parentId = Identify(root.FullName);
            var sourceId = Identify(source.FullName);
            var pythonHash = Hash(await File.ReadAllBytesAsync(python));
            var helperHash = Hash(await File.ReadAllBytesAsync(helper));
            Task<CloudSourcePathRepairIdentity> Invoke(CloudSourcePathRepairIdentity? archive, bool copy)
                => (Task<CloudSourcePathRepairIdentity>)method.Invoke(null,
                    [python, pythonHash, helper, helperHash, root.FullName, "archive", parentId, archive,
                     manifest, Hash(manifest), copy ? source.FullName : null, copy ? sourceId : null,
                     CancellationToken.None])!;
            var reserved = await Invoke(null, true);
            if (Directory.EnumerateFileSystemEntries(Path.Combine(root.FullName, "archive")).Any())
                throw new Exception("archive reservation copied files before its identity could be recorded");
            if (await Invoke(reserved, true) != reserved || await Invoke(reserved, false) != reserved)
                throw new Exception("archive adapter did not preserve the reserved identity");
            var archivePath = Path.Combine(root.FullName, "archive", "Track.flac");
            if (!(await File.ReadAllBytesAsync(archivePath)).SequenceEqual(contents))
                throw new Exception("archive payload did not match source");
        }
        finally { root.Delete(true); }
    }

    private static string Hash(byte[] contents) => Convert.ToHexStringLower(SHA256.HashData(contents));

    private static CloudSourcePathRepairIdentity Identify(string path)
    {
        var identity = LinuxTrustedFile.InspectDirectory(path);
        return new(identity.Device, identity.Inode, identity.MountId);
    }
}
