using LanDisk.Core.Interop;
using LanDisk.Core.Media;
using LanDisk.Core.Model;
using LanDisk.Core.Paths;
using LanDisk.Core.Storage;
using LanDisk.Server.Services;

namespace LanDisk.Server.Endpoints;

public static class FileEndpoints
{
    private const int StreamBufferSize = 256 * 1024;

    public sealed record RenameRequest(string? ShareId, string? Path, string? NewName, string? Version);
    public sealed record FolderRequest(string? ShareId, string? Path, string? Name);
    public sealed record DeleteRequest(string? ShareId, string? Path, string? Version);

    public static void MapFileEndpoints(this WebApplication app)
    {
        app.MapGet("/api/shares", (HttpContext context, LanDiskRuntime runtime) =>
        {
            RequestSession session = context.Session();
            if (session.Device is null && session.Admin is null)
                return ApiHelpers.Fail(StatusCodes.Status401Unauthorized, "NOT_AUTHENTICATED", "尚未配对。");

            var list = runtime.Shares.All()
                .Where(s => session.Admin is not null || s.Enabled)
                .Select(s => session.Admin is not null
                    ? AuthEndpoints.AdminShareDto(s)
                    : AuthEndpoints.DeviceShareDto(runtime, s, session.Device!))
                .ToList();

            return Results.Json(new { shares = list }, ApiHelpers.Json);
        });

        app.MapGet("/api/browse", (HttpContext context, LanDiskRuntime runtime,
            string? shareId, string? path, string? sort, bool? desc, int? offset, int? limit,
            string? filter, string? search, string? scope, string? category,
            string? after, string? before, long? minSize, long? maxSize, bool? filesOnly) =>
        {
            try
            {
                if (!ApiHelpers.TryAuthorizeShare(context, runtime, shareId, ShareCapabilities.Read, out ShareDefinition share, out IResult? error))
                    return error!;

                DirectorySortField field = Enum.TryParse(sort, ignoreCase: true, out DirectorySortField parsed)
                    ? parsed
                    : DirectorySortField.Name;

                BrowseScope browseScope = Enum.TryParse(scope, ignoreCase: true, out BrowseScope parsedScope)
                    ? parsedScope
                    : BrowseScope.Folder;

                FileCategory? categoryFilter = null;
                if (!string.IsNullOrWhiteSpace(category)
                    && Enum.TryParse(category, ignoreCase: true, out FileCategory parsedCategory))
                {
                    categoryFilter = parsedCategory;
                }

                // "filter" is the original single-directory name filter; "search" additionally
                // allows a recursive scope. Both end up in the same server side pipeline.
                string? searchTerm = !string.IsNullOrWhiteSpace(search) ? search : filter;
                if (string.IsNullOrWhiteSpace(search) && !string.IsNullOrWhiteSpace(filter)) browseScope = BrowseScope.Folder;

                RequestSession session = context.Session();
                DeviceAccessLevel level = session.Device?.Level ?? DeviceAccessLevel.ReadWrite;

                var query = new BrowseQuery
                {
                    Sort = field,
                    Descending = desc ?? false,
                    Search = searchTerm,
                    Scope = browseScope,
                    Category = categoryFilter,
                    ModifiedAfter = ParseDate(after, endOfDay: false),
                    ModifiedBefore = ParseDate(before, endOfDay: true),
                    MinSize = minSize,
                    MaxSize = maxSize,
                    FilesOnly = filesOnly ?? false,
                    Offset = offset ?? 0,
                    Limit = limit ?? runtime.Settings.ListingPageSize,
                    MaxScan = runtime.Settings.SearchMaxScanEntries,
                    MaxResults = runtime.Settings.SearchMaxResults
                };

                DirectoryListing listing = runtime.Browser.List(share, path, level, query);

                return Results.Json(new
                {
                    shareId = share.Id,
                    shareName = share.DisplayName,
                    shareState = share.State.ToString(),
                    shareStateDetail = share.StateDetail,
                    path = listing.DisplayPath,
                    breadcrumbs = listing.Breadcrumbs.Select(b => new { name = b.Name, path = b.RelativePath.Replace('\\', '/') }),
                    entries = listing.Entries.Select(EntryDto),
                    total = listing.TotalCount,
                    offset = listing.Offset,
                    limit = listing.Limit,
                    hasMore = listing.HasMore,
                    scanMs = listing.ScanMilliseconds,
                    scanned = listing.ScannedCount,
                    truncated = listing.Truncated,
                    sort = listing.Sort.ToString(),
                    descending = listing.Descending,
                    scope = browseScope.ToString(),
                    capabilities = new
                    {
                        canRead = LanDiskRuntime.Allows(share, level, ShareCapabilities.Read),
                        canUpload = LanDiskRuntime.Allows(share, level, ShareCapabilities.Upload),
                        canRename = LanDiskRuntime.Allows(share, level, ShareCapabilities.Rename),
                        canDelete = LanDiskRuntime.Allows(share, level, ShareCapabilities.Delete)
                    }
                }, ApiHelpers.Json);
            }
            catch (Exception ex)
            {
                return ApiHelpers.MapException(ex);
            }
        });

        app.MapGet("/api/file/meta", (HttpContext context, LanDiskRuntime runtime, string? shareId, string? path) =>
        {
            try
            {
                if (!ApiHelpers.TryAuthorizeShare(context, runtime, shareId, ShareCapabilities.Read, out ShareDefinition share, out IResult? error))
                    return error!;

                using ResolvedTarget target = runtime.Resolver.Resolve(share, path, ResolveMode.Existing);
                string name = target.ParentName;
                ContentKind kind = target.IsDirectory ? ContentKind.Binary : MimeTypes.Classify(name);

                return Results.Json(new
                {
                    name,
                    path = target.RelativePath.Replace('\\', '/'),
                    isDirectory = target.IsDirectory,
                    length = target.Length,
                    sizeText = PathRules.FormatSize(target.Length),
                    modifiedAt = target.Opened!.LastWriteTime.ToLocalTime(),
                    createdAt = target.Opened!.CreationTime.ToLocalTime(),
                    version = target.Opened!.VersionToken,
                    objectId = target.Opened!.ObjectId.ToString(),
                    mimeType = target.IsDirectory ? "inode/directory" : MimeTypes.ForFileName(name),
                    contentKind = kind.ToString(),
                    canPreview = !target.IsDirectory && kind == ContentKind.SafeInline,
                    canPlay = !target.IsDirectory && kind is ContentKind.Audio or ContentKind.Video,
                    volumeGuidPath = target.Opened!.VolumeGuidPath,
                    volumeMountPoint = target.Opened!.VolumeMountPoint
                }, ApiHelpers.Json);
            }
            catch (Exception ex)
            {
                return ApiHelpers.MapException(ex);
            }
        });

        // Plain text preview. Returns UTF-8 text as JSON so the client can display it as text
        // rather than letting the browser interpret it as markup.
        app.MapGet("/api/file/text", (HttpContext context, LanDiskRuntime runtime, string? shareId, string? path) =>
        {
            try
            {
                if (!ApiHelpers.TryAuthorizeShare(context, runtime, shareId, ShareCapabilities.Read, out ShareDefinition share, out IResult? error))
                    return error!;

                using ResolvedTarget target = runtime.Resolver.Resolve(share, path, ResolveMode.Existing);
                if (target.IsDirectory)
                    return ApiHelpers.Fail(StatusCodes.Status400BadRequest, "NOT_A_FILE", "目标是目录。");

                string name = target.ParentName;
                if (!MimeTypes.IsText(name))
                    return ApiHelpers.Fail(StatusCodes.Status415UnsupportedMediaType, "NOT_TEXT", "该文件不是支持的文本类型。");

                long max = runtime.Settings.TextPreviewMaxBytes;
                long length = target.Length;
                bool truncated = length > max;

                using var stream = new FileStream(target.Opened!.Handle, FileAccess.Read);
                int toRead = (int)Math.Min(length, max);
                byte[] buffer = new byte[toRead];
                int read = stream.ReadAtLeast(buffer, toRead, throwOnEndOfStream: false);

                if (!HasTextBom(buffer.AsSpan(0, read)) && LooksBinary(buffer.AsSpan(0, read)))
                    return ApiHelpers.Fail(StatusCodes.Status415UnsupportedMediaType, "BINARY_CONTENT", "文件内容看起来不是文本。");

                string text = DecodeText(buffer.AsSpan(0, read));

                return Results.Json(new
                {
                    name,
                    path = target.RelativePath.Replace('\\', '/'),
                    length,
                    truncated,
                    encoding = "utf-8/utf-16 auto",
                    text
                }, ApiHelpers.Json);
            }
            catch (Exception ex)
            {
                return ApiHelpers.MapException(ex);
            }
        });

        // Download: always an attachment, supports Range so downloads can be resumed.
        app.MapMethods("/api/file/download", ["GET", "HEAD"], async (HttpContext context, LanDiskRuntime runtime, string? shareId, string? path) =>
        {
            try
            {
                if (!ApiHelpers.TryAuthorizeShare(context, runtime, shareId, ShareCapabilities.Read, out ShareDefinition share, out IResult? error))
                    return error!;

                using ResolvedTarget target = runtime.Resolver.Resolve(share, path, ResolveMode.Existing);
                if (target.IsDirectory)
                    return ApiHelpers.Fail(StatusCodes.Status400BadRequest, "IS_DIRECTORY", "目录不能作为文件下载。");

                string transferId = RecordDownload(runtime, context, share, target);
                return await ServeDownloadAsync(runtime, context, transferId, target, target.ParentName, attachment: true);
            }
            catch (Exception ex)
            {
                return ApiHelpers.MapException(ex);
            }
        });

        // Inline preview / media streaming. Active content (HTML, SVG, scripts) is forced to
        // download even if the client asks for inline rendering.
        app.MapMethods("/api/file/content", ["GET", "HEAD"], async (HttpContext context, LanDiskRuntime runtime, string? shareId, string? path, string? disposition) =>
        {
            try
            {
                if (!ApiHelpers.TryAuthorizeShare(context, runtime, shareId, ShareCapabilities.Read, out ShareDefinition share, out IResult? error))
                    return error!;

                using ResolvedTarget target = runtime.Resolver.Resolve(share, path, ResolveMode.Existing);
                if (target.IsDirectory)
                    return ApiHelpers.Fail(StatusCodes.Status400BadRequest, "IS_DIRECTORY", "目录不能作为文件读取。");

                string name = target.ParentName;
                ContentKind kind = MimeTypes.Classify(name);
                bool wantsInline = string.Equals(disposition, "inline", StringComparison.OrdinalIgnoreCase);

                if (kind == ContentKind.ActiveContent)
                {
                    // Never render active content inside the application origin.
                    return await ServeAsync(context, target, name, attachment: true, allowInlineActive: false);
                }

                if (wantsInline && kind is ContentKind.SafeInline or ContentKind.Audio or ContentKind.Video)
                {
                    if (kind == ContentKind.SafeInline && MimeTypes.IsImage(name) && target.Length > runtime.Settings.InlinePreviewMaxBytes)
                        return ApiHelpers.Fail(StatusCodes.Status413PayloadTooLarge, "PREVIEW_TOO_LARGE",
                            "图片过大，无法在线预览，请下载后查看。");
                    return await ServeAsync(context, target, name, attachment: false, allowInlineActive: false);
                }

                string transferId2 = RecordDownload(runtime, context, share, target);
                return await ServeDownloadAsync(runtime, context, transferId2, target, name, attachment: true);
            }
            catch (Exception ex)
            {
                return ApiHelpers.MapException(ex);
            }
        });

        app.MapPost("/api/file/rename", (HttpContext context, LanDiskRuntime runtime, RenameRequest request) =>
        {
            try
            {
                if (!ApiHelpers.TryAuthorizeShare(context, runtime, request.ShareId, ShareCapabilities.Rename, out ShareDefinition share, out IResult? error))
                    return error!;

                RenameResult result = runtime.Operations.Rename(share, request.Path, request.NewName ?? string.Empty, request.Version);
                runtime.InvalidateScans(share.Id);

                runtime.Audit.Write(new AuditEntry
                {
                    Category = "file",
                    Action = "rename",
                    DeviceId = context.Session().Device?.Id,
                    DeviceName = context.Session().DisplayName,
                    ShareId = share.Id,
                    RelativePath = result.NewRelativePath,
                    Detail = "from=" + result.OldRelativePath,
                    Success = true
                });

                return Results.Json(new
                {
                    ok = true,
                    oldPath = result.OldRelativePath.Replace('\\', '/'),
                    path = result.NewRelativePath.Replace('\\', '/'),
                    name = result.NewName
                }, ApiHelpers.Json);
            }
            catch (Exception ex)
            {
                return ApiHelpers.MapException(ex);
            }
        });

        app.MapPost("/api/file/folder", (HttpContext context, LanDiskRuntime runtime, FolderRequest request) =>
        {
            try
            {
                if (!ApiHelpers.TryAuthorizeShare(context, runtime, request.ShareId, ShareCapabilities.Upload, out ShareDefinition share, out IResult? error))
                    return error!;

                string relative = runtime.Operations.CreateFolder(share, request.Path, request.Name ?? string.Empty);
                runtime.InvalidateScans(share.Id);

                runtime.Audit.Write(new AuditEntry
                {
                    Category = "file",
                    Action = "mkdir",
                    DeviceId = context.Session().Device?.Id,
                    DeviceName = context.Session().DisplayName,
                    ShareId = share.Id,
                    RelativePath = relative,
                    Success = true
                });

                return Results.Json(new { ok = true, path = relative.Replace('\\', '/') }, ApiHelpers.Json);
            }
            catch (Exception ex)
            {
                return ApiHelpers.MapException(ex);
            }
        });

        app.MapPost("/api/file/delete", (HttpContext context, LanDiskRuntime runtime, DeleteRequest request) =>
        {
            try
            {
                if (!ApiHelpers.TryAuthorizeShare(context, runtime, request.ShareId, ShareCapabilities.Delete, out ShareDefinition share, out IResult? error))
                    return error!;

                RequestSession session = context.Session();
                OperationActor actor = session.Device is null ? OperationActor.LocalAdmin : OperationActor.FromDevice(session.Device);

                RecycleEntry entry = runtime.Recycle.Delete(share, request.Path, request.Version, actor);
                runtime.InvalidateScans(share.Id);

                return Results.Json(new
                {
                    ok = true,
                    recycleId = entry.Id,
                    name = entry.DisplayName,
                    isDirectory = entry.IsDirectory,
                    length = entry.Length,
                    message = "已移入 LanDisk 回收站，可从回收站恢复。"
                }, ApiHelpers.Json);
            }
            catch (Exception ex)
            {
                return ApiHelpers.MapException(ex);
            }
        });
    }

    /// <summary>Shared entry projection used by browse, search and recent so every list looks the same.</summary>
    internal static object EntryDto(DirectoryEntryDto e) => new
    {
        name = e.Name,
        path = e.RelativePath.Replace('\\', '/'),
        isDirectory = e.IsDirectory,
        length = e.Length,
        // Directories are not scanned, so they report "—" rather than a misleading 0 B.
        sizeText = PathRules.FormatEntrySize(e.Length, e.IsDirectory),
        modifiedAt = e.ModifiedAt,
        modifiedUtc = e.ModifiedAt.ToUniversalTime(),
        createdAt = e.CreatedAt,
        version = e.ObjectId,
        isHidden = e.IsHidden,
        isLink = e.IsReparsePoint,
        contentKind = e.ContentKind,
        category = e.Category.ToString().ToLowerInvariant(),
        mimeType = e.MimeType,
        canPreview = e.CanPreview,
        canPlay = e.CanPlay,
        hasThumbnail = !e.IsDirectory && (e.Category == FileCategory.Image)
    };

    /// <summary>Parses an ISO date or date-time query value into a local DateTimeOffset bound.</summary>
    private static DateTimeOffset? ParseDate(string? value, bool endOfDay)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        if (DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeLocal, out DateTimeOffset parsed))
        {
            // A bare date means "the whole day", not midnight.
            if (value.Length <= 10)
            {
                return endOfDay ? parsed.Date.AddDays(1).AddTicks(-1) : parsed.Date;
            }
            return parsed;
        }

        return null;
    }

    private static string RecordDownload(LanDiskRuntime runtime, HttpContext context, ShareDefinition share, ResolvedTarget target)
    {
        RequestSession session = context.Session();
        var record = new TransferRecord
        {
            Direction = TransferDirection.Download,
            ShareId = share.Id,
            ShareName = share.DisplayName,
            RelativePath = target.RelativePath,
            FileName = target.ParentName,
            TotalBytes = target.Length,
            TransferredBytes = 0,
            State = TransferState.InProgress,
            DeviceId = session.Device?.Id ?? "local-admin",
            DeviceName = session.DisplayName
        };
        runtime.Transfers.Upsert(record);

        runtime.Audit.Write(new AuditEntry
        {
            Category = "download",
            Action = "serve",
            DeviceId = session.Device?.Id,
            DeviceName = session.DisplayName,
            ShareId = share.Id,
            RelativePath = target.RelativePath,
            Detail = $"bytes={target.Length}",
            Success = true
        });

        return record.Id;
    }

    /// <summary>
    /// Serves one download and guarantees the transfer record reaches a terminal state.
    ///
    /// <para><b>The defect this closes.</b> <see cref="RecordDownload"/> created a record in
    /// <c>InProgress</c> and nothing ever moved it: there was no progress update, no completion path and no
    /// handler for the abort. When the browser cancelled a download, <see cref="CopyRangeAsync"/> observed
    /// <c>RequestAborted</c> and threw <c>OperationCanceledException</c>, which simply unwound out of the
    /// endpoint — leaving the record at "0 B / 0 B, 进行中" forever, which is exactly what the Owner saw on the
    /// transfers page after cancelling <c>1111.txt</c>.</para>
    /// </summary>
    private static async Task<IResult> ServeDownloadAsync(
        LanDiskRuntime runtime, HttpContext context, string transferId, ResolvedTarget target, string fileName, bool attachment)
    {
        try
        {
            IResult result = await ServeAsync(context, target, fileName, attachment, allowInlineActive: false,
                throttle: runtime.DownloadThrottle);

            // 只有真的把正文发出去了才算完成。
            //
            // HEAD 探测、304 缓存校验、416 range 拒绝都不产生正文，而且**浏览器会自行发起** ——
            // 把这些做成"已取消"的记录，用户会在"传输"页看到一个自己从未发起过的下载被标成已取消。
            // 那比原来卡在"进行中"更难解释，所以这里直接**不留下用户可见记录**。
            bool bodyServed = context.Response.StatusCode is StatusCodes.Status200OK or StatusCodes.Status206PartialContent
                              && !HttpMethods.IsHead(context.Request.Method);
            if (bodyServed)
                runtime.Transfers.Terminalize(transferId, TransferState.Committed, target.Length, null);
            else
                runtime.Transfers.Remove(transferId);
            return result;
        }
        catch (OperationCanceledException)
        {
            // 浏览器主动取消 / 连接断开：这是 Cancelled，不是 InProgress，也不是 Failed。
            runtime.Transfers.Terminalize(transferId, TransferState.Cancelled, 0, "客户端取消了下载");
            return Results.Empty;
        }
        catch (IOException ex)
        {
            runtime.Transfers.Terminalize(transferId, TransferState.Failed, 0, "传输中断：" + ex.Message);
            return Results.Empty;
        }
        catch (Exception ex)
        {
            runtime.Transfers.Terminalize(transferId, TransferState.Failed, 0, ex.GetType().Name + ": " + ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Serves file bytes with correct HTTP range semantics. The stream is produced from the very
    /// handle that was validated by the resolver, so the bytes cannot come from a different object
    /// than the one the permission check approved.
    /// </summary>
    internal static async Task<IResult> ServeAsync(HttpContext context, ResolvedTarget target, string fileName, bool attachment, bool allowInlineActive,
        (int DelayMs, int ChunkBytes) throttle = default)
    {
        OpenedHandle opened = target.Opened!;
        long length = opened.Length;
        string etag = RangeHeader.BuildETag(length, opened.LastWriteTime);

        IHeaderDictionary headers = context.Response.Headers;
        headers.AcceptRanges = "bytes";
        headers.ETag = etag;
        headers.LastModified = RangeHeader.HttpDate(opened.LastWriteTime);
        headers.ContentDisposition = MimeTypes.ContentDisposition(fileName, attachment);

        context.Response.ContentType = MimeTypes.ForFileName(fileName);

        string? ifNoneMatch = context.Request.Headers.IfNoneMatch.ToString();
        if (!string.IsNullOrEmpty(ifNoneMatch) && IfNoneMatchSatisfied(ifNoneMatch, etag))
        {
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            context.Response.ContentLength = null;
            return Results.Empty;
        }

        bool isHead = HttpMethods.IsHead(context.Request.Method);
        bool ifRangeOk = RangeHeader.IfRangeMatches(context.Request.Headers.IfRange.ToString(), etag, opened.LastWriteTime);
        RangeParseResult range = RangeHeader.Parse(context.Request.Headers.Range.ToString(), length);

        if (range.IsRangeRequest && !range.IsSatisfiable && !range.IsMultipleRanges)
        {
            context.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
            headers.ContentRange = $"bytes */{length}";
            context.Response.ContentLength = null;
            return Results.Empty;
        }

        if (range is { IsRangeRequest: true, IsSatisfiable: true, Range: not null } && ifRangeOk)
        {
            ByteRange r = range.Range.Value;
            context.Response.StatusCode = StatusCodes.Status206PartialContent;
            headers.ContentRange = $"bytes {r.Start}-{r.End}/{length}";
            context.Response.ContentLength = r.Length;

            if (isHead) return Results.Empty;

            await using FileStream stream = new(opened.Handle, FileAccess.Read);
            stream.Seek(r.Start, SeekOrigin.Begin);
            await CopyRangeAsync(stream, context.Response.Body, r.Length, context.RequestAborted, throttle);
            return Results.Empty;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentLength = length;

        if (isHead) return Results.Empty;

        await using FileStream full = new(opened.Handle, FileAccess.Read);
        await CopyRangeAsync(full, context.Response.Body, length, context.RequestAborted, throttle);
        return Results.Empty;
    }

    private static async Task CopyRangeAsync(FileStream source, Stream destination, long count, CancellationToken cancellationToken,
        (int DelayMs, int ChunkBytes) throttle = default)
    {
        byte[] buffer = new byte[StreamBufferSize];
        long remaining = count;
        int sinceDelay = 0;
        int chunk = throttle.ChunkBytes > 0 ? throttle.ChunkBytes : buffer.Length;

        while (remaining > 0)
        {
            int want = (int)Math.Min(buffer.Length, remaining);
            int read = await source.ReadAsync(buffer.AsMemory(0, want), cancellationToken);
            if (read <= 0) break;
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            remaining -= read;

            // 仅测试：把发送有意放慢，使「中止发生在服务端写完 response 之前」成为**必然**，
            // 而不是靠「客户端收到多少字节」去推断。生产 (0,0) 时这段完全不执行。
            if (throttle.DelayMs > 0)
            {
                sinceDelay += read;
                if (sinceDelay >= chunk)
                {
                    sinceDelay = 0;
                    await Task.Delay(throttle.DelayMs, cancellationToken);
                }
            }
        }
    }

    private static bool IfNoneMatchSatisfied(string header, string etag)
    {
        if (header.Trim() == "*") return true;
        return header.Split(',').Any(part => string.Equals(part.Trim(), etag, StringComparison.Ordinal));
    }

    /// <summary>A byte order mark is positive evidence of text, so the binary heuristic is skipped.</summary>
    private static bool HasTextBom(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE) return true; // UTF-16 LE
        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF) return true; // UTF-16 BE
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) return true; // UTF-8
        return false;
    }

    private static bool LooksBinary(ReadOnlySpan<byte> data)
    {
        int sample = Math.Min(data.Length, 8192);
        int control = 0;
        for (int i = 0; i < sample; i++)
        {
            byte b = data[i];
            if (b == 0) return true;
            if (b < 0x09 || (b > 0x0D && b < 0x20)) control++;
        }
        return sample > 0 && control * 100 / sample > 5;
    }

    private static string DecodeText(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
            return System.Text.Encoding.Unicode.GetString(data[2..]);
        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
            return System.Text.Encoding.BigEndianUnicode.GetString(data[2..]);
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
            return System.Text.Encoding.UTF8.GetString(data[3..]);
        return System.Text.Encoding.UTF8.GetString(data);
    }
}
