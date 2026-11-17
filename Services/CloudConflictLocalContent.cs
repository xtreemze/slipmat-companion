using System.Globalization;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Microsoft.Win32.SafeHandles;
using Jellyfin.Plugin.AudioGateway.Models;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>Descriptor-bound metadata for one regular local source file.</summary>
/// <param name="RelativePath">Canonical relative path.</param>
/// <param name="Size">Size in bytes.</param>
/// <param name="LastWriteTimeUtcTicks">UTC modification time in 100-nanosecond ticks.</param>
/// <param name="Identity">Device, inode, size and change-time identity.</param>
public sealed record CloudConflictLocalFileMetadata(string RelativePath, long Size, long LastWriteTimeUtcTicks, string Identity);

/// <summary>Linux-only, no-follow local source and private-cache reader.</summary>
public static class CloudConflictLocalContent
{
    private const int O_RDONLY = 0;
    private const int O_WRONLY = 1;
    private const int O_CREAT = 0x40;
    private const int O_EXCL = 0x80;
    private const int O_NONBLOCK = 0x800;
    // arm64 overrides these two asm-generic values in arch/arm64/include/uapi/asm/fcntl.h.
    private static readonly int O_DIRECTORY = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 0x4000 : 0x10000;
    private static readonly int O_NOFOLLOW = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 0x8000 : 0x20000;
    private const int O_CLOEXEC = 0x80000;
    private const int AT_EMPTY_PATH = 0x1000;
    private const int STATX_BASIC_STATS = 0x7ff;
    private const int S_IFMT = 0xf000;
    private const int S_IFREG = 0x8000;
    private const int S_IFDIR = 0x4000;
    private const int MaxXmlBytes = 16 * 1024 * 1024;
    private const int MaxEntries = 100_000;
    private const int MaxDepth = 128;
    private static readonly uint[] PngCrcTable = BuildPngCrcTable();

    /// <summary>Hash and classify a stable regular source without following any path component.</summary>
    public static async Task<CloudConflictObject> ObserveAsync(string root, string relativePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var rootFd = OpenAbsoluteDirectory(root);
        var rootStat = Stat(rootFd);
        using var sourceFd = OpenRelativeFile(rootFd, relativePath, rootStat);
        var before = Stat(sourceFd);
        var payload = await HashAsync(sourceFd, before.Size, cancellationToken).ConfigureAwait(false);
        var after = Stat(sourceFd);
        RequireStable(before, after);
        var kind = Classify(sourceFd, payload.Prefix, before.Size, cancellationToken);
        RequireStable(before, Stat(sourceFd));
        return new(before.Size, payload.Digest, Identity(before), kind);
    }

    /// <summary>Capture a bounded, descriptor-relative tree of regular files.</summary>
    public static Task<IReadOnlyList<CloudConflictLocalFileMetadata>> CaptureMetadataAsync(string root, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var rootFd = OpenAbsoluteDirectory(root);
        var rootStat = Stat(rootFd);
        var result = new List<CloudConflictLocalFileMetadata>();
        Visit(rootFd, string.Empty, rootStat, 0, result, cancellationToken);
        return Task.FromResult<IReadOnlyList<CloudConflictLocalFileMetadata>>(result);
    }

    /// <summary>Copy an unchanged source into a new private cache file, or verify an existing copy.</summary>
    public static async Task CopyVerifiedAsync(string root, string relativePath, CloudConflictObject expected, string cachePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var rootFd = OpenAbsoluteDirectory(root);
        var rootStat = Stat(rootFd);
        using var sourceFd = OpenRelativeFile(rootFd, relativePath, rootStat);
        var before = Stat(sourceFd);
        if (before.Size != expected.Size || Identity(before) != expected.Identity) throw new IOException("Local source identity changed.");

        var (parent, leaf) = OpenParent(cachePath);
        using (parent)
        {
            if ((Stat(parent).Mode & 0x3f) != 0) throw new IOException("Cache directory is not private.");
            try
            {
                using var existingFd = OpenChild(parent, leaf, O_RDONLY | O_NONBLOCK, false);
                var existing = await HashAndClassifyAsync(existingFd, cancellationToken).ConfigureAwait(false);
                if (existing.Size != expected.Size || !DigestEqual(existing.Sha256, expected.Sha256)) throw new IOException("Existing cache differs from expected content.");
                var source = await HashAsync(sourceFd, before.Size, cancellationToken).ConfigureAwait(false);
                RequireStable(before, Stat(sourceFd));
                if (!DigestEqual(source.Digest, expected.Sha256)) throw new IOException("Local source digest changed.");
                return;
            }
            catch (FileNotFoundException) { }

            var temporary = "." + leaf + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";
            using var tempFd = OpenChild(parent, temporary, O_WRONLY | O_CREAT | O_EXCL, false, 0x180); // 0600
            try
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var source = new FileStream(new SafeFileHandle(Duplicate(sourceFd), ownsHandle: true), FileAccess.Read, 65536, isAsync: false))
                await using (var target = new FileStream(new SafeFileHandle(Duplicate(tempFd), ownsHandle: true), FileAccess.Write, 65536, isAsync: false))
                {
                    var buffer = new byte[65536];
                    long count = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
                    {
                        count = checked(count + read);
                        if (count > before.Size) throw new IOException("Local source grew during copy.");
                        hash.AppendData(buffer, 0, read);
                        await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    }
                    if (count != before.Size) throw new IOException("Local source shrank during copy.");
                    await target.FlushAsync(cancellationToken).ConfigureAwait(false);
                    target.Flush(flushToDisk: true);
                }
                RequireStable(before, Stat(sourceFd));
                if (!DigestEqual(Convert.ToHexStringLower(hash.GetHashAndReset()), expected.Sha256)) throw new IOException("Local content digest changed.");
                if (renameat2(parent.DangerousGetHandle().ToInt32(), temporary, parent.DangerousGetHandle().ToInt32(), leaf, 1) != 0)
                    throw new IOException("Cache destination already exists or could not be created.", new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
            }
            finally { _ = unlinkat(parent.DangerousGetHandle().ToInt32(), temporary, 0); }
        }
    }

    /// <summary>Read a regular cache file through a no-follow descriptor and verify a stable digest.</summary>
    public static async Task<CloudConflictObject> ReadCacheAsync(string cachePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (parent, leaf) = OpenParent(cachePath);
        using (parent)
        using (var fd = OpenChild(parent, leaf, O_RDONLY | O_NONBLOCK, false))
            return await HashAndClassifyAsync(fd, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<CloudConflictObject> HashAndClassifyAsync(SafeFileHandle fd, CancellationToken token)
    {
        var before = Stat(fd);
        RequireRegular(before);
        var payload = await HashAsync(fd, before.Size, token).ConfigureAwait(false);
        RequireStable(before, Stat(fd));
        var kind = Classify(fd, payload.Prefix, before.Size, token);
        RequireStable(before, Stat(fd));
        return new(before.Size, payload.Digest, Identity(before), kind);
    }

    private static async Task<(string Digest, byte[] Prefix)> HashAsync(SafeFileHandle fd, long size, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var stream = new FileStream(new SafeFileHandle(Duplicate(fd), ownsHandle: true), FileAccess.Read, 65536, isAsync: false);
        using var prefix = new MemoryStream(64);
        var buffer = new byte[65536];
        long count = 0;
        var captureXml = false;
        int read;
        while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            count = checked(count + read);
            if (count > size) throw new IOException("File grew during read.");
            hash.AppendData(buffer, 0, read);
            if (count == read) captureXml = LooksLikeXml(buffer.AsSpan(0, read));
            var limit = captureXml ? MaxXmlBytes : 64;
            if (prefix.Length < limit)
                prefix.Write(buffer, 0, (int)Math.Min(read, limit - prefix.Length));
        }
        if (count != size) throw new IOException("File shrank during read.");
        return (Convert.ToHexStringLower(hash.GetHashAndReset()), prefix.ToArray());
    }

    private static bool LooksLikeXml(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) bytes = bytes[3..];
        foreach (var value in bytes)
        {
            if (value is 9 or 10 or 13 or 32) continue;
            return value == '<';
        }
        return false;
    }

    private static CloudConflictContentKind Classify(SafeFileHandle fd, byte[] prefix, long size, CancellationToken token)
    {
        if (prefix.Length >= 3 && prefix[0] == 0xff && prefix[1] == 0xd8 && prefix[2] == 0xff)
            return ValidateJpeg(fd, size, token) ? CloudConflictContentKind.Jpeg : CloudConflictContentKind.Unknown;
        if (prefix.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            return ValidatePng(fd, size, token) ? CloudConflictContentKind.Png : CloudConflictContentKind.Unknown;
        if (size > MaxXmlBytes) return CloudConflictContentKind.Unknown;
        if (prefix.Length != size || !LooksLikeXml(prefix)) return CloudConflictContentKind.Unknown;
        try
        {
            using var input = new MemoryStream(prefix);
            using var xml = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxXmlBytes, IgnoreComments = true });
            xml.MoveToContent();
            var root = xml.LocalName.ToLowerInvariant();
            if (root is not ("artist" or "album" or "song" or "track" or "musicvideo")) return CloudConflictContentKind.Unknown;
            while (xml.Read()) { }
            return CloudConflictContentKind.XmlMetadata;
        }
        catch (XmlException) { return CloudConflictContentKind.Unknown; }
    }

    private static bool ValidatePng(SafeFileHandle fd, long size, CancellationToken token)
    {
        if (size < 45) return false;
        using var stream = Rewind(fd);
        Span<byte> header = stackalloc byte[8];
        Span<byte> imageHeader = stackalloc byte[13];
        Span<byte> expectedCrc = stackalloc byte[4];
        if (!Read(stream, header) || !header.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return false;
        var seenHeader = false;
        var seenPalette = false;
        var seenData = false;
        long dataBytes = 0;
        var dataEnded = false;
        byte colorType = 0;
        var buffer = new byte[65536];
        while (stream.Position < size)
        {
            token.ThrowIfCancellationRequested();
            if (size - stream.Position < 12 || !Read(stream, header)) return false;
            var length = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
            if (length > int.MaxValue || length > size - stream.Position - 4) return false;
            var type = header[4..8];
            foreach (var value in type)
                if (value is < 65 or > 122 || value is > 90 and < 97) return false;
            var ihdr = type.SequenceEqual("IHDR"u8);
            var plte = type.SequenceEqual("PLTE"u8);
            var idat = type.SequenceEqual("IDAT"u8);
            var iend = type.SequenceEqual("IEND"u8);
            if (!seenHeader && (!ihdr || length != 13) || seenHeader && ihdr
                || plte && (seenPalette || seenData || length is < 3 or > 768 || length % 3 != 0 || colorType is 0 or 4)
                || idat && (dataEnded || colorType == 3 && !seenPalette)
                || iend && (!seenData || dataBytes == 0 || length != 0)
                || !ihdr && !plte && !idat && !iend && (type[0] & 0x20) == 0) return false;

            uint crc = 0xffffffff;
            crc = UpdateCrc(crc, type);
            if (ihdr)
            {
                if (!Read(stream, imageHeader)) return false;
                crc = UpdateCrc(crc, imageHeader);
                var width = BinaryPrimitives.ReadUInt32BigEndian(imageHeader[..4]);
                var height = BinaryPrimitives.ReadUInt32BigEndian(imageHeader[4..8]);
                var depth = imageHeader[8]; colorType = imageHeader[9];
                if (width == 0 || height == 0 || !ValidPngDepth(depth, colorType)
                    || imageHeader[10] != 0 || imageHeader[11] != 0 || imageHeader[12] > 1) return false;
                seenHeader = true;
            }
            else
            {
                var remaining = (int)length;
                while (remaining > 0)
                {
                    token.ThrowIfCancellationRequested();
                    var take = Math.Min(remaining, buffer.Length);
                    var read = stream.Read(buffer, 0, take);
                    if (read == 0) return false;
                    crc = UpdateCrc(crc, buffer.AsSpan(0, read));
                    remaining -= read;
                }
            }
            if (!Read(stream, expectedCrc) || BinaryPrimitives.ReadUInt32BigEndian(expectedCrc) != ~crc) return false;
            if (plte) seenPalette = true;
            if (idat) { seenData = true; dataBytes += length; }
            else if (seenData) dataEnded = true;
            if (iend) return stream.Position == size;
        }
        return false;
    }

    private static bool ValidPngDepth(byte depth, byte color) => color switch
    {
        0 => depth is 1 or 2 or 4 or 8 or 16,
        2 => depth is 8 or 16,
        3 => depth is 1 or 2 or 4 or 8,
        4 or 6 => depth is 8 or 16,
        _ => false,
    };

    private static bool ValidateJpeg(SafeFileHandle fd, long size, CancellationToken token)
    {
        if (size < 14) return false;
        using var stream = Rewind(fd);
        Span<byte> frame = stackalloc byte[6];
        if (stream.ReadByte() != 0xff || stream.ReadByte() != 0xd8) return false;
        var seenFrame = false;
        var seenScan = false;
        var entropyBytes = 0L;
        var pendingMarker = -1;
        while (stream.Position < size || pendingMarker >= 0)
        {
            token.ThrowIfCancellationRequested();
            int marker;
            if (pendingMarker >= 0) { marker = pendingMarker; pendingMarker = -1; }
            else
            {
                if (stream.ReadByte() != 0xff) return false;
                do { marker = stream.ReadByte(); } while (marker == 0xff);
            }
            if (marker is < 0xc0 or 0xd8 or >= 0xd0 and <= 0xd7) return false;
            if (marker == 0xd9) return seenFrame && seenScan && entropyBytes > 0 && stream.Position == size;
            var high = stream.ReadByte(); var low = stream.ReadByte();
            if (high < 0 || low < 0) return false;
            var length = (high << 8) | low;
            if (length < 2 || length - 2 > size - stream.Position) return false;
            var payload = length - 2;
            if (IsSof(marker))
            {
                if (seenFrame || payload < 6) return false;
                if (!Read(stream, frame)) return false;
                var components = frame[5];
                if (frame[0] is not (8 or 12 or 16) || frame[1] == 0 && frame[2] == 0
                    || frame[3] == 0 && frame[4] == 0 || components == 0 || length != 8 + 3 * components) return false;
                stream.Seek(payload - 6, SeekOrigin.Current);
                seenFrame = true;
            }
            else if (marker == 0xda)
            {
                if (!seenFrame || payload < 4) return false;
                var components = stream.ReadByte();
                if (components is < 1 or > 4 || length != 6 + 2 * components) return false;
                stream.Seek(payload - 1, SeekOrigin.Current);
                seenScan = true;
                while (stream.Position < size)
                {
                    token.ThrowIfCancellationRequested();
                    var value = stream.ReadByte();
                    if (value != 0xff) { entropyBytes++; continue; }
                    int next;
                    do { next = stream.ReadByte(); } while (next == 0xff);
                    if (next < 0) return false;
                    if (next == 0 || next is >= 0xd0 and <= 0xd7) { entropyBytes++; continue; }
                    pendingMarker = next;
                    break;
                }
            }
            else stream.Seek(payload, SeekOrigin.Current);
        }
        return false;
    }

    private static bool IsSof(int marker) => marker is >= 0xc0 and <= 0xcf and not (0xc4 or 0xc8 or 0xcc);

    private static FileStream Rewind(SafeFileHandle fd)
    {
        if (lseek(fd.DangerousGetHandle().ToInt32(), 0, 0) < 0) throw IoError("lseek");
        return new FileStream(new SafeFileHandle(Duplicate(fd), ownsHandle: true), FileAccess.Read, 65536, isAsync: false);
    }

    private static bool Read(Stream stream, Span<byte> bytes)
    {
        while (!bytes.IsEmpty)
        {
            var read = stream.Read(bytes);
            if (read == 0) return false;
            bytes = bytes[read..];
        }
        return true;
    }

    private static uint[] BuildPngCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++) value = (value & 1) == 0 ? value >> 1 : 0xedb88320 ^ (value >> 1);
            table[i] = value;
        }
        return table;
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var value in data) crc = PngCrcTable[(int)((crc ^ value) & 0xff)] ^ (crc >> 8);
        return crc;
    }

    private static void Visit(SafeFileHandle directory, string relative, Statx root, int depth, List<CloudConflictLocalFileMetadata> result, CancellationToken token)
    {
        if (depth > MaxDepth) throw new IOException("Local source tree exceeds maximum depth.");
        foreach (var path in Directory.EnumerateFileSystemEntries("/proc/self/fd/" + directory.DangerousGetHandle().ToInt64().ToString(CultureInfo.InvariantCulture)))
        {
            token.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            if (name is "." or ".." || name.Contains('/')) throw new IOException("Invalid source entry.");
            using var fd = OpenChild(directory, name, O_RDONLY | O_NONBLOCK, false);
            var stat = Stat(fd);
            RequireSameDevice(root, stat);
            var child = relative.Length == 0 ? name : relative + "/" + name;
            if ((stat.Mode & S_IFMT) == S_IFDIR) Visit(fd, child, root, depth + 1, result, token);
            else
            {
                RequireRegular(stat);
                if (result.Count >= MaxEntries) throw new IOException("Local source tree exceeds maximum entries.");
                result.Add(new(child, stat.Size, LastWriteTicks(stat), Identity(stat)));
            }
        }
    }

    private static SafeFileHandle OpenAbsoluteDirectory(string path)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
            throw new PlatformNotSupportedException("Conflict source reads require Linux x64 or arm64.");
        if (!Path.IsPathFullyQualified(path)) throw new IOException("Expected absolute directory path.");
        var current = OpenFd("/", O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
        try
        {
            foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (part is "." or "..") throw new IOException("Unsafe path component.");
                var next = OpenChild(current, part, O_RDONLY | O_DIRECTORY, true);
                current.Dispose();
                current = next;
            }
            return current;
        }
        catch { current.Dispose(); throw; }
    }

    private static SafeFileHandle OpenRelativeFile(SafeFileHandle root, string relative, Statx rootStat)
    {
        var parts = relative.Split('/');
        if (parts.Length == 0 || parts.Any(part => part.Length == 0 || part is "." or ".." || part.Contains('\\')))
            throw new IOException("Unsafe relative path.");
        var current = new SafeFileHandle((IntPtr)Duplicate(root), ownsHandle: true);
        try
        {
            for (var i = 0; i < parts.Length - 1; i++)
            {
                var next = OpenChild(current, parts[i], O_RDONLY | O_DIRECTORY, true);
                RequireSameDevice(rootStat, Stat(next));
                current.Dispose();
                current = next;
            }
            var file = OpenChild(current, parts[^1], O_RDONLY | O_NONBLOCK, false);
            var stat = Stat(file);
            RequireRegular(stat);
            RequireSameDevice(rootStat, stat);
            return file;
        }
        finally { current.Dispose(); }
    }

    private static (SafeFileHandle Parent, string Leaf) OpenParent(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new IOException("Expected absolute cache path.");
        var leaf = Path.GetFileName(path);
        if (leaf.Length == 0 || leaf is "." or "..") throw new IOException("Invalid cache filename.");
        return (OpenAbsoluteDirectory(Path.GetDirectoryName(path)!), leaf);
    }

    private static SafeFileHandle OpenFd(string path, int flags)
    {
        var fd = open(path, flags, 0);
        return fd >= 0 ? new SafeFileHandle((IntPtr)fd, ownsHandle: true) : throw IoError(path);
    }

    private static SafeFileHandle OpenChild(SafeFileHandle parent, string name, int flags, bool directory, int mode = 0)
    {
        var fd = openat(parent.DangerousGetHandle().ToInt32(), name, flags | O_NOFOLLOW | O_CLOEXEC | (directory ? O_DIRECTORY : 0), mode);
        if (fd >= 0) return new SafeFileHandle((IntPtr)fd, ownsHandle: true);
        var error = Marshal.GetLastPInvokeError();
        if (error == 2) throw new FileNotFoundException("Path component does not exist.", name);
        throw IoError(name);
    }

    private static int Duplicate(SafeFileHandle fd)
    {
        var copy = dup(fd.DangerousGetHandle().ToInt32());
        return copy >= 0 ? copy : throw IoError("dup");
    }

    private static Statx Stat(SafeFileHandle fd)
    {
        if (statx(fd.DangerousGetHandle().ToInt32(), "", AT_EMPTY_PATH, STATX_BASIC_STATS, out var stat) != 0) throw IoError("statx");
        if ((stat.Mask & STATX_BASIC_STATS) != STATX_BASIC_STATS) throw new IOException("Filesystem lacks required stable metadata.");
        return stat;
    }

    private static void RequireRegular(Statx stat)
    {
        if ((stat.Mode & S_IFMT) != S_IFREG) throw new IOException("Source is not a regular file.");
    }

    private static void RequireSameDevice(Statx root, Statx file)
    {
        if (root.DevMajor != file.DevMajor || root.DevMinor != file.DevMinor) throw new IOException("Source crosses a filesystem boundary.");
    }

    private static void RequireStable(Statx before, Statx after)
    {
        if (Identity(before) != Identity(after)) throw new IOException("File changed during read.");
    }

    private static string Identity(Statx s) => string.Join(':', s.DevMajor, s.DevMinor, s.Ino, s.Size,
        s.MTimeSeconds, s.MTimeNanoseconds, s.CTimeSeconds, s.CTimeNanoseconds);

    private static long LastWriteTicks(Statx s) => checked(DateTime.UnixEpoch.Ticks + s.MTimeSeconds * TimeSpan.TicksPerSecond + s.MTimeNanoseconds / 100);
    private static bool DigestEqual(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static IOException IoError(string path) => new($"Cannot access {path}.", new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));

    // Linux statx ABI from linux/stat.h: fixed offsets through stx_dev_minor.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct Statx
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(16)] public uint Nlink;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Ino;
        [FieldOffset(40)] public long Size;
        [FieldOffset(96)] public long CTimeSeconds;
        [FieldOffset(104)] public uint CTimeNanoseconds;
        [FieldOffset(112)] public long MTimeSeconds;
        [FieldOffset(120)] public uint MTimeNanoseconds;
        [FieldOffset(136)] public uint DevMajor;
        [FieldOffset(140)] public uint DevMinor;
    }

    [DllImport("libc", SetLastError = true)] private static extern int open(string path, int flags, int mode);
    [DllImport("libc", SetLastError = true)] private static extern int openat(int directory, string name, int flags, int mode);
    [DllImport("libc", SetLastError = true)] private static extern int dup(int fd);
    [DllImport("libc", SetLastError = true)] private static extern long lseek(int fd, long offset, int whence);
    [DllImport("libc", SetLastError = true)] private static extern int statx(int fd, string path, int flags, int mask, out Statx stat);
    [DllImport("libc", SetLastError = true)] private static extern int unlinkat(int directory, string name, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int renameat2(int oldDirectory, string oldName, int newDirectory, string newName, uint flags);
}
