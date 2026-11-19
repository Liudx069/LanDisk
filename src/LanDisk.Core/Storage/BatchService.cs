using System.IO.Compression;
using System.Text;
using LanDisk.Core.Configuration;
using LanDisk.Core.Interop;
using LanDisk.Core.Model;
using LanDisk.Core.Paths;

namespace LanDisk.Core.Storage;

public sealed record BatchRequestItem(string Path, string? Version);

public sealed class BatchItemResult
{
    public required string Path { get; init; }
    public required bool Ok { get; init; }
    public string? ErrorCode { get; init; }
    public string? Error { get; init; }
    public string? RecycleId { get; init; }
    public bool IsDirectory { get; init; }
}

public sealed class BatchZipPlan
{
    public required IReadOnlyList<ZipPlanEntry> Entries { get; init; }
    public required IReadOnlyList<BatchItemResult> Skipped { get; init; }
    public required long TotalBytes { get; init; }
    public required string SuggestedName { get; init; }
    public required string ShareId { get; init; }
    public bool Truncated { get; init; }
    public string? TruncationReason { get; init; }
}

/// <summary>
/// One planned archive entry.
///
/// The absolute path is deliberately NOT the trust anchor. What is recorded is the share, the
/// share-relative path and the object identity observed at plan time; the absolute path is only a
/// display hint. Every entry is re-resolved through <see cref="PathResolver"/> immediately before it
/// is read, and the identity is compared against these values.
/// </summary>
public sealed record ZipPlanEntry(
    string EntryName,
    string RelativePath,
    string DisplayPath,
    string VolumeGuidPath,
    string ObjectId,
    long Length,
    DateTimeOffset LastWriteTime);

/// <summary>Outcome of re-validating a plan against the file system.</summary>
public sealed class BatchRevalidation
{
    public required IReadOnlyList<BatchItemResult> Changed { get; init; }
    public bool Ok => Changed.Count == 0;
}

/// <summary>
/// Streaming batch operations. Nothing here reads a whole selection into memory: the ZIP is
/// produced entry by entry into the response stream, and batch delete reuses the existing,
/// already validated recycle path for every item.
/// </summary>
public sealed class BatchService
{
    /// <summary>Error code used when an entry no longer matches what the user selected.</summary>
    public const string ChangedSinceSelection = "CHANGED_SINCE_SELECTION";

    private readonly PathResolver _resolver;
    private readonly FileOperations _operations;
    private readonly RecycleService _recycle;
    private readonly DirectoryBrowser _browser;
    private readonly AppSettings _settings;

    public BatchService(
        PathResolver resolver,
        FileOperations operations,
        RecycleService recycle,
        DirectoryBrowser browser,
        AppSettings settings)
    {
        _resolver = resolver;
        _operations = operations;
        _recycle = recycle;
        _browser = browser;
        _settings = settings;
    }

    // ---------------------------------------------------------------- delete

    public IReadOnlyList<BatchItemResult> Delete(ShareDefinition share, DeviceDefinition device, IReadOnlyList<BatchRequestItem> items)
    {
        if (items.Count == 0) return [];
        if (items.Count > _settings.BatchDeleteMaxItems)
            throw new InvalidOperationException($"一次最多删除 {_settings.BatchDeleteMaxItems} 项，本次请求 {items.Count} 项。");

        var results = new List<BatchItemResult>(items.Count);

        foreach (BatchRequestItem item in items)
        {
            string path = item.Path ?? string.Empty;
            try
            {
                RecycleEntry entry = _recycle.Delete(share, path, item.Version, OperationActor.FromDevice(device));
                results.Add(new BatchItemResult
                {
                    Path = path,
                    Ok = true,
                    RecycleId = entry.Id,
                    IsDirectory = entry.IsDirectory
                });
            }
            catch (PathSafetyException ex)
            {
                results.Add(new BatchItemResult { Path = path, Ok = false, ErrorCode = "PATH_" + ex.Reason.ToString().ToUpperInvariant(), Error = ex.Message });
            }
            catch (ShareUnavailableException ex)
            {
                results.Add(new BatchItemResult { Path = path, Ok = false, ErrorCode = "SHARE_" + ex.State.ToString().ToUpperInvariant(), Error = ex.Message });
            }
            catch (Exception ex)
            {
                results.Add(new BatchItemResult { Path = path, Ok = false, ErrorCode = "FAILED", Error = ex.Message });
            }
        }

        return results;
    }

    // ---------------------------------------------------------------- zip

    /// <summary>
    /// Resolves every requested item before any byte is streamed. Whatever cannot be included is
    /// reported per item instead of failing the whole request or silently disappearing.
    /// </summary>
    public BatchZipPlan Plan(ShareDefinition share, IReadOnlyList<BatchRequestItem> items, string? archiveName)
    {
        if (items.Count == 0)
            throw new ArgumentException("没有选择任何项目。", nameof(items));

        var entries = new List<ZipPlanEntry>();
        var skipped = new List<BatchItemResult>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        bool truncated = false;
        string? truncationReason = null;

        foreach (BatchRequestItem item in items)
        {
            if (truncated) break;

            string requested = (item.Path ?? string.Empty).Trim();
            if (requested.Length == 0)
            {
                skipped.Add(new BatchItemResult { Path = requested, Ok = false, ErrorCode = "EMPTY_PATH", Error = "路径为空。" });
                continue;
            }

            try
            {
                using ResolvedTarget target = _resolver.Resolve(share, requested, ResolveMode.Existing);

                if (target.IsDirectory)
                {
                    CollectDirectory(target, entries, usedNames, ref totalBytes, ref truncated, ref truncationReason);
                }
                else
                {
                    AddFile(
                        target.RelativePath,
                        SanitizeEntryName(target.RelativePath),
                        target.Length,
                        target.Opened!.VolumeGuidPath,
                        target.Opened.ObjectId.ToString(),
                        target.Opened.LastWriteTime,
                        entries, usedNames, ref totalBytes, ref truncated, ref truncationReason);
                }
            }
            catch (PathSafetyException ex)
            {
                skipped.Add(new BatchItemResult { Path = requested, Ok = false, ErrorCode = "PATH_" + ex.Reason.ToString().ToUpperInvariant(), Error = ex.Message });
            }
            catch (ShareUnavailableException ex)
            {
                skipped.Add(new BatchItemResult { Path = requested, Ok = false, ErrorCode = "SHARE_" + ex.State.ToString().ToUpperInvariant(), Error = ex.Message });
            }
            catch (Exception ex)
            {
                skipped.Add(new BatchItemResult { Path = requested, Ok = false, ErrorCode = "FAILED", Error = ex.Message });
            }
        }

        string name = string.IsNullOrWhiteSpace(archiveName)
            ? $"{SanitizeArchiveName(share.DisplayName)}-{DateTime.Now:yyyyMMdd-HHmmss}.zip"
            : SanitizeArchiveName(archiveName!);

        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) name += ".zip";

        return new BatchZipPlan
        {
            Entries = entries,
            Skipped = skipped,
            TotalBytes = totalBytes,
            SuggestedName = name,
            ShareId = share.Id,
            Truncated = truncated,
            TruncationReason = truncationReason
        };
    }

    private void CollectDirectory(
        ResolvedTarget target,
        List<ZipPlanEntry> entries,
        HashSet<string> usedNames,
        ref long totalBytes,
        ref bool truncated,
        ref string? truncationReason)
    {
        string baseRelative = target.RelativePath;
        string internalStoreName = AppPaths.VolumeStoreName;
        string shareVolumeGuid = target.Opened!.VolumeGuidPath;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            // Nothing carrying the reparse point attribute is followed, so a junction inside the
            // selection cannot pull content from outside the share into the archive.
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
            MaxRecursionDepth = 32
        };

        var enumerable = new System.IO.Enumeration.FileSystemEnumerable<EnumeratedFile>(
            target.FinalPath,
            (ref System.IO.Enumeration.FileSystemEntry entry) => new EnumeratedFile(
                entry.ToFullPath(),
                SafeLength(ref entry),
                SafeLastWrite(ref entry)),
            options)
        {
            ShouldIncludePredicate = (ref System.IO.Enumeration.FileSystemEntry entry) =>
            {
                string name = entry.FileName.ToString();
                if (name is "." or "..") return false;
                if (string.Equals(name, internalStoreName, StringComparison.OrdinalIgnoreCase)) return false;
                if (entry.IsDirectory) return false;
                return true;
            },

            // Only files are reported, but directories must still be descended into. Without an
            // explicit recurse predicate the include predicate is reused and the walk stops at the
            // first level, silently producing an empty folder archive.
            ShouldRecursePredicate = (ref System.IO.Enumeration.FileSystemEntry entry) =>
                !string.Equals(entry.FileName.ToString(), internalStoreName, StringComparison.OrdinalIgnoreCase)
        };

        string rootFull = PathRules.TrimTrailingSeparator(Path.GetFullPath(target.FinalPath));
        string shareRoot = PathRules.TrimTrailingSeparator(Path.GetFullPath(target.Share.RootPath));

        foreach (EnumeratedFile file in enumerable)
        {
            if (truncated) return;

            string relativeToShare;
            try
            {
                // Express the entry relative to the SHARE, not merely to the selected folder, so
                // the stream time re-resolution goes through the same share boundary check.
                relativeToShare = PathRules.RelativeTo(shareRoot, file.FullPath);
            }
            catch
            {
                continue;
            }

            if (relativeToShare.Length == 0) continue;

            string entryName = SanitizeEntryName(
                string.IsNullOrEmpty(baseRelative) ? PathRules.RelativeTo(rootFull, file.FullPath) : baseRelative + "\\" + PathRules.RelativeTo(rootFull, file.FullPath));

            // The file index is not available from directory enumeration, so the plan records the
            // length and timestamp. Stream time re-resolution produces the authoritative identity
            // and is compared against these values.
            AddFile(
                relativeToShare,
                entryName,
                file.Length,
                shareVolumeGuid,
                objectId: null,
                file.LastWriteTime,
                entries, usedNames, ref totalBytes, ref truncated, ref truncationReason);
        }
    }

    private readonly record struct EnumeratedFile(string FullPath, long Length, DateTimeOffset LastWriteTime);

    private static long SafeLength(ref System.IO.Enumeration.FileSystemEntry entry)
    {
        try { return entry.Length; } catch { return 0; }
    }

    private static DateTimeOffset SafeLastWrite(ref System.IO.Enumeration.FileSystemEntry entry)
    {
        try { return entry.LastWriteTimeUtc; } catch { return DateTimeOffset.MinValue; }
    }

    private void AddFile(
        string relativePath,
        string entryName,
        long length,
        string volumeGuidPath,
        string? objectId,
        DateTimeOffset lastWriteTime,
        List<ZipPlanEntry> entries,
        HashSet<string> usedNames,
        ref long totalBytes,
        ref bool truncated,
        ref string? truncationReason)
    {
        if (entries.Count >= _settings.BatchZipMaxEntries)
        {
            truncated = true;
            truncationReason = $"条目数超过上限 {_settings.BatchZipMaxEntries}。";
            return;
        }

        if (totalBytes + length > _settings.BatchZipMaxBytes)
        {
            truncated = true;
            truncationReason = $"总大小超过上限 {PathRules.FormatSize(_settings.BatchZipMaxBytes)}。";
            return;
        }

        string unique = entryName;
        int suffix = 1;
        while (!usedNames.Add(unique))
        {
            unique = AppendSuffix(entryName, suffix++);
            if (suffix > 10_000)
            {
                truncated = true;
                truncationReason = "归档内名称冲突过多。";
                return;
            }
        }

        entries.Add(new ZipPlanEntry(unique, relativePath, relativePath, volumeGuidPath, objectId ?? string.Empty, length, lastWriteTime));
        totalBytes += length;
    }

    /// <summary>
    /// Re-validates every planned entry against the file system right now, through the same
    /// <see cref="PathResolver"/> used by every other file endpoint: share state, share root,
    /// volume identity, reparse points, path containment and object identity are all checked again.
    /// </summary>
    public BatchRevalidation RevalidateAll(ShareDefinition share, BatchZipPlan plan)
    {
        var changed = new List<BatchItemResult>();

        foreach (ZipPlanEntry entry in plan.Entries)
        {
            BatchItemResult? problem = TryValidate(share, entry);
            if (problem is not null) changed.Add(problem);
        }

        return new BatchRevalidation { Changed = changed };
    }

    /// <summary>
    /// Validates one entry and returns the resolved target when it is still the same object.
    /// The caller owns the returned target and must dispose it.
    /// </summary>
    private ResolvedTarget? TryOpenValidated(ShareDefinition share, ZipPlanEntry entry, out BatchItemResult? problem)
    {
        problem = null;

        ResolvedTarget target;
        try
        {
            target = _resolver.Resolve(share, entry.RelativePath, ResolveMode.Existing);
        }
        catch (PathSafetyException ex)
        {
            problem = new BatchItemResult
            {
                Path = entry.DisplayPath,
                Ok = false,
                ErrorCode = ChangedSinceSelection,
                Error = "该条目自选择后已变化（路径校验未通过）：" + ex.Message
            };
            return null;
        }
        catch (ShareUnavailableException ex)
        {
            problem = new BatchItemResult
            {
                Path = entry.DisplayPath,
                Ok = false,
                ErrorCode = "SHARE_" + ex.State.ToString().ToUpperInvariant(),
                Error = ex.Message
            };
            return null;
        }
        catch (Exception ex)
        {
            problem = new BatchItemResult
            {
                Path = entry.DisplayPath,
                Ok = false,
                ErrorCode = ChangedSinceSelection,
                Error = "该条目自选择后已变化（无法解析）：" + ex.Message
            };
            return null;
        }

        if (target.Opened is null || target.IsDirectory)
        {
            problem = new BatchItemResult
            {
                Path = entry.DisplayPath,
                Ok = false,
                ErrorCode = ChangedSinceSelection,
                Error = "该条目自选择后已不再是普通文件。"
            };
            target.Dispose();
            return null;
        }

        if (!string.Equals(target.Opened.VolumeGuidPath, entry.VolumeGuidPath, StringComparison.OrdinalIgnoreCase))
        {
            problem = new BatchItemResult
            {
                Path = entry.DisplayPath,
                Ok = false,
                ErrorCode = ChangedSinceSelection,
                Error = "该条目所在的卷身份已变化（可能换盘或被重新格式化）。"
            };
            target.Dispose();
            return null;
        }

        // Two independent checks, because they catch different things:
        //  * the file index changes when a path is deleted and recreated, or replaced by another
        //    object, which is the "different object at the same path" case;
        //  * length and timestamp change when the same object is rewritten in place, which keeps
        //    its index but no longer holds the content the user selected.
        // Both must match, so neither a swapped disk nor an in-place rewrite is read silently.
        bool objectIdMismatch = entry.ObjectId.Length > 0
            && !string.Equals(target.Opened.ObjectId.ToString(), entry.ObjectId, StringComparison.OrdinalIgnoreCase);

        bool lengthMismatch = target.Opened.Length != entry.Length;

        bool timeMismatch = (target.Opened.LastWriteTime - entry.LastWriteTime).Duration() > TimeSpan.FromSeconds(2);

        if (objectIdMismatch || lengthMismatch || timeMismatch)
        {
            problem = new BatchItemResult
            {
                Path = entry.DisplayPath,
                Ok = false,
                ErrorCode = ChangedSinceSelection,
                Error = objectIdMismatch
                    ? "该条目自选择后已被替换成另一个对象。"
                    : "该条目自选择后内容已被修改。"
            };
            target.Dispose();
            return null;
        }

        return target;
    }

    private BatchItemResult? TryValidate(ShareDefinition share, ZipPlanEntry entry)
    {
        ResolvedTarget? target = TryOpenValidated(share, entry, out BatchItemResult? problem);
        target?.Dispose();
        return problem;
    }

    /// <summary>
    /// Writes the archive directly into <paramref name="output"/>. Each entry is re-resolved through
    /// the resolver immediately before it is read, opened from the verified handle, copied with a
    /// bounded buffer and closed before the next entry starts, so peak memory is one buffer plus the
    /// compressor state regardless of how large the selection is.
    /// </summary>
    public async Task<BatchZipStreamReport> WriteAsync(
        Stream output,
        ShareDefinition share,
        BatchZipPlan plan,
        CancellationToken cancellationToken)
    {
        int written = 0;
        var failedAtTransfer = new List<BatchItemResult>();

        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        if (plan.Skipped.Count > 0 || plan.Truncated)
        {
            WriteManifestEntry(archive, "_LanDisk_未包含的项目.txt", plan);
        }

        byte[] buffer = new byte[1 << 20];

        foreach (ZipPlanEntry entry in plan.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Re-resolution happens per entry, not once per request: the plan is never treated as a
            // permanent trusted reference to a path.
            ResolvedTarget? target = TryOpenValidated(share, entry, out BatchItemResult? problem);
            if (target is null)
            {
                failedAtTransfer.Add(problem!);
                continue;
            }

            using (target)
            {
                try
                {
                    ZipArchiveEntry zipEntry = archive.CreateEntry(entry.EntryName, CompressionLevel.Fastest);
                    zipEntry.LastWriteTime = SafeZipTimestamp(entry.LastWriteTime);

                    using FileStream source = new(target.Opened!.Handle, FileAccess.Read);
                    await using Stream destination = zipEntry.Open();
                    long copied = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        copied += read;
                    }

                    if (copied != entry.Length)
                    {
                        failedAtTransfer.Add(new BatchItemResult
                        {
                            Path = entry.DisplayPath,
                            Ok = false,
                            ErrorCode = "SIZE_CHANGED",
                            Error = $"读取过程中文件长度发生变化（计划 {entry.Length} 字节，实际 {copied} 字节）。"
                        });
                    }

                    written++;
                }
                catch (Exception ex)
                {
                    failedAtTransfer.Add(new BatchItemResult { Path = entry.DisplayPath, Ok = false, ErrorCode = "TRANSFER_FAILED", Error = ex.Message });
                }
            }
        }

        // Entries added after the data entries are still valid; the central directory is only
        // written when the archive is disposed.
        if (failedAtTransfer.Count > 0)
        {
            WriteTransferFailureEntry(archive, failedAtTransfer);
        }

        return new BatchZipStreamReport
        {
            WrittenEntries = written,
            Skipped = plan.Skipped,
            FailedDuringTransfer = failedAtTransfer
        };
    }

    private static void WriteManifestEntry(ZipArchive archive, string name, BatchZipPlan plan)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        using Stream stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));

        writer.WriteLine("LanDisk 批量下载说明");
        writer.WriteLine("生成时间: " + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        writer.WriteLine();

        if (plan.Truncated)
        {
            writer.WriteLine("!! 本次归档被截断：" + (plan.TruncationReason ?? "达到上限"));
            writer.WriteLine();
        }

        if (plan.Skipped.Count > 0)
        {
            writer.WriteLine($"以下 {plan.Skipped.Count} 个项目未能加入归档：");
            foreach (BatchItemResult item in plan.Skipped)
            {
                writer.WriteLine($"  - {item.Path}  原因({item.ErrorCode}): {item.Error}");
            }
            writer.WriteLine();
        }

        writer.WriteLine("其余项目已完整包含在本归档中。");
    }

    private static void WriteTransferFailureEntry(ZipArchive archive, IReadOnlyList<BatchItemResult> failures)
    {
        ZipArchiveEntry entry = archive.CreateEntry("_LanDisk_传输中断的项目.txt", CompressionLevel.NoCompression);
        using Stream stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));

        writer.WriteLine("以下条目在写入归档时失败，可能不完整：");
        foreach (BatchItemResult item in failures)
        {
            writer.WriteLine($"  - {item.Path}  原因({item.ErrorCode}): {item.Error}");
        }
    }

    /// <summary>The timestamp recorded in the plan, clamped to the range ZIP supports.</summary>
    private static DateTimeOffset SafeZipTimestamp(DateTimeOffset value)
    {
        if (value.Year < 1980) return new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        if (value.Year > 2107) return new DateTimeOffset(2107, 12, 31, 23, 59, 58, TimeSpan.Zero);
        return value;
    }

    private static string AppendSuffix(string entryName, int suffix)
    {
        int slash = entryName.LastIndexOf('/');
        string directory = slash >= 0 ? entryName[..(slash + 1)] : string.Empty;
        string name = slash >= 0 ? entryName[(slash + 1)..] : entryName;
        int dot = name.LastIndexOf('.');
        return dot > 0
            ? $"{directory}{name[..dot]} ({suffix}){name[dot..]}"
            : $"{directory}{name} ({suffix})";
    }

    /// <summary>Converts a share relative path into a safe forward-slash entry name.</summary>
    internal static string SanitizeEntryName(string relativePath)
    {
        string value = relativePath.Replace('\\', '/').Trim('/');
        var segments = new List<string>();

        foreach (string segment in value.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or "..") continue;
            // Strip characters that would confuse archive tools or escape the extraction root.
            string cleaned = new(segment.Where(c => c != '\0' && c != ':' && c != '*' && c != '?' && c != '"' && c != '<' && c != '>' && c != '|').ToArray());
            cleaned = cleaned.TrimEnd('.', ' ');
            if (cleaned.Length == 0) continue;
            segments.Add(cleaned);
        }

        return segments.Count == 0 ? "unnamed" : string.Join('/', segments);
    }

    private static string SanitizeArchiveName(string name)
    {
        string cleaned = new(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c) && c != '/' && c != '\\').ToArray());
        cleaned = cleaned.Trim();
        if (cleaned.Length == 0) cleaned = "LanDisk";
        return cleaned.Length > 80 ? cleaned[..80] : cleaned;
    }
}

public sealed class BatchZipStreamReport
{
    public required int WrittenEntries { get; init; }
    public required IReadOnlyList<BatchItemResult> Skipped { get; init; }
    public required IReadOnlyList<BatchItemResult> FailedDuringTransfer { get; init; }
}
