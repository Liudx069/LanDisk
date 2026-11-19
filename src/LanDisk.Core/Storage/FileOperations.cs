using System.Security.Cryptography;
using LanDisk.Core.Interop;
using LanDisk.Core.Model;
using LanDisk.Core.Paths;

namespace LanDisk.Core.Storage;

public sealed class RenameResult
{
    public required string OldRelativePath { get; init; }
    public required string NewRelativePath { get; init; }
    public required string NewName { get; init; }
}

/// <summary>
/// Rename, create-folder and hard delete primitives. Every mutation addresses the object through
/// the handle produced by <see cref="PathResolver"/>, so a path swapped between validation and
/// execution cannot redirect the operation onto a different file.
/// </summary>
public sealed class FileOperations
{
    private readonly PathResolver _resolver;

    public FileOperations(PathResolver resolver) => _resolver = resolver;

    public RenameResult Rename(ShareDefinition share, string? relativePath, string newName, string? versionToken)
    {
        PathRules.ValidateSegment(newName);

        using ResolvedTarget target = _resolver.Resolve(share, relativePath, ResolveMode.Existing, forWrite: true);
        if (target.RelativePath.Length == 0)
            throw new InvalidOperationException("不能重命名共享根目录。");

        if (!target.Opened!.MatchesVersionToken(versionToken))
            throw new PathSafetyException(PathRejection.ObjectChanged, "目标对象已变化，请刷新后重试。");

        string parentRelative = Path.GetDirectoryName(target.RelativePath)?.Replace('/', '\\') ?? string.Empty;
        string parentFull = parentRelative.Length == 0
            ? PathRules.TrimTrailingSeparator(Path.GetFullPath(share.RootPath))
            : Path.Combine(PathRules.TrimTrailingSeparator(Path.GetFullPath(share.RootPath)), parentRelative);

        string? oldName = Path.GetFileName(target.RelativePath);
        bool caseOnly = oldName is not null && string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase);

        if (caseOnly && string.Equals(oldName, newName, StringComparison.Ordinal))
            return new RenameResult { OldRelativePath = target.RelativePath, NewRelativePath = target.RelativePath, NewName = newName };

        string destination = Path.Combine(parentFull, newName);

        if (!caseOnly && (File.Exists(destination) || Directory.Exists(destination)))
            throw new PathConflictException($"目标名称 “{newName}” 已存在，本版本不会覆盖。");

        if (!PathRules.IsWithinRoot(share.RootPath, destination))
            throw new PathSafetyException(PathRejection.OutsideShare, "目标路径不在共享目录内。");

        // Protect the internal store even when the share covers a whole volume.
        if (destination.Split('\\').Any(s => string.Equals(s, Configuration.AppPaths.VolumeStoreName, StringComparison.OrdinalIgnoreCase)))
            throw new PathSafetyException(PathRejection.OutsideShare, "不允许操作 LanDisk 内部目录。");

        if (caseOnly)
        {
            // Windows needs a temporary hop because a pure case change is a no-op rename.
            string temp = Path.Combine(parentFull, $".landisk-casetmp-{Guid.NewGuid():N}");
            Win32FileSystem.RenameByHandle(target.Opened.Handle, temp);
            try
            {
                using OpenedHandle moved = Win32FileSystem.Open(temp, deleteAccess: true);
                Win32FileSystem.RenameByHandle(moved.Handle, destination);
            }
            catch
            {
                // Roll back so the user is never left with a hidden temporary name.
                try
                {
                    using OpenedHandle back = Win32FileSystem.Open(temp, deleteAccess: true);
                    Win32FileSystem.RenameByHandle(back.Handle, Path.Combine(parentFull, oldName!));
                }
                catch { }
                throw;
            }
        }
        else
        {
            Win32FileSystem.RenameByHandle(target.Opened.Handle, destination);
        }

        string newRelative = parentRelative.Length == 0 ? newName : parentRelative + "\\" + newName;
        return new RenameResult { OldRelativePath = target.RelativePath, NewRelativePath = newRelative, NewName = newName };
    }

    public string CreateFolder(ShareDefinition share, string? parentRelative, string name)
    {
        PathRules.ValidateSegment(name);

        using ResolvedTarget parent = _resolver.Resolve(share, parentRelative, ResolveMode.Existing);
        if (!parent.IsDirectory)
            throw new InvalidOperationException("父路径不是目录。");

        string full = Path.Combine(parent.FinalPath, name);
        if (File.Exists(full) || Directory.Exists(full))
            throw new PathConflictException($"名称 “{name}” 已存在。");

        Directory.CreateDirectory(full);

        using OpenedHandle created = Win32FileSystem.Open(full, deleteAccess: false);
        if (!PathRules.IsWithinRoot(share.RootPath, created.FinalPath))
        {
            try { Directory.Delete(full, true); } catch { }
            throw new PathSafetyException(PathRejection.OutsideShare, "新建目录落在了共享范围之外。");
        }

        string relative = parent.RelativePath.Length == 0 ? name : parent.RelativePath + "\\" + name;
        return relative;
    }

    public void HardDelete(string fullPath, bool isDirectory)
    {
        if (isDirectory) HardDeleteDirectory(fullPath);
        else HardDeleteFile(fullPath);
    }

    public void HardDeleteFile(string fullPath)
    {
        using OpenedHandle handle = Win32FileSystem.Open(fullPath, deleteAccess: true);
        if (handle.IsDirectory)
        {
            foreach (string child in Directory.EnumerateFileSystemEntries(fullPath))
            {
                if (Directory.Exists(child)) HardDeleteDirectory(child);
                else HardDeleteFile(child);
            }
            using OpenedHandle dirHandle = Win32FileSystem.Open(fullPath, deleteAccess: true);
            Win32FileSystem.DeleteByHandle(dirHandle.Handle);
        }
        else
        {
            Win32FileSystem.DeleteByHandle(handle.Handle);
        }
    }

    public void HardDeleteDirectory(string fullPath)
    {
        foreach (string child in Directory.EnumerateFileSystemEntries(fullPath))
        {
            uint attrs = NativeMethods.GetFileAttributesW(child);
            if (attrs != NativeMethods.INVALID_FILE_ATTRIBUTES && (attrs & NativeMethods.FILE_ATTRIBUTE_DIRECTORY) != 0)
                HardDeleteDirectory(child);
            else
                HardDeleteFile(child);
        }

        using OpenedHandle handle = Win32FileSystem.Open(fullPath, deleteAccess: true);
        Win32FileSystem.DeleteByHandle(handle.Handle);
    }

    /// <summary>Sums file sizes below a directory, bounded so a huge tree cannot stall a request.</summary>
    public static (long TotalBytes, long FileCount, bool Truncated) MeasureDirectory(string path, int maxEntries = 200_000)
    {
        long total = 0;
        long count = 0;
        var stack = new Stack<string>();
        stack.Push(path);

        while (stack.Count > 0)
        {
            string current = stack.Pop();
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(current); }
            catch { continue; }

            foreach (string entry in entries)
            {
                if (count >= maxEntries) return (total, count, true);
                try
                {
                    uint attrs = NativeMethods.GetFileAttributesW(entry);
                    if (attrs == NativeMethods.INVALID_FILE_ATTRIBUTES) continue;
                    if ((attrs & NativeMethods.FILE_ATTRIBUTE_DIRECTORY) != 0)
                    {
                        stack.Push(entry);
                    }
                    else
                    {
                        total += new FileInfo(entry).Length;
                        count++;
                    }
                }
                catch { }
            }
        }

        return (total, count, false);
    }

    /// <summary>SHA-256 of a file, computed through a read-only handle.</summary>
    public static string HashFile(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static string HashStream(Stream stream)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexStringLower(sha.ComputeHash(stream));
    }
}
