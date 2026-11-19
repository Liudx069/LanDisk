using System.Collections.Concurrent;
using System.Security.Cryptography;
using LanDisk.Core.Configuration;
using LanDisk.Core.Interop;
using LanDisk.Core.Model;
using LanDisk.Core.Paths;
using LanDisk.Core.State;

namespace LanDisk.Core.Storage;

public enum UploadState
{
    Open,
    Committing,
    Committed,
    Failed,
    Cancelled,
    Expired
}

public sealed class UploadSession
{
    public required string Id { get; init; }
    public required string DeviceId { get; init; }
    public required string DeviceName { get; init; }
    public required string ShareId { get; init; }
    public required string TargetRelativePath { get; init; }
    public required string FileName { get; init; }
    public required long DeclaredLength { get; init; }
    public required string ContentFingerprint { get; init; }
    public required string StagingPath { get; init; }
    public required string StoreRootPath { get; init; }
    public required bool StoreIsCrossVolume { get; init; }
    public required string VolumeGuidPath { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset LastActivityAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset ExpiresAt { get; set; }
    public UploadState State { get; set; } = UploadState.Open;
    public long ReceivedBytes { get; set; }
    public string RunningHashHex { get; set; } = string.Empty;
    public string? FinalRelativePath { get; set; }
    public string? Error { get; set; }

    internal IncrementalHash? RunningHash { get; set; }
    internal FileStream? Stream { get; set; }
    internal SemaphoreSlim Gate { get; } = new(1, 1);

    public string Sha256PrefixDisplay => ContentFingerprint;
}

public sealed class ChunkWriteResult
{
    public required long ReceivedBytes { get; init; }
    public required long DeclaredLength { get; init; }
    public required bool IsComplete { get; init; }
}

/// <summary>
/// Chunked, resumable uploads.
///
/// Design rules that the implementation deliberately enforces:
///  * chunks are written sequentially to a staging file on the destination volume; an
///    out-of-order chunk is rejected with the expected offset instead of being applied;
///  * the staging file is never visible in a directory listing and is never downloadable;
///  * committing re-validates the share, the target directory and the volume, then performs a
///    non-overwriting move, so a conflicting file can never be silently replaced;
///  * resuming requires the client to prove that the bytes already on the server are a prefix
///    of the file it is about to continue uploading.
/// </summary>
public sealed class UploadService
{
    private readonly ConcurrentDictionary<string, UploadSession> _sessions = new();
    private readonly PathResolver _resolver;
    private readonly VolumeStoreLocator _locator;
    private readonly AppSettings _settings;
    private readonly AppPaths _paths;
    private readonly AuditLog _audit;

    public UploadService(PathResolver resolver, VolumeStoreLocator locator, AppSettings settings, AppPaths paths, AuditLog audit)
    {
        _resolver = resolver;
        _locator = locator;
        _settings = settings;
        _paths = paths;
        _audit = audit;
    }

    /// <summary>Computes the cheap content fingerprint the client must present at init and resume.</summary>
    public static string ComputeFingerprint(long length, DateTimeOffset lastModified, ReadOnlySpan<byte> head, ReadOnlySpan<byte> tail)
    {
        using var sha = SHA256.Create();
        Span<byte> lengthBytes = stackalloc byte[8];
        BitConverter.TryWriteBytes(lengthBytes, length);
        sha.TransformBlock(lengthBytes.ToArray(), 0, 8, null, 0);
        Span<byte> timeBytes = stackalloc byte[8];
        BitConverter.TryWriteBytes(timeBytes, lastModified.UtcTicks);
        sha.TransformBlock(timeBytes.ToArray(), 0, 8, null, 0);
        if (head.Length > 0) sha.TransformBlock(head.ToArray(), 0, head.Length, null, 0);
        if (tail.Length > 0) sha.TransformBlock(tail.ToArray(), 0, tail.Length, null, 0);
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexStringLower(sha.Hash!);
    }

    public UploadSession Init(
        DeviceDefinition device,
        ShareDefinition share,
        string? targetDirectoryRelative,
        string fileName,
        long declaredLength,
        string contentFingerprint)
    {
        PathRules.ValidateSegment(fileName);

        if (declaredLength < 0)
            throw new ArgumentException("文件长度不能为负数。", nameof(declaredLength));

        string fingerprint = (contentFingerprint ?? string.Empty).Trim().ToLowerInvariant();
        if (fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit))
            throw new ArgumentException("contentFingerprint 必须是 64 位十六进制 SHA-256。", nameof(contentFingerprint));

        int active = _sessions.Values.Count(s => s.DeviceId == device.Id && s.State == UploadState.Open);
        if (active >= _settings.MaxConcurrentUploadsPerDevice)
            throw new InvalidOperationException($"同时进行的上传会话已达上限（{_settings.MaxConcurrentUploadsPerDevice}）。");

        using ResolvedTarget parent = _resolver.Resolve(share, targetDirectoryRelative, ResolveMode.Existing);
        if (!parent.IsDirectory)
            throw new InvalidOperationException("上传目标不是目录。");

        // Reject the case where the name is already a directory. A same-named *file* is not an
        // error here: the client decides between "fail" and "save as a copy" at commit time, and
        // the handle used for this probe is disposed immediately so the file is never left locked.
        using (ResolvedTarget existing = _resolver.Resolve(share, JoinRelative(parent.RelativePath, fileName), ResolveMode.ExistingOptional))
        {
            if (existing.Exists && existing.IsDirectory)
                throw new PathConflictException($"目标位置已存在同名文件夹“{fileName}”。");
        }

        VolumeStore store = _locator.Locate(parent.FinalPath);
        EnsureStagingCapacity(store, declaredLength);

        string id = Guid.NewGuid().ToString("N");
        string stagingPath = Path.Combine(store.StagingDir, id + ".part");

        FileStream stream = new(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1 << 20,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var session = new UploadSession
        {
            Id = id,
            DeviceId = device.Id,
            DeviceName = device.Name,
            ShareId = share.Id,
            TargetRelativePath = parent.RelativePath,
            FileName = fileName,
            DeclaredLength = declaredLength,
            ContentFingerprint = fingerprint,
            StagingPath = stagingPath,
            StoreRootPath = store.RootPath,
            StoreIsCrossVolume = store.IsCrossVolume,
            VolumeGuidPath = store.VolumeGuidPath,
            ExpiresAt = DateTimeOffset.Now.AddMinutes(_settings.UploadIdleTimeoutMinutes),
            Stream = stream,
            RunningHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256),
            ReceivedBytes = 0
        };

        if (!_sessions.TryAdd(id, session))
        {
            stream.Dispose();
            throw new InvalidOperationException("无法创建上传会话。");
        }

        _audit.Write(new AuditEntry
        {
            Category = "upload",
            Action = "init",
            DeviceId = device.Id,
            DeviceName = device.Name,
            ShareId = share.Id,
            RelativePath = JoinRelative(parent.RelativePath, fileName),
            Detail = $"id={id} bytes={declaredLength}",
            Success = true
        });

        return session;
    }

    public UploadSession? Find(string sessionId, string deviceId)
    {
        if (!_sessions.TryGetValue(sessionId, out UploadSession? session)) return null;
        if (!string.Equals(session.DeviceId, deviceId, StringComparison.Ordinal)) return null;
        return session;
    }

    public IReadOnlyList<UploadSession> ListForDevice(string deviceId)
        => _sessions.Values.Where(s => s.DeviceId == deviceId).OrderByDescending(s => s.CreatedAt).ToList();

    /// <summary>
    /// Verifies that the bytes already staged are a prefix of the file the client now wants to
    /// continue. Without this a same-name, same-size but different file would be spliced onto the
    /// existing prefix and silently produce a corrupt result.
    /// </summary>
    public UploadSession Resume(string sessionId, string deviceId, string contentFingerprint, string? prefixSha256)
    {
        UploadSession session = Find(sessionId, deviceId) ?? throw new KeyNotFoundException("上传会话不存在。");
        if (session.State != UploadState.Open)
            throw new InvalidOperationException("上传会话已结束。");
        if (session.ExpiresAt < DateTimeOffset.Now)
        {
            Cancel(session, "会话已过期");
            throw new InvalidOperationException("上传会话已过期，请重新开始。");
        }

        if (!string.Equals(session.ContentFingerprint, (contentFingerprint ?? string.Empty).ToLowerInvariant(), StringComparison.Ordinal))
            throw new PathSafetyException(PathRejection.ObjectChanged,
                "重新选择的文件与原会话内容不一致，已拒绝续传以避免拼装出损坏的文件。");

        if (!string.IsNullOrEmpty(prefixSha256))
        {
            // The running hash is recomputed from the staged bytes rather than trusted from the
            // client, so a client that re-selected a different file cannot splice new bytes onto
            // an existing prefix and produce a silently corrupt result.
            string actual = ComputeStagedPrefixHash(session);
            if (!prefixSha256.Trim().Equals(actual, StringComparison.OrdinalIgnoreCase))
                throw new PathSafetyException(PathRejection.ObjectChanged,
                    "已接收前缀的哈希与客户端不一致，已拒绝续传。");
            session.RunningHashHex = actual;
        }
        else if (session.ReceivedBytes > 0)
        {
            throw new ArgumentException("续传必须提供 prefixSha256 以校验已接收内容。", nameof(prefixSha256));
        }

        session.LastActivityAt = DateTimeOffset.Now;
        session.ExpiresAt = DateTimeOffset.Now.AddMinutes(_settings.UploadIdleTimeoutMinutes);
        return session;
    }

    public async Task<ChunkWriteResult> AppendChunkAsync(UploadSession session, long offset, Stream body, long contentLength, string? chunkSha256, CancellationToken cancellationToken)
    {
        if (session.State != UploadState.Open)
            throw new InvalidOperationException("上传会话已结束。");

        if (contentLength <= 0)
            throw new ArgumentException("分块长度必须大于 0。", nameof(contentLength));

        if (contentLength > _settings.MaxChunkBytes)
            throw new ArgumentException($"单个分块不能超过 {_settings.MaxChunkBytes} 字节。", nameof(contentLength));

        await session.Gate.WaitAsync(cancellationToken);
        try
        {
            if (offset != session.ReceivedBytes)
            {
                throw new UploadOffsetMismatchException(session.ReceivedBytes, offset);
            }

            if (session.ReceivedBytes + contentLength > session.DeclaredLength)
                throw new ArgumentException("分块将超出声明的文件长度。", nameof(contentLength));

            FileStream stream = session.Stream ?? throw new InvalidOperationException("上传流已关闭。");

            byte[] buffer = new byte[Math.Min(contentLength, 256 * 1024)];
            long remaining = contentLength;
            using var chunkHash = string.IsNullOrEmpty(chunkSha256) ? null : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            while (remaining > 0)
            {
                int want = (int)Math.Min(buffer.Length, remaining);
                int read = await body.ReadAsync(buffer.AsMemory(0, want), cancellationToken);
                if (read <= 0) throw new IOException("请求体提前结束，分块不完整。");

                await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                session.RunningHash!.AppendData(buffer, 0, read);
                chunkHash?.AppendData(buffer, 0, read);
                session.ReceivedBytes += read;
                remaining -= read;
            }

            if (chunkHash is not null)
            {
                string actual = Convert.ToHexStringLower(chunkHash.GetHashAndReset());
                if (!actual.Equals(chunkSha256!.Trim(), StringComparison.OrdinalIgnoreCase))
                    throw new PathSafetyException(PathRejection.ObjectChanged, "分块校验和不匹配，已拒绝该分块。");
            }

            session.LastActivityAt = DateTimeOffset.Now;
            session.ExpiresAt = DateTimeOffset.Now.AddMinutes(_settings.UploadIdleTimeoutMinutes);

            return new ChunkWriteResult
            {
                ReceivedBytes = session.ReceivedBytes,
                DeclaredLength = session.DeclaredLength,
                IsComplete = session.ReceivedBytes == session.DeclaredLength
            };
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async Task<UploadSession> CompleteAsync(ShareDefinition share, DeviceDefinition device, UploadSession session, string? overallSha256, string conflictPolicy, CancellationToken cancellationToken)
    {
        await session.Gate.WaitAsync(cancellationToken);
        try
        {
            if (session.State != UploadState.Open)
                throw new InvalidOperationException("上传会话已结束。");

            if (session.ReceivedBytes != session.DeclaredLength)
                throw new InvalidOperationException($"文件尚未接收完整（{session.ReceivedBytes}/{session.DeclaredLength}）。");

            session.State = UploadState.Committing;

            string serverHash = Convert.ToHexStringLower(session.RunningHash!.GetHashAndReset());
            session.RunningHashHex = serverHash;

            if (!string.IsNullOrEmpty(overallSha256)
                && !overallSha256.Trim().Equals(serverHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new PathSafetyException(PathRejection.ObjectChanged, "整文件校验和不匹配，已拒绝提交。");
            }

            FileStream stream = session.Stream ?? throw new InvalidOperationException("上传流已关闭。");
            await stream.FlushAsync(cancellationToken);
            stream.Flush(true);
            stream.Dispose();
            session.Stream = null;

            string finalRelative = Commit(share, session, conflictPolicy);

            session.State = UploadState.Committed;
            session.FinalRelativePath = finalRelative;

            _audit.Write(new AuditEntry
            {
                Category = "upload",
                Action = "commit",
                DeviceId = device.Id,
                DeviceName = device.Name,
                ShareId = share.Id,
                RelativePath = finalRelative,
                Detail = $"id={session.Id} bytes={session.DeclaredLength} sha256={serverHash}",
                Success = true
            });

            return session;
        }
        catch (Exception ex)
        {
            session.State = UploadState.Failed;
            session.Error = ex.Message;
            CloseAndDeleteStaging(session);

            _audit.Write(new AuditEntry
            {
                Category = "upload",
                Action = "commit",
                DeviceId = device.Id,
                DeviceName = device.Name,
                ShareId = share.Id,
                RelativePath = JoinRelative(session.TargetRelativePath, session.FileName),
                Detail = ex.Message,
                Success = false
            });

            throw;
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private string Commit(ShareDefinition share, UploadSession session, string conflictPolicy)
    {
        string name = session.FileName;
        string relative = JoinRelative(session.TargetRelativePath, name);

        // Re-validate the destination directory now: the share may have been disabled, the disk
        // removed, or the directory renamed while the bytes were in flight.
        ResolvedTarget destination;
        try
        {
            destination = _resolver.Resolve(share, relative, ResolveMode.ForCreate, forWrite: true);
        }
        catch (PathConflictException) when (string.Equals(conflictPolicy, "rename", StringComparison.OrdinalIgnoreCase))
        {
            (string renamed, ResolvedTarget target) = ResolveAlternateName(share, session.TargetRelativePath, name);
            name = renamed;
            destination = target;
        }

        using (destination)
        {
            string finalPath = destination.LexicalPath;

            if (!session.StoreIsCrossVolume)
            {
                try
                {
                    File.Move(session.StagingPath, finalPath, overwrite: false);
                }
                catch (IOException ex) when (File.Exists(finalPath))
                {
                    throw new PathConflictException("提交时发现同名文件，已拒绝覆盖：" + ex.Message);
                }
            }
            else
            {
                using (var source = new FileStream(session.StagingPath, FileMode.Open, FileAccess.Read, FileShare.None))
                using (var target = new FileStream(finalPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.WriteThrough))
                {
                    source.CopyTo(target, 1 << 20);
                    target.Flush(true);
                }
                File.Delete(session.StagingPath);
            }

            return JoinRelative(session.TargetRelativePath, name);
        }
    }

    private (string, ResolvedTarget) ResolveAlternateName(ShareDefinition share, string directory, string name)
    {
        string stem = Path.GetFileNameWithoutExtension(name);
        string extension = Path.GetExtension(name);

        for (int i = 1; i <= 200; i++)
        {
            string candidate = $"{stem} ({i}){extension}";
            try
            {
                ResolvedTarget target = _resolver.Resolve(share, JoinRelative(directory, candidate), ResolveMode.ForCreate, forWrite: true);
                return (candidate, target);
            }
            catch (PathConflictException) { }
        }

        throw new PathConflictException("同名文件过多，无法自动另存。请先整理目标目录。");
    }

    public void Cancel(UploadSession session, string reason)
    {
        session.State = UploadState.Cancelled;
        session.Error = reason;
        CloseAndDeleteStaging(session);
        _sessions.TryRemove(session.Id, out _);
    }

    /// <summary>Cancels every session belonging to a revoked device.</summary>
    public int CancelForDevice(string deviceId, string reason)
    {
        int count = 0;
        foreach (UploadSession session in _sessions.Values.Where(s => s.DeviceId == deviceId).ToList())
        {
            Cancel(session, reason);
            count++;
        }
        return count;
    }

    private static void CloseAndDeleteStaging(UploadSession session)
    {
        try
        {
            session.Stream?.Dispose();
            session.Stream = null;
        }
        catch { }

        try
        {
            if (File.Exists(session.StagingPath)) File.Delete(session.StagingPath);
        }
        catch { }
    }

    /// <summary>
    /// Removes expired sessions and any staging file that no live session owns.
    /// Only files LanDisk itself created in its own staging directory are ever touched.
    /// </summary>
    public int Reconcile()
    {
        int cleaned = 0;
        DateTimeOffset now = DateTimeOffset.Now;

        foreach (UploadSession session in _sessions.Values.ToList())
        {
            if (session.State == UploadState.Open && session.ExpiresAt < now)
            {
                Cancel(session, "空闲超时");
                cleaned++;
            }
        }

        var live = _sessions.Values.Select(s => s.StagingPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string stagingRoot in EnumerateStagingRoots())
        {
            try
            {
                if (!Directory.Exists(stagingRoot)) continue;
                foreach (string file in Directory.EnumerateFiles(stagingRoot, "*.part"))
                {
                    if (live.Contains(file)) continue;
                    var info = new FileInfo(file);
                    if (now - info.LastWriteTimeUtc > TimeSpan.FromMinutes(_settings.UploadIdleTimeoutMinutes))
                    {
                        try { info.Delete(); cleaned++; } catch { }
                    }
                }
            }
            catch { }
        }

        return cleaned;
    }

    private IEnumerable<string> EnumerateStagingRoots()
    {
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            string? root = null;
            try
            {
                if (drive.DriveType is DriveType.Fixed or DriveType.Removable)
                    root = drive.RootDirectory.FullName;
            }
            catch { }
            if (root is null) continue;

            string primary = Path.Combine(root, AppPaths.VolumeStoreName, "staging");
            if (Directory.Exists(primary)) yield return primary;
        }

        string fallbackRoot = Path.Combine(_paths.FallbackStoreDir, "*", "staging");
        yield return fallbackRoot;
    }

    private void EnsureStagingCapacity(VolumeStore store, long declaredLength)
    {
        long quota = _settings.StagingQuotaBytes;
        if (!store.TryGetFreeSpace(out ulong free, out _, out _)) return;

        long used = 0;
        try
        {
            if (Directory.Exists(store.StagingDir))
            {
                foreach (string file in Directory.EnumerateFiles(store.StagingDir))
                {
                    try { used += new FileInfo(file).Length; } catch { }
                }
            }
        }
        catch { }

        long effectiveQuota = quota > 0 ? quota : (long)Math.Min(50L * 1024 * 1024 * 1024, (long)free / 4);

        if (used + declaredLength > effectiveQuota)
            throw new IOException($"暂存空间不足：已用 {PathRules.FormatSize(used)}，本次需要 {PathRules.FormatSize(declaredLength)}，上限 {PathRules.FormatSize(effectiveQuota)}。");

        if (declaredLength > 0 && (ulong)declaredLength > free)
            throw new IOException($"目标磁盘可用空间不足：需要 {PathRules.FormatSize(declaredLength)}，可用 {PathRules.FormatSize((long)free)}。");
    }

    private static string JoinRelative(string directory, string name)
        => string.IsNullOrEmpty(directory) ? name : directory + "\\" + name;

    /// <summary>SHA-256 over the first <see cref="UploadSession.ReceivedBytes"/> staged bytes.</summary>
    private static string ComputeStagedPrefixHash(UploadSession session)
    {
        if (session.ReceivedBytes <= 0) return Convert.ToHexStringLower(SHA256.HashData([]));

        session.Stream?.Flush();

        using var stream = new FileStream(session.StagingPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        byte[] buffer = new byte[1 << 20];
        long remaining = session.ReceivedBytes;
        while (remaining > 0)
        {
            int want = (int)Math.Min(buffer.Length, remaining);
            int read = stream.Read(buffer, 0, want);
            if (read <= 0) break;
            sha.AppendData(buffer, 0, read);
            remaining -= read;
        }

        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }
}

public sealed class UploadOffsetMismatchException : Exception
{
    public UploadOffsetMismatchException(long expected, long actual)
        : base($"分块偏移不正确：服务器已接收 {expected} 字节，收到偏移 {actual}。分块必须按顺序上传。")
    {
        Expected = expected;
        Actual = actual;
    }

    public long Expected { get; }
    public long Actual { get; }
}
