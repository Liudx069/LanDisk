using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace LanDisk.Core.Interop;

/// <summary>
/// Minimal, explicit P/Invoke surface used by LanDisk. Everything that touches the file
/// system outside of the .NET BCL lives here so the trust boundary stays auditable in one file.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class NativeMethods
{
    internal const uint GENERIC_READ = 0x80000000;
    internal const uint GENERIC_WRITE = 0x40000000;
    internal const uint GENERIC_ALL = 0x10000000;

    internal const uint DELETE = 0x00010000;
    internal const uint FILE_READ_ATTRIBUTES = 0x0080;
    internal const uint FILE_READ_DATA = 0x0001;
    internal const uint FILE_READ_EA = 0x0008;
    internal const uint FILE_WRITE_DATA = 0x0002;
    internal const uint FILE_WRITE_ATTRIBUTES = 0x0100;
    internal const uint SYNCHRONIZE = 0x00100000;

    internal const uint FILE_SHARE_READ = 0x00000001;
    internal const uint FILE_SHARE_WRITE = 0x00000002;
    internal const uint FILE_SHARE_DELETE = 0x00000004;

    internal const uint OPEN_EXISTING = 3;
    internal const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    internal const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    internal const uint FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000;

    internal const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
    internal const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x00000400;
    internal const uint FILE_ATTRIBUTE_READONLY = 0x00000001;
    internal const uint INVALID_FILE_ATTRIBUTES = 0xFFFFFFFF;

    internal const int ERROR_SUCCESS = 0;
    internal const int ERROR_FILE_NOT_FOUND = 2;
    internal const int ERROR_PATH_NOT_FOUND = 3;
    internal const int ERROR_ACCESS_DENIED = 5;
    internal const int ERROR_ALREADY_EXISTS = 183;
    internal const int ERROR_NOT_SAME_DEVICE = 17;
    internal const int ERROR_FILE_EXISTS = 80;
    internal const int ERROR_DIR_NOT_EMPTY = 145;

    internal const uint FILE_NAME_NORMALIZED = 0x0;
    internal const uint VOLUME_NAME_DOS = 0x0;

    internal const int FileRenameInfo = 3;
    internal const int FileDispositionInfo = 4;
    internal const int FileDispositionInfoEx = 21;

    internal const uint FILE_DISPOSITION_FLAG_DELETE = 0x00000001;
    internal const uint FILE_DISPOSITION_FLAG_POSIX_SEMANTICS = 0x00000002;
    internal const uint FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE = 0x00000010;

    // Pack = 4 is required: FILETIME is a pair of DWORDs and the native structure is 4 byte
    // aligned. Without it the CLR would place the 64-bit fields on 8 byte boundaries and every
    // field after dwFileAttributes would be read at the wrong offset.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct BY_HANDLE_FILE_INFORMATION
    {
        public uint dwFileAttributes;
        public long ftCreationTime;
        public long ftLastAccessTime;
        public long ftLastWriteTime;
        public uint dwVolumeSerialNumber;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint nNumberOfLinks;
        public uint nFileIndexHigh;
        public uint nFileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FILE_DISPOSITION_INFO_EX
    {
        public uint Flags;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out BY_HANDLE_FILE_INFORMATION lpFileInformation);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle hFile,
        [Out] char[] lpszFilePath,
        uint cchFilePath,
        uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool GetVolumePathNameW(
        string lpszFileName,
        [Out] char[] lpszVolumePathName,
        uint cchBufferLength);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool GetVolumeNameForVolumeMountPointW(
        string lpszVolumeMountPoint,
        [Out] char[] lpszVolumeName,
        uint cchBufferLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool SetFileInformationByHandle(
        SafeFileHandle hFile,
        int FileInformationClass,
        IntPtr lpFileInformation,
        uint dwBufferSize);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern uint GetFileAttributesW(string lpFileName);

    // CharSet.Unicode is required: without it the CLR marshals the path as ANSI while still
    // calling the W entry point, the call fails, and every disk space check silently no-ops.
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool GetDiskFreeSpaceExW(
        string lpDirectoryName,
        out ulong lpFreeBytesAvailableToCaller,
        out ulong lpTotalNumberOfBytes,
        out ulong lpTotalNumberOfFreeBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool FlushFileBuffers(SafeFileHandle hFile);
}
