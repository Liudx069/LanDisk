using LanDisk.Core.Configuration;
using LanDisk.Core.Interop;
using LanDisk.Core.Model;

namespace LanDisk.Core.Paths;

public sealed class ShareUnavailableException : Exception
{
    public ShareUnavailableException(ShareState state, string message) : base(message) => State = state;
    public ShareState State { get; }
}

/// <summary>How a target path should be interpreted.</summary>
public enum ResolveMode
{
    /// <summary>The target must already exist.</summary>
    Existing,
    /// <summary>The parent directory must exist; the final segment must not yet exist.</summary>
    ForCreate,
    /// <summary>Walk as far as possible; missing intermediate directories are an error.</summary>
    ExistingOptional
}

/// <summary>Outcome of resolving a share-relative path to a verified file system object.</summary>
public sealed class ResolvedTarget : IDisposable
{
    public required ShareDefinition Share { get; init; }
    public required string RelativePath { get; init; }
    public required string LexicalPath { get; init; }
    public required OpenedHandle? Opened { get; init; }
    public required bool Exists { get; init; }
    public required string ParentName { get; init; }

    public bool IsDirectory => Opened?.IsDirectory ?? false;
    public long Length => Opened?.Length ?? 0;
    public FileObjectId ObjectId => Opened?.ObjectId ?? default;
    public string FinalPath => Opened?.FinalPath ?? LexicalPath;

    public void Dispose() => Opened?.Dispose();
}

/// <summary>
/// The single chokepoint through which every file endpoint resolves a client supplied
/// shareId + relative path. Directory listing, preview, media, download, upload, rename,
/// delete and restore all go through here; there is no second, weaker path to the disk.
/// </summary>
public sealed class PathResolver
{
    private readonly AppPaths _paths;

    public PathResolver(AppPaths paths) => _paths = paths;

    /// <summary>
    /// Verifies that a share is currently usable: the root exists, its volume identity still
    /// matches the one captured when the share was created, and the root object is the same one.
    /// </summary>
    public ShareHealth ProbeShare(ShareDefinition share)
    {
        if (!Directory.Exists(share.RootPath))
        {
            return new ShareHealth(ShareState.Offline, "共享根目录当前不可访问（磁盘可能已拔出或路径被移动）。");
        }

        OpenedHandle root;
        try
        {
            root = Win32FileSystem.Open(share.RootPath, deleteAccess: false);
        }
        catch (Exception ex)
        {
            return new ShareHealth(ShareState.PermissionDenied, "无法打开共享根目录：" + ex.Message);
        }

        using (root)
        {
            if (!root.IsDirectory)
                return new ShareHealth(ShareState.Offline, "共享根路径不再是目录。");

            if (root.IsReparsePoint)
                return new ShareHealth(ShareState.IdentityMismatch, "共享根目录是重解析点（符号链接/junction/挂载点），本版本拒绝使用。");

            if (!string.IsNullOrEmpty(share.VolumeGuidPath)
                && !string.Equals(root.VolumeGuidPath, share.VolumeGuidPath, StringComparison.OrdinalIgnoreCase))
            {
                return new ShareHealth(ShareState.IdentityMismatch,
                    $"卷身份已变化：登记为 {share.VolumeGuidPath}，当前为 {root.VolumeGuidPath}。需要在本机管理端重新确认。");
            }

            if (!string.IsNullOrEmpty(share.RootObjectId)
                && !string.Equals(root.ObjectId.ToString(), share.RootObjectId, StringComparison.OrdinalIgnoreCase))
            {
                return new ShareHealth(ShareState.IdentityMismatch,
                    "共享根目录对象身份已变化（目录可能被替换或重新格式化），需要在本机管理端重新确认。");
            }
        }

        return new ShareHealth(ShareState.Online, null);
    }

    public ResolvedTarget Resolve(ShareDefinition share, string? relativePath, ResolveMode mode, FileObjectId? expectedObjectId = null, bool forWrite = false)
    {
        ShareHealth health = ProbeShare(share);
        if (!health.State.Equals(ShareState.Online))
            throw new ShareUnavailableException(health.State, health.Detail ?? "共享当前不可用。");

        string[] segments = PathRules.SplitRelative(relativePath);
        string relative = PathRules.JoinSegments(segments);

        string rootFull = PathRules.TrimTrailingSeparator(Path.GetFullPath(share.RootPath));
        string lexical = segments.Length == 0
            ? rootFull
            : Path.Combine([rootFull, .. segments]);

        // Lexical containment is checked first: cheap, and it rejects the obvious cases
        // before any handle is opened. It is never the only check.
        if (!PathRules.IsWithinRoot(rootFull, lexical))
            throw new PathSafetyException(PathRejection.OutsideShare, "目标路径不在共享目录内。");

        GuardInternal(lexical, relative);

        if (segments.Length == 0)
        {
            OpenedHandle rootHandle = Win32FileSystem.Open(rootFull, deleteAccess: forWrite);
            return new ResolvedTarget
            {
                Share = share,
                RelativePath = string.Empty,
                LexicalPath = rootFull,
                Opened = rootHandle,
                Exists = true,
                ParentName = string.Empty
            };
        }

        string parentLexical = segments.Length == 1
            ? rootFull
            : Path.Combine([rootFull, .. segments[..^1]]);
        string leafName = segments[^1];

        // Verify the ancestor chain carries no reparse points. Checking only the leaf would
        // let a junction earlier in the path silently redirect the operation elsewhere.
        VerifyAncestorChain(rootFull, segments);

        if (mode == ResolveMode.ForCreate)
        {
            // The parent must exist, be a directory, and live on the share's volume.
            using OpenedHandle parent = OpenVerifiedDirectory(share, parentLexical);
            string candidate = Path.Combine(parent.FinalPath, leafName);
            if (!PathRules.IsWithinRoot(rootFull, candidate))
                throw new PathSafetyException(PathRejection.OutsideShare, "目标路径不在共享目录内。");
            GuardInternal(candidate, relative);

            if (Directory.Exists(candidate) || File.Exists(candidate))
                throw new PathConflictException("目标名称已存在，本版本不会静默覆盖。");

            return new ResolvedTarget
            {
                Share = share,
                RelativePath = relative,
                LexicalPath = candidate,
                Opened = null,
                Exists = false,
                ParentName = leafName
            };
        }

        OpenedHandle? opened = null;
        try
        {
            opened = OpenExistingVerified(share, rootFull, lexical, forWrite);
        }
        catch (FileNotFoundException) when (mode == ResolveMode.ExistingOptional)
        {
            return new ResolvedTarget
            {
                Share = share,
                RelativePath = relative,
                LexicalPath = lexical,
                Opened = null,
                Exists = false,
                ParentName = leafName
            };
        }
        catch (IOException) when (mode == ResolveMode.ExistingOptional)
        {
            // The name exists but could not be opened (typically a sharing violation from another
            // process). Report it as present without a handle rather than failing the caller, so
            // that a "save as a copy" upload can still proceed.
            return new ResolvedTarget
            {
                Share = share,
                RelativePath = relative,
                LexicalPath = lexical,
                Opened = null,
                Exists = true,
                ParentName = leafName
            };
        }

        if (expectedObjectId is { } expected && !opened.ObjectId.Equals(expected))
        {
            opened.Dispose();
            throw new PathSafetyException(PathRejection.ObjectChanged,
                "目标对象已被替换或修改，请刷新列表后重试。");
        }

        if (forWrite && !opened.IsDirectory && opened.NumberOfLinks > 1)
        {
            opened.Dispose();
            throw new PathSafetyException(PathRejection.HardLink,
                "该文件存在多个硬链接，修改它可能影响共享目录之外的对象，已拒绝本次操作。");
        }

        return new ResolvedTarget
        {
            Share = share,
            RelativePath = relative,
            LexicalPath = lexical,
            Opened = opened,
            Exists = true,
            ParentName = leafName
        };
    }

    private OpenedHandle OpenExistingVerified(ShareDefinition share, string rootFull, string lexical, bool forWrite)
    {
        OpenedHandle opened;
        try
        {
            opened = Win32FileSystem.Open(lexical, deleteAccess: forWrite);
        }
        catch (IOException ex) when (ex.HResult is 2 or 3)
        {
            throw new FileNotFoundException("目标不存在。", lexical, ex);
        }

        try
        {
            if (opened.IsReparsePoint)
                throw new PathSafetyException(PathRejection.ReparsePoint,
                    "目标是符号链接、junction 或其他重解析点，本版本不跟随此类对象。");

            if (!PathRules.IsWithinRoot(rootFull, opened.FinalPath))
                throw new PathSafetyException(PathRejection.OutsideShare,
                    "目标的真实路径不在共享目录内（可能被链接重定向）。");

            if (!string.Equals(opened.VolumeGuidPath, share.VolumeGuidPath, StringComparison.OrdinalIgnoreCase))
                throw new PathSafetyException(PathRejection.VolumeMismatch,
                    "目标位于与共享登记不同的卷上。");

            GuardInternal(opened.FinalPath, PathRules.RelativeTo(rootFull, opened.FinalPath));
            return opened;
        }
        catch
        {
            opened.Dispose();
            throw;
        }
    }

    private OpenedHandle OpenVerifiedDirectory(ShareDefinition share, string lexical)
    {
        OpenedHandle opened = Win32FileSystem.Open(lexical, deleteAccess: false);
        try
        {
            if (!opened.IsDirectory)
                throw new DirectoryNotFoundException("父路径不是目录：" + lexical);
            if (opened.IsReparsePoint)
                throw new PathSafetyException(PathRejection.ReparsePoint, "父目录是重解析点，已拒绝。");
            if (!string.Equals(opened.VolumeGuidPath, share.VolumeGuidPath, StringComparison.OrdinalIgnoreCase))
                throw new PathSafetyException(PathRejection.VolumeMismatch, "父目录位于不同的卷上。");
            GuardInternal(opened.FinalPath, PathRules.RelativeTo(share.RootPath, opened.FinalPath));
            return opened;
        }
        catch
        {
            opened.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Walks every intermediate directory below the share root and refuses the request if any
    /// of them is a reparse point. Combined with the handle-based final path check on the leaf,
    /// this means a junction cannot be used to step outside the share.
    /// </summary>
    private static void VerifyAncestorChain(string rootFull, string[] segments)
    {
        if (segments.Length <= 1) return;

        string current = rootFull;
        for (int i = 0; i < segments.Length - 1; i++)
        {
            current = Path.Combine(current, segments[i]);
            OpenedHandle handle;
            try
            {
                handle = Win32FileSystem.Open(current, deleteAccess: false);
            }
            catch (IOException)
            {
                throw new DirectoryNotFoundException("中间目录不存在：" + current);
            }

            using (handle)
            {
                if (handle.IsReparsePoint)
                    throw new PathSafetyException(PathRejection.ReparsePoint,
                        $"路径中的 '{segments[i]}' 是重解析点（链接/junction/挂载点），本版本不跟随。");
                if (!handle.IsDirectory)
                    throw new DirectoryNotFoundException("路径中的 '" + segments[i] + "' 不是目录。");
            }
        }
    }

    /// <summary>Rejects any path that lands inside LanDisk's own configuration, staging or recycle areas.</summary>
    private void GuardInternal(string candidate, string relative)
    {
        foreach (string internalRoot in _paths.InternalRoots())
        {
            if (string.IsNullOrEmpty(internalRoot)) continue;
            try
            {
                if (PathRules.IsWithinRoot(internalRoot, candidate))
                    throw new PathSafetyException(PathRejection.OutsideShare,
                        "该位置属于 LanDisk 内部数据目录，不允许通过共享访问。");
            }
            catch (PathSafetyException) { throw; }
            catch { }
        }

        // Belt and braces: refuse the well known internal directory name anywhere in the tree.
        foreach (string segment in relative.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(segment, AppPaths.VolumeStoreName, StringComparison.OrdinalIgnoreCase))
                throw new PathSafetyException(PathRejection.OutsideShare,
                    "该位置属于 LanDisk 内部数据目录，不允许通过共享访问。");
        }
    }
}

public readonly record struct ShareHealth(ShareState State, string? Detail);

public sealed class PathConflictException : Exception
{
    public PathConflictException(string message) : base(message) { }
}
