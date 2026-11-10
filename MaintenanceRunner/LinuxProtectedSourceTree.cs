using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Slipmat.MaintenanceRunner;

/// <summary>Metadata-only, descriptor-pinned validation of a root-controlled source tree.</summary>
public static class LinuxProtectedSourceTree
{
    private const int PathOnly = 0x200000, CloseOnExec = 0x80000, EmptyPath = 0x1000;
    private const uint BasicStatsAndMountId = 0x17ff;

    public static void Verify(DirectoryIdentity source, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux() || !Path.IsPathFullyQualified(source.Path) || Path.GetFullPath(source.Path) != source.Path)
            throw new InvalidOperationException("source-tree-invalid");
        var arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        if (!arm && RuntimeInformation.ProcessArchitecture != Architecture.X64) throw new InvalidOperationException("unsupported-linux-architecture");
        var directory = arm ? 0x4000 : 0x10000;
        var noFollow = arm ? 0x8000 : 0x20000;
        var pending = new Stack<SafeFileHandle>();
        SafeFileHandle? current = null;
        try
        {
            current = Open(-100, "/", PathOnly | directory | noFollow | CloseOnExec);
            Check(current, directory: true, null);
            foreach (var part in source.Path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                ct.ThrowIfCancellationRequested();
                var next = Open(current.DangerousGetHandle().ToInt32(), part, PathOnly | directory | noFollow | CloseOnExec);
                try { Check(next, directory: true, null); } catch { next.Dispose(); throw; }
                current.Dispose(); current = next;
            }
            var root = Metadata(current);
            if (root.Inode != source.Inode || root.Device != source.Device || root.MountId != source.MountId)
                throw new InvalidOperationException("source-tree-identity-changed");
            pending.Push(current); current = null;
            while (pending.TryPop(out var dir))
            {
                using (dir)
                {
                    ct.ThrowIfCancellationRequested();
                    var fd = dir.DangerousGetHandle().ToInt32();
                    foreach (var entry in Directory.EnumerateFileSystemEntries($"/proc/self/fd/{fd}"))
                    {
                        ct.ThrowIfCancellationRequested();
                        var name = Path.GetFileName(entry);
                        using var child = Open(fd, name, PathOnly | noFollow | CloseOnExec);
                        var metadata = Metadata(child);
                        if (metadata.Uid != 0 || (metadata.Mode & 0x12) != 0 || metadata.MountId != source.MountId)
                            throw new InvalidOperationException("source-tree-unprotected");
                        var type = metadata.Mode & 0xf000;
                        if (type == 0x4000)
                        {
                            var next = Open(fd, name, PathOnly | directory | noFollow | CloseOnExec);
                            try
                            {
                                var pinned = Metadata(next);
                                if (pinned != metadata) throw new InvalidOperationException("source-tree-changed");
                                pending.Push(next);
                            }
                            catch { next.Dispose(); throw; }
                        }
                        else if (type != 0x8000) throw new InvalidOperationException("source-tree-unprotected");
                    }
                }
            }
        }
        finally { current?.Dispose(); while (pending.TryPop(out var handle)) handle.Dispose(); }
    }

    private static void Check(SafeFileHandle fd, bool directory, int? mountId)
    {
        var data = Metadata(fd);
        if (data.Uid != 0 || (data.Mode & 0x12) != 0 || (data.Mode & 0xf000) != (directory ? 0x4000 : 0x8000)
            || (mountId is not null && data.MountId != mountId)) throw new InvalidOperationException("source-tree-unprotected");
    }
    private sealed record Meta(uint Uid, ushort Mode, ulong Inode, ulong Device, int MountId);
    private static Meta Metadata(SafeFileHandle fd)
    {
        var bytes = new byte[256];
        if (statx(fd.DangerousGetHandle().ToInt32(), "", EmptyPath, BasicStatsAndMountId, bytes) != 0
            || (BinaryPrimitives.ReadUInt32LittleEndian(bytes) & 0x1000) == 0) throw new IOException("source-tree-stat-failed");
        var major = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(136));
        var minor = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(140));
        var device = ((ulong)(major & 0xfff) << 8) | (minor & 0xff) | ((ulong)(minor & 0xffffff00) << 12) | ((ulong)(major & 0xfffff000) << 32);
        var mount = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(144));
        if (mount == 0 || mount > int.MaxValue) throw new IOException("source-tree-stat-failed");
        return new(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(20)), BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(28)),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(32)), device, (int)mount);
    }
    private static SafeFileHandle Open(int fd, string name, int flags)
    {
        var result = openat(fd, name, flags);
        if (result < 0) throw new IOException("source-tree-open-failed", Marshal.GetHRForLastWin32Error());
        return new SafeFileHandle((IntPtr)result, true);
    }
    [DllImport("libc", SetLastError = true)] private static extern int openat(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int statx(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, [Out] byte[] buffer);
}
