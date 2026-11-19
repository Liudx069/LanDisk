using LanDisk.Core.Configuration;
using LanDisk.Core.Interop;
using LanDisk.Core.Model;
using LanDisk.Core.Paths;
using LanDisk.Core.State;

namespace LanDisk.Core.Storage;

public sealed class RecycleService
{
    private readonly RecycleIndex _index;
    private readonly VolumeStoreLocator _locator;
    private readonly PathResolver _resolver;
    private readonly FileOperations _operations;
    private readonly AuditLog _audit;

    public RecycleService(RecycleIndex index, VolumeStoreLocator locator, PathResolver resolver, FileOperations operations, AuditLog audit)
    {
        _index = index;
        _locator = locator;
        _resolver = resolver;
        _operations = operations;
        _audit = audit;
    }

    public IReadOnlyList<RecycleEntry> List(Func<RecycleEntry, bool>? predicate = null)
    {
        IReadOnlyList<RecycleEntry> live = _index.Live();
        IEnumerable<RecycleEntry> query = live.OrderByDescending(e => e.DeletedAt);
        if (predicate is not null) query = query.Where(predicate);
        return query.ToList();
    }

    public RecycleEntry? Find(string id) => _index.Find(id);

    /// <summary>
    /// Moves an object into LanDisk's own recycle store. This is never a permanent delete:
    /// if the payload cannot be staged the operation fails and the original stays untouched.
    /// </summary>
    public RecycleEntry Delete(ShareDefinition share, string? relativePath, string? versionToken, OperationActor actor)
    {
        using ResolvedTarget target = _resolver.Resolve(share, relativePath, ResolveMode.Existing, forWrite: true);
        if (target.RelativePath.Length == 0)
            throw new InvalidOperationException("不能删除共享根目录。");

        if (!target.Opened!.MatchesVersionToken(versionToken))
            throw new PathSafetyException(PathRejection.ObjectChanged, "目标对象已变化，请刷新后重试。");

        bool isDirectory = target.IsDirectory;
        long length = isDirectory ? 0 : target.Opened.Length;
        long fileCount = 1;
        if (isDirectory)
        {
            (long total, long count, _) = FileOperations.MeasureDirectory(target.FinalPath);
            length = total;
            fileCount = count;
        }

        VolumeStore store = _locator.Locate(target.FinalPath);
        string entryId = Guid.NewGuid().ToString("N");
        string payloadDir = Path.Combine(store.RecycleDir, entryId);
        Directory.CreateDirectory(payloadDir);
        string payloadPath = Path.Combine(payloadDir, "data");

        var entry = new RecycleEntry
        {
            Id = entryId,
            ShareId = share.Id,
            OriginalRelativePath = target.RelativePath,
            DisplayName = target.ParentName,
            IsDirectory = isDirectory,
            Length = length,
            VolumeGuidPath = target.Opened.VolumeGuidPath,
            SourceObjectId = target.Opened.ObjectId.ToString(),
            StoredRelativePath = Path.Combine(entryId, "data"),
            WasCopied = store.IsCrossVolume,
            DeletedByDeviceId = actor.Id,
            DeletedByDeviceName = actor.Name,
            State = RecycleState.Staged
        };

        // Journal first: a crash after this point is always reconcilable.
        _index.Add(entry);

        try
        {
            if (!store.IsCrossVolume)
            {
                Win32FileSystem.RenameByHandle(target.Opened.Handle, payloadPath);
            }
            else
            {
                string partial = payloadPath + ".part";
                entry.ContentHash = CopyTreeVerified(target.FinalPath, partial, isDirectory);
                File.Move(partial, payloadPath, true);
                _index.Update(entry.Id, e => { e.ContentHash = entry.ContentHash; });
                entry.ContentHash = entry.ContentHash;
                _operations.HardDelete(target.FinalPath, isDirectory);
            }

            _index.Update(entry.Id, e =>
            {
                e.State = RecycleState.Committed;
                e.ContentHash = entry.ContentHash;
            });
            entry.State = RecycleState.Committed;

            _audit.Write(new AuditEntry
            {
                Category = "recycle",
                Action = "delete",
                DeviceId = actor.IsDevice ? actor.Id : null,
                DeviceName = actor.Name,
                Source = actor.SourceToken,
                ShareId = share.Id,
                RelativePath = target.RelativePath,
                Detail = $"id={entry.Id} bytes={length} files={fileCount} copied={store.IsCrossVolume}",
                Success = true
            });

            return entry;
        }
        catch (Exception ex)
        {
            // Roll the payload back if we can; the original is still in place in every
            // failure mode that reaches this handler on the same-volume path.
            try { if (Directory.Exists(payloadDir)) Directory.Delete(payloadDir, true); } catch { }
            _index.Update(entry.Id, e => { e.State = RecycleState.Failed; });
            _audit.Write(new AuditEntry
            {
                Category = "recycle",
                Action = "delete",
                DeviceId = actor.IsDevice ? actor.Id : null,
                DeviceName = actor.Name,
                Source = actor.SourceToken,
                ShareId = share.Id,
                RelativePath = target.RelativePath,
                Detail = ex.Message,
                Success = false
            });
            throw;
        }
    }

    /// <summary>Restores a recycled object back into its share.</summary>
    public RecycleEntry Restore(string entryId, ShareDefinition share, OperationActor actor, string? alternateName = null)
    {
        RecycleEntry entry = _index.Find(entryId) ?? throw new KeyNotFoundException("回收站条目不存在。");
        if (entry.State is RecycleState.Restored or RecycleState.Purged)
            throw new InvalidOperationException("该条目已被恢复或已永久清空。");
        if (!string.Equals(entry.ShareId, share.Id, StringComparison.Ordinal))
            throw new InvalidOperationException("该条目不属于当前共享。");

        VolumeStore store = LocateStoreForEntry(entry);
        string payloadPath = Path.Combine(store.RecycleDir, entry.StoredRelativePath);
        if (!File.Exists(payloadPath) && !Directory.Exists(payloadPath))
            throw new FileNotFoundException("回收站中的内容已丢失，无法恢复。", payloadPath);

        string targetRelative = entry.OriginalRelativePath;
        if (!string.IsNullOrEmpty(alternateName))
        {
            string parent = Path.GetDirectoryName(entry.OriginalRelativePath)?.Replace('/', '\\') ?? string.Empty;
            targetRelative = parent.Length == 0 ? alternateName : parent + "\\" + alternateName;
        }

        using ResolvedTarget destination = _resolver.Resolve(share, targetRelative, ResolveMode.ForCreate, forWrite: true);

        if (store.IsCrossVolume)
        {
            CopyTreeVerified(payloadPath, destination.LexicalPath, entry.IsDirectory);
            _operations.HardDelete(payloadPath, entry.IsDirectory);
        }
        else
        {
            using OpenedHandle payload = Win32FileSystem.Open(payloadPath, deleteAccess: true);
            Win32FileSystem.RenameByHandle(payload.Handle, destination.LexicalPath);
        }

        _index.Update(entry.Id, e => { e.State = RecycleState.Restored; });
        CleanupEmptyPayloadDir(payloadPath);

        _audit.Write(new AuditEntry
        {
            Category = "recycle",
            Action = "restore",
            DeviceId = actor.IsDevice ? actor.Id : null,
            DeviceName = actor.Name,
            Source = actor.SourceToken,
            ShareId = share.Id,
            RelativePath = targetRelative,
            Detail = $"id={entry.Id}",
            Success = true
        });

        entry.State = RecycleState.Restored;
        return entry;
    }

    /// <summary>Permanently removes a recycled payload. Only reachable from the local admin surface.</summary>
    public void Purge(string entryId, OperationActor? actor)
    {
        RecycleEntry entry = _index.Find(entryId) ?? throw new KeyNotFoundException("回收站条目不存在。");
        if (entry.State is RecycleState.Purged) return;

        VolumeStore store = LocateStoreForEntry(entry);
        string payloadPath = Path.Combine(store.RecycleDir, entry.StoredRelativePath);

        try
        {
            if (Directory.Exists(payloadPath)) _operations.HardDelete(payloadPath, true);
            else if (File.Exists(payloadPath)) _operations.HardDelete(payloadPath, false);
        }
        catch (Exception ex)
        {
            _index.Update(entry.Id, e => { e.State = RecycleState.Failed; });
            throw new IOException("永久清空失败：" + ex.Message, ex);
        }

        _index.Update(entry.Id, e => { e.State = RecycleState.Purged; });
        CleanupEmptyPayloadDir(payloadPath);
        _index.Remove(entry.Id);

        _audit.Write(new AuditEntry
        {
            Category = "recycle",
            Action = "purge",
            DeviceId = actor is { IsDevice: true } ? actor.Id : null,
            DeviceName = actor?.Name ?? OperationActor.LocalAdminName,
            Source = actor?.SourceToken ?? OperationActor.LocalAdmin.SourceToken,
            ShareId = entry.ShareId,
            RelativePath = entry.OriginalRelativePath,
            Detail = $"id={entry.Id}",
            Success = true
        });
    }

    private VolumeStore LocateStoreForEntry(RecycleEntry entry)
    {
        string? mountPoint = VolumeStoreLocator.FindMountPointForVolume(entry.VolumeGuidPath);
        if (mountPoint is null)
            throw new ShareUnavailableException(ShareState.Offline,
                "该条目所在的卷当前未挂载，无法访问回收内容。");

        VolumeStore store = _locator.Locate(mountPoint);
        if (!string.Equals(store.VolumeGuidPath, entry.VolumeGuidPath, StringComparison.OrdinalIgnoreCase))
            throw new ShareUnavailableException(ShareState.IdentityMismatch,
                "回收内容登记的卷身份与当前挂载的卷不一致。");
        return store;
    }

    private static void CleanupEmptyPayloadDir(string payloadPath)
    {
        try
        {
            string? dir = Path.GetDirectoryName(payloadPath);
            if (dir is not null && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir, true);
        }
        catch { }
    }

    private static string CopyTreeVerified(string source, string destination, bool isDirectory)
    {
        if (!isDirectory)
        {
            return CopyFileVerified(source, destination);
        }

        Directory.CreateDirectory(destination);
        var hashes = new List<string>();
        var stack = new Stack<(string Source, string Destination)>();
        stack.Push((source, destination));

        while (stack.Count > 0)
        {
            (string src, string dst) = stack.Pop();
            foreach (string entry in Directory.EnumerateFileSystemEntries(src))
            {
                string name = Path.GetFileName(entry);
                string target = Path.Combine(dst, name);
                uint attrs = NativeMethods.GetFileAttributesW(entry);
                if (attrs != NativeMethods.INVALID_FILE_ATTRIBUTES && (attrs & NativeMethods.FILE_ATTRIBUTE_DIRECTORY) != 0)
                {
                    Directory.CreateDirectory(target);
                    stack.Push((entry, target));
                }
                else
                {
                    hashes.Add(name + ":" + CopyFileVerified(entry, target));
                }
            }
        }

        hashes.Sort(StringComparer.Ordinal);
        byte[] combined = System.Text.Encoding.UTF8.GetBytes(string.Join('\n', hashes));
        return Security.SecretTokens.Sha256Hex(combined);
    }

    private static string CopyFileVerified(string source, string destination)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.WriteThrough);
        using var sha = System.Security.Cryptography.SHA256.Create();

        byte[] buffer = new byte[1 << 20];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, read);
            sha.TransformBlock(buffer, 0, read, null, 0);
        }
        sha.TransformFinalBlock([], 0, 0);
        output.Flush(true);
        return Convert.ToHexStringLower(sha.Hash!);
    }

    /// <summary>
    /// Reconciler run at startup. It only ever deletes LanDisk's own staged payloads; a payload
    /// whose original still exists is discarded so the user keeps their file exactly where it was.
    /// </summary>
    public ReconcileReport Reconcile(IReadOnlyList<ShareDefinition> shares)
    {
        int committed = 0, failed = 0, orphaned = 0;
        var report = new ReconcileReport();

        foreach (RecycleEntry entry in _index.All().ToList())
        {
            if (entry.State is RecycleState.Restored or RecycleState.Purged)
            {
                _index.Remove(entry.Id);
                continue;
            }

            string? mountPoint = VolumeStoreLocator.FindMountPointForVolume(entry.VolumeGuidPath);
            if (mountPoint is null)
            {
                report.UnreachableVolumes.Add(entry.Id);
                continue;
            }

            VolumeStore store = _locator.Locate(mountPoint);
            string payloadPath = Path.Combine(store.RecycleDir, entry.StoredRelativePath);
            bool payloadExists = File.Exists(payloadPath) || Directory.Exists(payloadPath);

            ShareDefinition? share = shares.FirstOrDefault(s => s.Id == entry.ShareId);
            bool originalExists = false;
            if (share is not null && !string.IsNullOrEmpty(entry.OriginalRelativePath))
            {
                try
                {
                    string candidate = Path.Combine(PathRules.TrimTrailingSeparator(Path.GetFullPath(share.RootPath)), entry.OriginalRelativePath);
                    originalExists = File.Exists(candidate) || Directory.Exists(candidate);
                }
                catch { }
            }

            if (entry.State == RecycleState.Committed)
            {
                if (payloadExists) { committed++; }
                else { _index.Update(entry.Id, e => e.State = RecycleState.Failed); failed++; }
                continue;
            }

            // Staged
            if (payloadExists && !originalExists)
            {
                _index.Update(entry.Id, e => e.State = RecycleState.Committed);
                committed++;
            }
            else if (payloadExists && originalExists)
            {
                // The move never happened (rename is atomic) or the copy finished but the
                // original delete did not. Either way the user's file is intact, so discard
                // the staged payload rather than risk deleting their data twice.
                try { _operations.HardDelete(payloadPath, Directory.Exists(payloadPath)); } catch { }
                _index.Update(entry.Id, e => e.State = RecycleState.Failed);
                orphaned++;
            }
            else
            {
                _index.Update(entry.Id, e => e.State = RecycleState.Failed);
                failed++;
            }
        }

        report.Committed = committed;
        report.Failed = failed;
        report.OrphanedCleanups = orphaned;
        return report;
    }
}

public sealed class ReconcileReport
{
    public int Committed { get; set; }
    public int Failed { get; set; }
    public int OrphanedCleanups { get; set; }
    public List<string> UnreachableVolumes { get; } = [];
}
