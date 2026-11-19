using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LanDisk.Core.Interop;

/// <summary>Identity of a file system object as reported by an open handle.</summary>
public readonly record struct FileObjectId(uint VolumeSerial, ulong FileIndex)
{
    public override string ToString() => $"{VolumeSerial:X8}-{FileIndex:X16}";
}

/// <summary>Result of opening a handle to a path, plus the facts we need for validation.</summary>
public sealed class OpenedHandle : IDisposable
{
    public required SafeFileHandle Handle { get; init; }
    public required string FinalPath { get; init; }
    public required string VolumeMountPoint { get; init; }
    public required string VolumeGuidPath { get; init; }
    public required FileObjectId ObjectId { get; init; }
    public required uint Attributes { get; init; }
    public required bool IsDirectory { get; init; }
    public required uint NumberOfLinks { get; init; }
    public required long Length { get; init; }
    public required DateTimeOffset LastWriteTime { get; init; }
    public required DateTimeOffset CreationTime { get; init; }

    /// <summary>Version token echoed by clients; cheap to compute and re-checked on every write.</summary>
    public string VersionToken => $"{Length:x}-{LastWriteTime.UtcTicks:x}";

    /// <summary>
    /// Re-checks a client supplied version token. Length must match exactly; the timestamp is
    /// compared with a tolerance because directory entries on FAT/exFAT (typical for removable
    /// media) are cached and have two second granularity, which would otherwise produce
    /// spurious "object changed" failures for files nobody touched.
    /// </summary>
    public bool MatchesVersionToken(string? token, TimeSpan? tolerance = null)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;

        string[] parts = token.Split('-');
        if (parts.Length != 2) return false;
        if (!long.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out long length)) return false;
        if (!long.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out long ticks)) return false;

        if (length != Length) return false;

        TimeSpan allowed = tolerance ?? TimeSpan.FromSeconds(2);
        DateTimeOffset expected;
        try { expected = new DateTimeOffset(ticks, TimeSpan.Zero); }
        catch { return false; }

        return (LastWriteTime - expected).Duration() <= allowed;
    }

    public bool IsReparsePoint => (Attributes & NativeMethods.FILE_ATTRIBUTE_REPARSE_POINT) != 0;
    public bool IsReadOnly => (Attributes & NativeMethods.FILE_ATTRIBUTE_READONLY) != 0;

    public void Dispose() => Handle.Dispose();
}

/// <summary>Thin wrapper around the Win32 handle APIs that LanDisk's safety model depends on.</summary>
[SupportedOSPlatform("windows")]
public static class Win32FileSystem
{
    private const int MaxPathChars = 4096;

    /// <summary>
    /// Opens a handle to <paramref name="path"/> without following a final reparse point.
    ///
    /// Only GENERIC_READ is requested for reads; DELETE is added when the caller may rename or
    /// remove the object. GENERIC_WRITE is deliberately never requested: it would make opening a
    /// file carrying FILE_ATTRIBUTE_READONLY fail, and rename/delete do not need write data access.
    /// Directories require FILE_FLAG_BACKUP_SEMANTICS, so it is always supplied.
    /// </summary>
    public static OpenedHandle Open(string path, bool deleteAccess = false)
    {
        uint access = NativeMethods.GENERIC_READ | NativeMethods.SYNCHRONIZE;
        if (deleteAccess) access |= NativeMethods.DELETE;

        uint share = NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE | NativeMethods.FILE_SHARE_DELETE;
        uint flags = NativeMethods.FILE_FLAG_BACKUP_SEMANTICS | NativeMethods.FILE_FLAG_OPEN_REPARSE_POINT;

        SafeFileHandle handle = NativeMethods.CreateFileW(
            path, access, share, IntPtr.Zero, NativeMethods.OPEN_EXISTING, flags, IntPtr.Zero);

        if (handle.IsInvalid)
        {
            int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException($"CreateFileW failed for '{path}' (Win32 error {err}).", err);
        }

        try
        {
            return Describe(handle, path);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static OpenedHandle Describe(SafeFileHandle handle, string requestedPath)
    {
        if (!NativeMethods.GetFileInformationByHandle(handle, out var info))
        {
            int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            throw new IOException($"GetFileInformationByHandle failed for '{requestedPath}' (Win32 error {err}).", err);
        }

        string finalPath = GetFinalPath(handle, requestedPath);
        string mountPoint = GetVolumeMountPoint(finalPath);
        string volumeGuid = GetVolumeGuidPath(mountPoint);

        ulong index = ((ulong)info.nFileIndexHigh << 32) | info.nFileIndexLow;
        long length = ((long)info.nFileSizeHigh << 32) | info.nFileSizeLow;

        return new OpenedHandle
        {
            Handle = handle,
            FinalPath = finalPath,
            VolumeMountPoint = mountPoint,
            VolumeGuidPath = volumeGuid,
            ObjectId = new FileObjectId(info.dwVolumeSerialNumber, index),
            Attributes = info.dwFileAttributes,
            IsDirectory = (info.dwFileAttributes & NativeMethods.FILE_ATTRIBUTE_DIRECTORY) != 0,
            NumberOfLinks = info.nNumberOfLinks,
            Length = length,
            LastWriteTime = SafeFileTime(info.ftLastWriteTime),
            CreationTime = SafeFileTime(info.ftCreationTime)
        };
    }

    private static DateTimeOffset SafeFileTime(long raw)
    {
        try
        {
            if (raw <= 0) return DateTimeOffset.UnixEpoch;
            return new DateTimeOffset(DateTime.FromFileTimeUtc(raw), TimeSpan.Zero);
        }
        catch
        {
            return DateTimeOffset.UnixEpoch;
        }
    }

    public static string GetFinalPath(SafeFileHandle handle, string requestedPath)
    {
        var buffer = new char[MaxPathChars];
        uint len = NativeMethods.GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, NativeMethods.FILE_NAME_NORMALIZED);
        if (len == 0 || len >= buffer.Length)
        {
            int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            throw new IOException($"GetFinalPathNameByHandleW failed for '{requestedPath}' (Win32 error {err}).", err);
        }

        string raw = new string(buffer, 0, (int)len);
        return Denormalize(raw);
    }

    /// <summary>Strips the \\?\ / \\?\UNC\ prefixes that GetFinalPathNameByHandle returns.</summary>
    public static string Denormalize(string raw)
    {
        const string uncPrefix = @"\\?\UNC\";
        const string prefix = @"\\?\";
        if (raw.StartsWith(uncPrefix, StringComparison.Ordinal))
            return @"\\" + raw[uncPrefix.Length..];
        if (raw.StartsWith(prefix, StringComparison.Ordinal))
            return raw[prefix.Length..];
        return raw;
    }

    /// <summary>Returns the mount point (e.g. D:\) that hosts <paramref name="path"/>.</summary>
    public static string GetVolumeMountPoint(string path)
    {
        var buffer = new char[MaxPathChars];
        if (!NativeMethods.GetVolumePathNameW(path, buffer, (uint)buffer.Length))
        {
            int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            throw new IOException($"GetVolumePathNameW failed for '{path}' (Win32 error {err}).", err);
        }
        int len = Array.IndexOf(buffer, '\0');
        if (len < 0) len = buffer.Length;
        return new string(buffer, 0, len);
    }

    /// <summary>Returns the volume GUID path (\\?\Volume{...}\) for a mount point.</summary>
    public static string GetVolumeGuidPath(string mountPoint)
    {
        var buffer = new char[MaxPathChars];
        if (!NativeMethods.GetVolumeNameForVolumeMountPointW(mountPoint, buffer, (uint)buffer.Length))
        {
            int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            throw new IOException($"GetVolumeNameForVolumeMountPointW failed for '{mountPoint}' (Win32 error {err}).", err);
        }
        int len = Array.IndexOf(buffer, '\0');
        if (len < 0) len = buffer.Length;
        return new string(buffer, 0, len);
    }

    /// <summary>Volume GUID path for an arbitrary path, or null when the path/volume is unavailable.</summary>
    public static string? TryGetVolumeGuidPath(string path)
    {
        try
        {
            string mount = GetVolumeMountPoint(path);
            return GetVolumeGuidPath(mount);
        }
        catch
        {
            return null;
        }
    }

    public static bool IsReparsePoint(string path)
    {
        uint attrs = NativeMethods.GetFileAttributesW(path);
        if (attrs == NativeMethods.INVALID_FILE_ATTRIBUTES) return false;
        return (attrs & NativeMethods.FILE_ATTRIBUTE_REPARSE_POINT) != 0;
    }

    public static bool TryGetVolumeSpace(string path, out ulong freeForCaller, out ulong total, out ulong totalFree)
    {
        bool ok = NativeMethods.GetDiskFreeSpaceExW(path, out freeForCaller, out total, out totalFree);
        return ok;
    }

    /// <summary>
    /// Renames/moves the object behind <paramref name="handle"/> to <paramref name="destination"/>.
    /// Because the source is addressed by handle, the move cannot be redirected by a
    /// concurrent path substitution between validation and execution.
    /// </summary>
    public static void RenameByHandle(SafeFileHandle handle, string destination, bool replaceIfExists = false)
    {
        byte[] nameBytes = Encoding.Unicode.GetBytes(destination);
        // x64 layout of FILE_RENAME_INFO: BOOLEAN(1) + pad(7) + HANDLE(8) + DWORD(4) + WCHAR[]
        int size = 8 + IntPtr.Size + 4 + nameBytes.Length + 2;
        IntPtr buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
        try
        {
            for (int i = 0; i < size; i++) System.Runtime.InteropServices.Marshal.WriteByte(buffer, i, 0);
            System.Runtime.InteropServices.Marshal.WriteByte(buffer, 0, replaceIfExists ? (byte)1 : (byte)0);
            System.Runtime.InteropServices.Marshal.WriteIntPtr(buffer, 8, IntPtr.Zero);
            System.Runtime.InteropServices.Marshal.WriteInt32(buffer, 8 + IntPtr.Size, nameBytes.Length);
            System.Runtime.InteropServices.Marshal.Copy(nameBytes, 0, buffer + 8 + IntPtr.Size + 4, nameBytes.Length);

            if (!NativeMethods.SetFileInformationByHandle(handle, NativeMethods.FileRenameInfo, buffer, (uint)size))
            {
                int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                throw new IOException($"Rename to '{destination}' failed (Win32 error {err}).", err);
            }
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Permanently deletes the object behind <paramref name="handle"/>.</summary>
    public static void DeleteByHandle(SafeFileHandle handle)
    {
        int size = sizeof(uint);
        IntPtr buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
        try
        {
            uint flags = NativeMethods.FILE_DISPOSITION_FLAG_DELETE
                         | NativeMethods.FILE_DISPOSITION_FLAG_POSIX_SEMANTICS
                         | NativeMethods.FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE;
            System.Runtime.InteropServices.Marshal.WriteInt32(buffer, 0, unchecked((int)flags));
            if (!NativeMethods.SetFileInformationByHandle(handle, NativeMethods.FileDispositionInfoEx, buffer, (uint)size))
            {
                int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                throw new IOException($"Permanent delete failed (Win32 error {err}).", err);
            }
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
        }
    }

    public static void Flush(SafeFileHandle handle) => NativeMethods.FlushFileBuffers(handle);
}
