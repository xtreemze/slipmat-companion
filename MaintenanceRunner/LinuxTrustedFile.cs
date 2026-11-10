using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Slipmat.MaintenanceRunner;

/// <summary>Reads a small root-controlled regular file through pinned, no-follow directory descriptors.</summary>
public static class LinuxTrustedFile
{
    private const int CloseOnExec = 0x80000;
    private const int PathOnly = 0x200000;
    private const int NonBlocking = 0x800;
    private const int EmptyPath = 0x1000;
    private const uint BasicStats = 0x7ff;

    /// <summary>Inspect a root-controlled directory by a pinned no-follow descriptor.</summary>
    public static DirectoryIdentity InspectDirectory(string path)
    {
        if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("linux-required");
        if (!Path.IsPathFullyQualified(path) || Path.GetFullPath(path) != path) throw new InvalidOperationException("trusted-path-invalid");
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part is "." or "..")) throw new InvalidOperationException("trusted-path-invalid");
        var arm64 = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        if (!arm64 && RuntimeInformation.ProcessArchitecture != Architecture.X64) throw new InvalidOperationException("unsupported-linux-architecture");
        var flags = (arm64 ? 0x4000 | 0x8000 : 0x10000 | 0x20000) | CloseOnExec;
        using var root = OpenAt(-100, "/", flags);
        RequireRootControlled(root, directory: true);
        SafeFileHandle? current = null;
        try
        {
            foreach (var part in parts)
            {
                var next = OpenAt((current ?? root).DangerousGetHandle().ToInt32(), part, flags);
                try { RequireRootControlled(next, directory: true); }
                catch { next.Dispose(); throw; }
                current?.Dispose(); current = next;
            }
            var buffer = new byte[256];
            if (statx((current ?? root).DangerousGetHandle().ToInt32(), "", EmptyPath, BasicStats | 0x1000, buffer) != 0
                || (BinaryPrimitives.ReadUInt32LittleEndian(buffer) & 0x1000) == 0)
                throw new IOException("trusted-mount-identity-unavailable");
            var inode = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(32));
            var major = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(136));
            var minor = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(140));
            var device = ((ulong)(major & 0xfff) << 8) | (minor & 0xff) | ((ulong)(minor & 0xffffff00) << 12) | ((ulong)(major & 0xfffff000) << 32);
            var mountId = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(144));
            if (mountId == 0 || mountId > int.MaxValue) throw new IOException("trusted-mount-identity-unavailable");
            return new(path, device, inode, (int)mountId);
        }
        finally { current?.Dispose(); }
    }

    /// <summary>Inspect a root-controlled directory, returning null only when the exact path is absent.</summary>
    public static DirectoryIdentity? TryInspectDirectory(string path)
    {
        if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("linux-required");
        if (!Path.IsPathFullyQualified(path) || Path.GetFullPath(path) != path || path.Contains('\0'))
            throw new InvalidOperationException("trusted-path-invalid");
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part is "." or "..")) throw new InvalidOperationException("trusted-path-invalid");
        var arm64 = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        if (!arm64 && RuntimeInformation.ProcessArchitecture != Architecture.X64) throw new InvalidOperationException("unsupported-linux-architecture");
        var flags = (arm64 ? 0x4000 | 0x8000 : 0x10000 | 0x20000) | CloseOnExec;
        using var root = OpenAt(-100, "/", flags);
        RequireRootControlled(root, directory: true);
        SafeFileHandle? current = null;
        try
        {
            foreach (var part in parts)
            {
                var parent = current ?? root;
                var descriptor = openat(parent.DangerousGetHandle().ToInt32(), part, flags);
                if (descriptor < 0)
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error == 2) return null; // ENOENT at a no-follow path component.
                    throw new IOException("trusted-path-open-failed", Marshal.GetHRForLastWin32Error());
                }
                var next = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
                try { RequireRootControlled(next, directory: true); }
                catch { next.Dispose(); throw; }
                current?.Dispose(); current = next;
            }

            var buffer = new byte[256];
            if (statx((current ?? root).DangerousGetHandle().ToInt32(), "", EmptyPath, BasicStats | 0x1000, buffer) != 0
                || (BinaryPrimitives.ReadUInt32LittleEndian(buffer) & 0x1000) == 0)
                throw new IOException("trusted-mount-identity-unavailable");
            var inode = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(32));
            var major = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(136));
            var minor = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(140));
            var device = ((ulong)(major & 0xfff) << 8) | (minor & 0xff) | ((ulong)(minor & 0xffffff00) << 12) | ((ulong)(major & 0xfffff000) << 32);
            var mountId = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(144));
            if (mountId == 0 || mountId > int.MaxValue) throw new IOException("trusted-mount-identity-unavailable");
            return new(path, device, inode, (int)mountId);
        }
        finally { current?.Dispose(); }
    }

    /// <summary>Create a private immutable snapshot once and flush file and containing directory.</summary>
    public static void CreateNew(string path, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("linux-required");
        if (!Path.IsPathFullyQualified(path) || Path.GetFullPath(path) != path || bytes.Length > 4 * 1024 * 1024)
            throw new InvalidOperationException("trusted-path-invalid");
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(part => part is "." or "..")) throw new InvalidOperationException("trusted-path-invalid");
        var arm64 = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        if (!arm64 && RuntimeInformation.ProcessArchitecture != Architecture.X64) throw new InvalidOperationException("unsupported-linux-architecture");
        var directoryFlag = arm64 ? 0x4000 : 0x10000;
        var noFollow = arm64 ? 0x8000 : 0x20000;
        using var root = OpenAt(-100, "/", directoryFlag | noFollow | CloseOnExec);
        RequireRootControlled(root, directory: true);
        SafeFileHandle? current = null;
        try
        {
            for (var i = 0; i < parts.Length - 1; i++)
            {
                var next = OpenAt((current ?? root).DangerousGetHandle().ToInt32(), parts[i], directoryFlag | noFollow | CloseOnExec);
                try { RequireRootControlled(next, directory: true); }
                catch { next.Dispose(); throw; }
                current?.Dispose();
                current = next;
            }
            var parent = current ?? root;
            var descriptor = openat_create(parent.DangerousGetHandle().ToInt32(), parts[^1], 1 | 0x40 | 0x80 | noFollow | CloseOnExec, 0x180);
            if (descriptor < 0) throw new IOException("trusted-snapshot-create-failed");
            using (var stream = new FileStream(new SafeFileHandle((IntPtr)descriptor, ownsHandle: true), FileAccess.Write))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (fsync(parent.DangerousGetHandle().ToInt32()) != 0) throw new IOException("trusted-snapshot-directory-flush-failed");
        }
        finally { current?.Dispose(); }
    }

    public static byte[] Read(string path, int maximumBytes = 65536)
    {
        if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("linux-required");
        if (maximumBytes <= 0 || !Path.IsPathFullyQualified(path) || Path.GetFullPath(path) != path)
            throw new InvalidOperationException("trusted-path-invalid");
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(part => part is "." or "..")) throw new InvalidOperationException("trusted-path-invalid");
        // Linux arm64 and x86_64 assign different values to O_DIRECTORY/O_NOFOLLOW.
        var arm64 = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        if (!arm64 && RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new InvalidOperationException("unsupported-linux-architecture");
        var directoryFlag = arm64 ? 0x4000 : 0x10000;
        var noFollow = arm64 ? 0x8000 : 0x20000;
        using var root = OpenAt(-100, "/", directoryFlag | noFollow | CloseOnExec);
        RequireRootControlled(root, directory: true);
        SafeFileHandle? current = null;
        try
        {
            for (var i = 0; i < parts.Length; i++)
            {
                var isDirectory = i != parts.Length - 1;
                var next = OpenAt((current ?? root).DangerousGetHandle().ToInt32(), parts[i], noFollow | CloseOnExec | (isDirectory ? directoryFlag : PathOnly));
                try { RequireRootControlled(next, isDirectory); }
                catch { next.Dispose(); throw; }
                current?.Dispose();
                current = next;
            }
            // Reopen the already type-checked pinned inode through procfs, never the original pathname.
            using var readable = OpenAt(-100, $"/proc/self/fd/{current!.DangerousGetHandle().ToInt32()}", CloseOnExec | NonBlocking);
            RequireRootControlled(readable, directory: false);
            using var stream = new FileStream(readable, FileAccess.Read);
            using var result = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = stream.Read(buffer)) != 0)
            {
                if (result.Length + count > maximumBytes) throw new InvalidOperationException("trusted-file-too-large");
                result.Write(buffer, 0, count);
            }
            return result.ToArray();
        }
        finally { current?.Dispose(); }
    }

    private static SafeFileHandle OpenAt(int parent, string path, int flags)
    {
        var fd = openat(parent, path, flags);
        if (fd < 0) throw new IOException("trusted-path-open-failed");
        return new SafeFileHandle((IntPtr)fd, ownsHandle: true);
    }

    private static void RequireRootControlled(SafeFileHandle handle, bool directory)
    {
        // Linux statx ABI: uid at 20 and mode at 28. Use AT_EMPTY_PATH on the pinned descriptor.
        var buffer = new byte[256];
        if (statx(handle.DangerousGetHandle().ToInt32(), "", EmptyPath, BasicStats, buffer) != 0)
            throw new IOException("trusted-path-stat-failed");
        var uid = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(20));
        var mode = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(28));
        var expectedType = directory ? 0x4000 : 0x8000;
        if (uid != 0 || (mode & 0xF000) != expectedType || (mode & 0x12) != 0)
            throw new InvalidOperationException("trusted-path-not-root-controlled");
    }

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int openat(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int openat_create(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mode);
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int fsync(int descriptor);
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int statx(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, [Out] byte[] buffer);
}
