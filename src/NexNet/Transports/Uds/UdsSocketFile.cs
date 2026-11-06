using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace NexNet.Transports.Uds;

/// <summary>
/// Socket file helpers for the UDS server: probing a path for a listener, telling a socket file from any other file
/// and reading a file's identity. Every native call fails closed: an error means "unknown", never "socket".
/// </summary>
internal static partial class UdsSocketFile
{
    internal enum ProbeResult
    {
        /// <summary>Something accepted the connection.</summary>
        Listening,

        /// <summary>The connection was refused: nothing listens on the path.</summary>
        Refused,

        /// <summary>Any other outcome (timeout, access denied, ...).</summary>
        Unknown,
    }

    /// <summary>
    /// The identity of a file: device/volume and inode/file index.
    /// </summary>
    internal readonly record struct FileIdentity(ulong Device, ulong Inode);

    /// <summary>
    /// Returns the filesystem path of <paramref name="endPoint"/>, or null when it has none (an abstract socket).
    /// </summary>
    public static string? GetFilePath(UnixDomainSocketEndPoint endPoint)
    {
        var address = endPoint.Serialize();
        // The family takes two bytes; an abstract address starts with a NUL byte.
        if (address.Size <= 2 || address[2] == 0)
            return null;

        var path = endPoint.ToString();
        return string.IsNullOrEmpty(path) ? null : Path.GetFullPath(path);
    }

    /// <summary>
    /// Tries to connect to <paramref name="path"/> and reports whether something listens on it.
    /// </summary>
    public static async ValueTask<ProbeResult> ProbeAsync(string path, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.IP);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), cts.Token).ConfigureAwait(false);
            return ProbeResult.Listening;
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return ProbeResult.Refused;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ProbeResult.Unknown;
        }
        catch (SocketException)
        {
            return ProbeResult.Unknown;
        }
    }

    /// <summary>
    /// True only when <paramref name="path"/> itself (not a link target) is confirmed to be a socket file.
    /// </summary>
    public static bool IsSocketFile(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return WindowsIsSocketFile(path);
            if (OperatingSystem.IsLinux())
                return LinuxStat(path, out var mode, out _) && (mode & S_IFMT) == S_IFSOCK;
            if (OperatingSystem.IsMacOS())
                return MacStat(path, out var mode, out _) && (mode & S_IFMT) == S_IFSOCK;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or IOException
                                      or UnauthorizedAccessException or ArgumentException)
        {
            // Unknown: never treated as a socket.
        }

        return false;
    }

    /// <summary>
    /// Reads the identity of <paramref name="path"/> itself (links are not followed); null when it cannot be read.
    /// </summary>
    public static FileIdentity? TryGetIdentity(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return WindowsGetIdentity(path);
            if (OperatingSystem.IsLinux())
                return LinuxStat(path, out _, out var id) ? id : null;
            if (OperatingSystem.IsMacOS())
                return MacStat(path, out _, out var id) ? id : null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or IOException
                                      or UnauthorizedAccessException or ArgumentException)
        {
            // Unknown.
        }

        return null;
    }

    /// <summary>
    /// Deletes <paramref name="path"/> only if it still has <paramref name="identity"/>. Never throws.
    /// </summary>
    public static bool DeleteIfSame(string path, FileIdentity? identity)
    {
        if (identity == null)
            return false;

        try
        {
            if (TryGetIdentity(path) != identity)
                return false;

            File.Delete(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private const uint S_IFMT = 0xF000;
    private const uint S_IFSOCK = 0xC000;

    // ---- Linux: statx has the same layout on every architecture. ----

    private const int AT_FDCWD = -100;
    private const int AT_SYMLINK_NOFOLLOW = 0x100;
    private const uint STATX_TYPE = 0x1;
    private const uint STATX_INO = 0x100;

    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static unsafe partial int statx(int dirfd, string path, int flags, uint mask, byte* buffer);

    private static unsafe bool LinuxStat(string path, out uint mode, out FileIdentity identity)
    {
        mode = 0;
        identity = default;
        // struct statx is 256 bytes.
        var buffer = stackalloc byte[256];
        new Span<byte>(buffer, 256).Clear();
        if (statx(AT_FDCWD, path, AT_SYMLINK_NOFOLLOW, STATX_TYPE | STATX_INO, buffer) != 0)
            return false;

        var returnedMask = *(uint*)buffer; // stx_mask @0
        if ((returnedMask & (STATX_TYPE | STATX_INO)) != (STATX_TYPE | STATX_INO))
            return false;

        mode = *(ushort*)(buffer + 28); // stx_mode @28
        var ino = *(ulong*)(buffer + 32); // stx_ino @32
        var devMajor = *(uint*)(buffer + 136); // stx_dev_major @136
        var devMinor = *(uint*)(buffer + 140); // stx_dev_minor @140
        identity = new FileIdentity(((ulong)devMajor << 32) | devMinor, ino);
        return true;
    }

    // ---- macOS: the 64-bit-inode struct stat (lstat on arm64, lstat$INODE64 on x64). ----

    [LibraryImport("libc", EntryPoint = "lstat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static unsafe partial int lstat_arm64(string path, byte* buffer);

    [LibraryImport("libc", EntryPoint = "lstat$INODE64", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static unsafe partial int lstat_x64(string path, byte* buffer);

    private static unsafe bool MacStat(string path, out uint mode, out FileIdentity identity)
    {
        mode = 0;
        identity = default;
        var arch = RuntimeInformation.ProcessArchitecture;
        if (arch != Architecture.Arm64 && arch != Architecture.X64)
            return false;

        // struct stat (64-bit inode) is 144 bytes.
        var buffer = stackalloc byte[256];
        new Span<byte>(buffer, 256).Clear();
        var result = arch == Architecture.Arm64 ? lstat_arm64(path, buffer) : lstat_x64(path, buffer);
        if (result != 0)
            return false;

        var dev = *(uint*)buffer; // st_dev @0 (int32)
        mode = *(ushort*)(buffer + 4); // st_mode @4
        var ino = *(ulong*)(buffer + 8); // st_ino @8
        identity = new FileIdentity(dev, ino);
        return true;
    }

    // ---- Windows ----

    private const uint IO_REPARSE_TAG_AF_UNIX = 0x80000023;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    private const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;
    private const uint FILE_READ_ATTRIBUTES = 0x80;
    private const uint FILE_SHARE_ALL = 0x1 | 0x2 | 0x4;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const int FileAttributeTagInfo = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_ATTRIBUTE_TAG_INFO
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetFileInformationByHandleEx(SafeFileHandle file, int fileInformationClass,
        void* fileInformation, uint bufferSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetFileInformationByHandle(SafeFileHandle file, BY_HANDLE_FILE_INFORMATION* fileInformation);

    private static SafeFileHandle? WindowsOpen(string path)
    {
        // FILE_FLAG_OPEN_REPARSE_POINT opens the reparse point itself, never what it points to.
        var handle = CreateFile(path, FILE_READ_ATTRIBUTES, FILE_SHARE_ALL, 0, OPEN_EXISTING,
            FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS, 0);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        return handle;
    }

    private static unsafe bool WindowsIsSocketFile(string path)
    {
        using var handle = WindowsOpen(path);
        if (handle == null)
            return false;

        FILE_ATTRIBUTE_TAG_INFO info;
        if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfo, &info, (uint)sizeof(FILE_ATTRIBUTE_TAG_INFO)))
            return false;

        return (info.FileAttributes & FILE_ATTRIBUTE_DIRECTORY) == 0
               && (info.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0
               && info.ReparseTag == IO_REPARSE_TAG_AF_UNIX;
    }

    private static unsafe FileIdentity? WindowsGetIdentity(string path)
    {
        using var handle = WindowsOpen(path);
        if (handle == null)
            return null;

        BY_HANDLE_FILE_INFORMATION info;
        if (!GetFileInformationByHandle(handle, &info))
            return null;

        return new FileIdentity(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
    }
}

/// <summary>
/// Wraps a <see cref="UnixDomainSocketEndPoint"/> so that <see cref="Socket"/> does not record the bound file name:
/// .NET deletes that file by path when the socket closes, even if another file has replaced it. The server deletes
/// its socket file itself, after checking the file's identity.
/// </summary>
internal sealed class UdsNoAutoDeleteEndPoint : EndPoint
{
    private readonly UnixDomainSocketEndPoint _inner;

    public UdsNoAutoDeleteEndPoint(UnixDomainSocketEndPoint inner) => _inner = inner;

    public override AddressFamily AddressFamily => _inner.AddressFamily;

    public override SocketAddress Serialize() => _inner.Serialize();

    public override EndPoint Create(SocketAddress socketAddress) => _inner.Create(socketAddress);

    public override string ToString() => _inner.ToString();
}
