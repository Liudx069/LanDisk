using System.Text.Json;
using LanDisk.Core.Media;
using LanDisk.Core.Model;
using LanDisk.Core.Paths;
using LanDisk.Core.State;
using LanDisk.Core.Storage;
using LanDisk.Server.Services;

namespace LanDisk.Server.Endpoints;

/// <summary>
/// The "library" surface added by the UX upgrade: quick views, favourites, thumbnails and batch
/// operations. Every one of them resolves paths through the same PathResolver as the core file
/// endpoints, so none of them is a weaker way in.
/// </summary>
public static class LibraryEndpoints
{
    public sealed record BatchRequest(string? ShareId, List<BatchItemRequest>? Items);
    public sealed record BatchItemRequest(string? Path, string? Version);
    public sealed record BatchZipRequest(string? ShareId, List<BatchItemRequest>? Items, string? ArchiveName, bool? PlanOnly);
    public sealed record FavoriteRequest(string? ShareId, string? Path);
    public sealed record PreferenceRequest(string? Sort, bool? Descending, string? View);

    public static void MapLibraryEndpoints(this WebApplication app)
    {
        app.MapGet("/api/recent", (HttpContext context, LanDiskRuntime runtime, string? shareId, int? limit, int? offset) =>
        {
            try
            {
                RequestSession session = context.Session();
                if (session.Device is null && session.Admin is null)
                    return ApiHelpers.Fail(StatusCodes.Status401Unauthorized, "NOT_AUTHENTICATED", "尚未配对。");

                DeviceAccessLevel level = session.Device?.Level ?? DeviceAccessLevel.ReadWrite;

                // Visible shares only: an administrator sees every enabled share, a device only
                // those it has been granted.
                List<ShareDefinition> shares = runtime.Shares.All()
                    .Where(s => s.Enabled)
                    .Where(s => session.Admin is not null || LanDiskRuntime.Allows(s, level, ShareCapabilities.Read))
                    .Where(s => string.IsNullOrEmpty(shareId) || s.Id == shareId)
                    .ToList();

                int take = Math.Clamp(limit ?? 50, 1, 200);
                int skip = Math.Max(0, offset ?? 0);
                DateTimeOffset since = DateTimeOffset.Now.AddDays(-Math.Max(1, runtime.Settings.RecentWindowDays));

                var collected = new List<(ShareDefinition Share, DirectoryEntryDto Entry)>();
                var truncatedReasons = new List<string>();
                var perShare = new List<object>();
                long totalScanMs = 0;

                int perShareCap = Math.Max(1, runtime.Settings.RecentMaxPerShare);
                int scanCap = Math.Max(1, runtime.Settings.RecentMaxScanEntries);

                foreach (ShareDefinition share in shares)
                {
                    if (share.State != ShareState.Online) continue;

                    string cacheKey = $"recent:{share.Id}:{share.VolumeGuidPath}:{share.RootObjectId}:{perShareCap}:{scanCap}";
                    try
                    {
                        // The whole (bounded) candidate set is collected, not a single fixed page.
                        // A page-sized fetch here used to cap how many candidates could ever be
                        // considered without saying so.
                        CollectedListing listing = runtime.Scans.GetOrAdd(cacheKey, () => runtime.Browser.Collect(
                            share, string.Empty, new BrowseQuery
                            {
                                Scope = BrowseScope.Share,
                                Sort = DirectorySortField.Modified,
                                Descending = true,
                                FilesOnly = true,
                                ModifiedAfter = since,
                                // Paging is not used on this path; ask for the whole collected set.
                                Limit = int.MaxValue,
                                MaxScan = scanCap,
                                MaxResults = perShareCap
                            }));

                        totalScanMs += listing.ScanMilliseconds;

                        if (listing.ScanLimitReached)
                            truncatedReasons.Add($"scan_limit:{share.DisplayName}");
                        if (listing.ResultLimitReached)
                            truncatedReasons.Add($"per_share_limit:{share.DisplayName}");

                        perShare.Add(new
                        {
                            shareId = share.Id,
                            name = share.DisplayName,
                            candidates = listing.Entries.Count,
                            scanned = listing.ScannedCount,
                            truncated = listing.Truncated
                        });

                        foreach (DirectoryEntryDto entry in listing.Entries)
                            collected.Add((share, entry));
                    }
                    catch (ShareUnavailableException)
                    {
                        // A share that went offline between the state check and the scan is simply
                        // left out; it is reported through the normal share state instead.
                    }
                }

                List<object> ordered = collected
                    .OrderByDescending(p => p.Entry.ModifiedAt)
                    .ThenBy(p => p.Entry.RelativePath, StringComparer.Ordinal)
                    .ThenBy(p => p.Share.Id, StringComparer.Ordinal)
                    .Skip(skip)
                    .Take(take)
                    .Select(p => (object)new
                    {
                        shareId = p.Share.Id,
                        shareName = p.Share.DisplayName,
                        shareState = p.Share.State.ToString(),
                        entry = FileEndpoints.EntryDto(p.Entry)
                    })
                    .ToList();

                return Results.Json(new
                {
                    windowDays = runtime.Settings.RecentWindowDays,
                    total = collected.Count,
                    offset = skip,
                    limit = take,
                    hasMore = skip + ordered.Count < collected.Count,
                    truncated = truncatedReasons.Count > 0,
                    truncatedReasons,
                    perShare,
                    scanMs = totalScanMs,
                    items = ordered
                }, ApiHelpers.Json);
            }
            catch (Exception ex)
            {
                return ApiHelpers.MapException(ex);
            }
        });

        // ---- favourites ---------------------------------------------------

        app.MapGet("/api/favorites", (HttpContext context, LanDiskRuntime runtime) =>
        {
            RequestSession session = context.Session();
            if (session.Device is null && session.Admin is null)
                return ApiHelpers.Fail(StatusCodes.Status401Unauthorized, "NOT_AUTHENTICATED", "尚未配对。");

            string owner = OwnerId(session);
            IReadOnlyList<FavoriteEntry> favorites = runtime.Favorites.ForOwner(owner);
            DeviceAccessLevel level = session.Device?.Level ?? DeviceAccessLevel.ReadWrite;

            var items = new List<object>();
            foreach (FavoriteEntry favorite in favorites)
            {
                ShareDefinition? share = runtime.Shares.Find(favorite.ShareId);
                string status;
                string? detail = null;

                if (share is null)
                {
                    status = "missing";
                    detail = "原共享已被删除。";
                }
                else if (!share.Enabled)
                {
                    status = "disabled";
                    detail = "该共享已被本机关闭。";
                }
                else if (!LanDiskRuntime.Allows(share, level, ShareCapabilities.Read))
                {
                    status = "denied";
                    detail = "当前设备没有该共享的读取权限。";
                }
                else
                {
                    try
                    {
                        using ResolvedTarget target = runtime.Resolver.Resolve(share, favorite.RelativePath, ResolveMode.ExistingOptional);
                        if (!target.Exists)
                        {
                            status = "missing";
                            detail = "目标已不存在。";
                        }
                        else if (target.Opened is null)
                        {
                            status = "unreadable";
                            detail = "目标当前无法打开（可能被占用）。";
                        }
                        else
                        {
                            status = "ok";
                        }
                    }
                    catch (ShareUnavailableException ex)
                    {
                        status = "offline";
                        detail = ex.Message;
                    }
                    catch (Exception ex)
                    {
                        status = "unreadable";
                        detail = ex.Message;
                    }
                }

                items.Add(new
                {
                    id = favorite.Id,
                    shareId = favorite.ShareId,
                    shareName = share?.DisplayName ?? "(已删除的共享)",
                    path = favorite.RelativePath.Replace('\\', '/'),
                    name = favorite.DisplayName,
                    isDirectory = favorite.IsDirectory,
                    addedAt = favorite.AddedAt,
                    status,
                    statusDetail = detail
                });
            }

            return Results.Json(new { items }, ApiHelpers.Json);
        });

        app.MapPost("/api/favorites", (HttpContext context, LanDiskRuntime runtime, FavoriteRequest request) =>
        {
            try
            {
                RequestSession session = context.Session();
                if (session.Device is null && session.Admin is null)
                    return ApiHelpers.Fail(StatusCodes.Status401Unauthorized, "NOT_AUTHENTICATED", "尚未配对。");

                if (!ApiHelpers.TryAuthorizeShare(context, runtime, request.ShareId, ShareCapabilities.Read, out ShareDefinition share, out IResult? error))
                    return error!;

                using ResolvedTarget target = runtime.Resolver.Resolve(share, request.Path, ResolveMode.Existing);

                string owner = OwnerId(session);
                FavoriteEntry? existing = runtime.Favorites.Find(owner, share.Id, target.RelativePath);
                if (existing is not null)
                    return Results.Json(new { ok = true, alreadyExists = true, id = existing.Id }, ApiHelpers.Json);

                var favorite = new FavoriteEntry
                {
                    OwnerId = owner,
                    ShareId = share.Id,
                    RelativePath = target.RelativePath,
                    DisplayName = target.ParentName,
                    IsDirectory = target.IsDirectory
                };
                runtime.Favorites.Add(favorite);

                return Results.Json(new { ok = true, id = favorite.Id }, ApiHelpers.Json);
            }
            catch (Exception ex)
            {
                return ApiHelpers.MapException(ex);
            }
        });

        app.MapDelete("/api/favorites/{id}", (HttpContext context, LanDiskRuntime runtime, string id) =>
        {
            RequestSession session = context.Session();
            if (session.Device is null && session.Admin is null)
                return ApiHelpers.Fail(StatusCodes.Status401Unauthorized, "NOT_AUTHENTICATED", "尚未配对。");

            FavoriteEntry? favorite = runtime.Favorites.FindById(id);
            if (favorite is null)
                return ApiHelpers.Fail(StatusCodes.Status404NotFound, "NOT_FOUND", "收藏不存在。");

            // A device may only remove its own bookmarks.
            if (!string.Equals(favorite.OwnerId, OwnerId(session), StringComparison.Ordinal) && session.Admin is null)
                return ApiHelpers.Fail(StatusCodes.Status403Forbidden, "PERMISSION_DENIED", "不能删除其他设备的收藏。");

            runtime.Favorites.Remove(id);
            return Results.Json(new { ok = true }, ApiHelpers.Json);
        });

        // ---- per principal UI preferences ---------------------------------

        app.MapGet("/api/preferences", (HttpContext context, LanDiskRuntime runtime) =>
        {
            RequestSession session = context.Session();
            if (session.Device is null && session.Admin is null)
                return ApiHelpers.Fail(StatusCodes.Status401Unauthorized, "NOT_AUTHENTICATED", "尚未配对。");

            return Results.Json(PreferenceDto(runtime.Preferences.Get(OwnerId(session))), ApiHelpers.Json);
        });

        app.MapMethods("/api/preferences", ["PATCH", "POST"], (HttpContext context, LanDiskRuntime runtime, PreferenceRequest request) =>
        {
            RequestSession session = context.Session();
            if (session.Device is null && session.Admin is null)
                return ApiHelpers.Fail(StatusCodes.Status401Unauthorized, "NOT_AUTHENTICATED", "尚未配对。");

            UiPreference current = runtime.Preferences.Get(OwnerId(session));
            var updated = new UiPreference
            {
                Sort = NormalizeSort(request.Sort) ?? current.Sort,
                Descending = request.Descending ?? current.Descending,
                View = NormalizeView(request.View) ?? current.View
            };

            runtime.Preferences.Set(OwnerId(session), updated);
            return Results.Json(PreferenceDto(updated), ApiHelpers.Json);
        });

        // ---- thumbnails ---------------------------------------------------

        app.MapGet("/api/file/thumb", (HttpContext context, LanDiskRuntime runtime, string? shareId, string? path) =>
        {
            try
            {
                if (!ApiHelpers.TryAuthorizeShare(context, runtime, shareId, ShareCapabilities.Read, out ShareDefinition share, out IResult? error))
                    return error!;

                using ResolvedTarget target = runtime.Resolver.Resolve(share, path, ResolveMode.Existing);
                ThumbnailOutcome outcome = runtime.Thumbnails.Get(target);

                if (outcome.Status != ThumbnailStatus.Ok || outcome.Result is null)
                {
                    return ApiHelpers.Fail(
                        outcome.Status == ThumbnailStatus.SourceTooLarge
                            ? StatusCodes.Status413PayloadTooLarge
                            : StatusCodes.Status415UnsupportedMediaType,
                        "THUMB_" + outcome.Status.ToString().ToUpperInvariant(),
                        outcome.Detail ?? "无法生成缩略图。");
                }

                context.Response.Headers.CacheControl = "private, max-age=300";
                context.Response.Headers.ETag = $"\"thumb-{target.Opened!.ObjectId}-{target.Opened.Length:x}\"";
                return Results.File(outcome.Result.Bytes, outcome.Result.ContentType);
            }
            catch (Exception ex)
            {
                return ApiHelpers.MapException(ex);
            }
        });

        // ---- batch --------------------------------------------------------

        app.MapPost("/api/batch/delete", (HttpContext context, LanDiskRuntime runtime, BatchRequest request) =>
        {
            try
            {
                if (!ApiHelpers.TryAuthorizeShare(context, runtime, request.ShareId, ShareCapabilities.Delete, out ShareDefinition share, out IResult? error))
                    return error!;

                List<BatchItemRequest> items = request.Items ?? [];
                if (items.Count == 0)
                    return ApiHelpers.Fail(StatusCodes.Status400BadRequest, "NO_ITEMS", "没有选择任何项目。");

                RequestSession session = context.Session();
                DeviceDefinition actor = session.Device ?? new DeviceDefinition { Id = "local-admin", Name = "本机管理端" };

                IReadOnlyList<BatchItemResult> results = runtime.Batch.Delete(
                    share,
                    actor,
                    items.Select(i => new BatchRequestItem(i.Path ?? string.Empty, i.Version)).ToList());

                runtime.InvalidateScans(share.Id);

                int succeeded = results.Count(r => r.Ok);

                return Results.Json(new
                {
                    ok = succeeded == results.Count,
                    requested = results.Count,
                    succeeded,
                    failed = results.Count - succeeded,
                    results = results.Select(r => new
                    {
                        path = r.Path.Replace('\\', '/'),
                        ok = r.Ok,
                        errorCode = r.ErrorCode,
                        error = r.Error,
                        recycleId = r.RecycleId,
                        isDirectory = r.IsDirectory
                    })
                }, ApiHelpers.Json);
            }
            catch (Exception ex)
            {
                return ApiHelpers.MapException(ex);
            }
        });

        // A dry run so the UI can show, per item, exactly what will and will not be included
        // before the user commits to a potentially large archive.
        app.MapPost("/api/batch/plan", (HttpContext context, LanDiskRuntime runtime, BatchZipRequest request) =>
        {
            try
            {
                if (!ApiHelpers.TryAuthorizeShare(context, runtime, request.ShareId, ShareCapabilities.Read, out ShareDefinition share, out IResult? error))
                    return error!;

                List<BatchItemRequest> items = request.Items ?? [];
                if (items.Count == 0)
                    return ApiHelpers.Fail(StatusCodes.Status400BadRequest, "NO_ITEMS", "没有选择任何项目。");

                BatchZipPlan plan = runtime.Batch.Plan(
                    share,
                    items.Select(i => new BatchRequestItem(i.Path ?? string.Empty, i.Version)).ToList(),
                    request.ArchiveName);

                return Results.Json(new
                {
                    archiveName = plan.SuggestedName,
                    fileCount = plan.Entries.Count,
                    totalBytes = plan.TotalBytes,
                    totalSizeText = PathRules.FormatSize(plan.TotalBytes),
                    truncated = plan.Truncated,
                    truncationReason = plan.TruncationReason,
                    skipped = plan.Skipped.Select(s => new
                    {
                        path = s.Path.Replace('\\', '/'),
                        errorCode = s.ErrorCode,
                        error = s.Error
                    })
                }, ApiHelpers.Json);
            }
            catch (Exception ex)
            {
                return ApiHelpers.MapException(ex);
            }
        });

        app.MapPost("/api/batch/download", async (HttpContext context, LanDiskRuntime runtime, ILoggerFactory loggerFactory, BatchZipRequest request) =>
        {
            ILogger logger = loggerFactory.CreateLogger("LanDisk.Batch");
            try
            {
                if (!ApiHelpers.TryAuthorizeShare(context, runtime, request.ShareId, ShareCapabilities.Read, out ShareDefinition share, out IResult? error))
                    return error!;

                List<BatchItemRequest> items = request.Items ?? [];
                if (items.Count == 0)
                    return ApiHelpers.Fail(StatusCodes.Status400BadRequest, "NO_ITEMS", "没有选择任何项目。");

                BatchZipPlan plan = runtime.Batch.Plan(
                    share,
                    items.Select(i => new BatchRequestItem(i.Path ?? string.Empty, i.Version)).ToList(),
                    request.ArchiveName);

                if (plan.Entries.Count == 0)
                {
                    return ApiHelpers.Fail(StatusCodes.Status409Conflict, "NOTHING_TO_ARCHIVE",
                        plan.Skipped.Count > 0 ? "选中的项目都不可读取，未生成归档。" : "没有可打包的文件。");
                }

                RequestSession session = context.Session();

                // Same revalidation as the ticketed path: this route plans and streams in one
                // request, but the entries are still re-resolved per entry while streaming.
                BatchRevalidation revalidation = runtime.Batch.RevalidateAll(share, plan);
                if (!revalidation.Ok)
                {
                    return Results.Json(new
                    {
                        error = new
                        {
                            code = BatchService.ChangedSinceSelection,
                            message = "选择的项目在下载开始前发生了变化，本次归档已取消。请刷新后重新选择。",
                            extra = new
                            {
                                changedCount = revalidation.Changed.Count,
                                totalCount = plan.Entries.Count,
                                items = revalidation.Changed.Select(c => new { path = c.Path.Replace('\\', '/'), errorCode = c.ErrorCode, error = c.Error })
                            }
                        }
                    }, ApiHelpers.Json, statusCode: StatusCodes.Status409Conflict);
                }

                runtime.Audit.Write(new AuditEntry
                {
                    Category = "download",
                    Action = "batch-zip",
                    DeviceId = session.Device?.Id,
                    DeviceName = session.DisplayName,
                    ShareId = share.Id,
                    Detail = $"files={plan.Entries.Count} bytes={plan.TotalBytes} skipped={plan.Skipped.Count} revalidated=yes",
                    Success = true
                });

                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = "application/zip";
                context.Response.Headers.ContentDisposition = MimeTypes.ContentDisposition(plan.SuggestedName, attachment: true);
                context.Response.Headers["X-LanDisk-Archive-Files"] = plan.Entries.Count.ToString();
                context.Response.Headers["X-LanDisk-Archive-Skipped"] = plan.Skipped.Count.ToString();
                context.Response.Headers["X-LanDisk-Archive-Bytes"] = plan.TotalBytes.ToString();
                if (plan.Truncated) context.Response.Headers["X-LanDisk-Archive-Truncated"] = "1";

                // ZipArchive emits small synchronous writes for entry headers and for the central
                // directory when it is disposed. Kestrel disallows synchronous IO by default, so the
                // allowance is enabled for this single request only rather than globally; the file
                // payload itself is still copied with asynchronous reads and writes.
                var bodyControl = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpBodyControlFeature>();
                bool previousAllowSync = bodyControl?.AllowSynchronousIO ?? false;
                if (bodyControl is not null) bodyControl.AllowSynchronousIO = true;

                try
                {
                    BatchZipStreamReport report = await runtime.Batch.WriteAsync(context.Response.Body, share, plan, context.RequestAborted);

                    logger.LogInformation("批量归档完成：写入 {Written} 项，传输期失败 {Failed} 项，跳过 {Skipped} 项",
                        report.WrittenEntries, report.FailedDuringTransfer.Count, report.Skipped.Count);
                }
                finally
                {
                    if (bodyControl is not null) bodyControl.AllowSynchronousIO = previousAllowSync;
                }

                return Results.Empty;
            }
            catch (OperationCanceledException)
            {
                // The client went away mid-archive; nothing to report and nothing to clean up.
                return Results.Empty;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "批量归档失败");

                // Once the archive has started streaming the response cannot be replaced by a JSON
                // error, so the failure is reported through the log instead of corrupting the stream.
                if (context.Response.HasStarted) return Results.Empty;
                return ApiHelpers.MapException(ex);
            }
        });

        // Issues a short lived, single use ticket for a prepared archive. The browser then performs
        // a plain GET, which lets the ZIP stream straight to disk instead of being buffered in
        // JavaScript memory. The ticket is bound to the requesting device as well as being random.
        app.MapPost("/api/batch/ticket", (HttpContext context, LanDiskRuntime runtime, BatchZipRequest request) =>
        {
            try
            {
                if (!ApiHelpers.TryAuthorizeShare(context, runtime, request.ShareId, ShareCapabilities.Read, out ShareDefinition share, out IResult? error))
                    return error!;

                List<BatchItemRequest> items = request.Items ?? [];
                if (items.Count == 0)
                    return ApiHelpers.Fail(StatusCodes.Status400BadRequest, "NO_ITEMS", "没有选择任何项目。");

                BatchZipPlan plan = runtime.Batch.Plan(
                    share,
                    items.Select(i => new BatchRequestItem(i.Path ?? string.Empty, i.Version)).ToList(),
                    request.ArchiveName);

                if (plan.Entries.Count == 0)
                {
                    return ApiHelpers.Fail(StatusCodes.Status409Conflict, "NOTHING_TO_ARCHIVE",
                        plan.Skipped.Count > 0 ? "选中的项目都不可读取，未生成归档。" : "没有可打包的文件。");
                }

                RequestSession session = context.Session();
                BatchTicket ticket = runtime.BatchTickets.Issue(LibraryEndpoints.OwnerId(session), share.Id, plan.SuggestedName, plan);

                return Results.Json(new
                {
                    ticket = ticket.Id,
                    url = "/api/batch/download?ticket=" + Uri.EscapeDataString(ticket.Id),
                    archiveName = plan.SuggestedName,
                    fileCount = plan.Entries.Count,
                    totalBytes = plan.TotalBytes,
                    totalSizeText = PathRules.FormatSize(plan.TotalBytes),
                    expiresAt = ticket.ExpiresAt,
                    truncated = plan.Truncated,
                    truncationReason = plan.TruncationReason,
                    skipped = plan.Skipped.Select(s => new
                    {
                        path = s.Path.Replace('\\', '/'),
                        errorCode = s.ErrorCode,
                        error = s.Error
                    })
                }, ApiHelpers.Json);
            }
            catch (Exception ex)
            {
                return ApiHelpers.MapException(ex);
            }
        });

        // Streaming download of a previously prepared archive.
        app.MapGet("/api/batch/download", async (HttpContext context, LanDiskRuntime runtime, ILoggerFactory loggerFactory, string? ticket) =>
        {
            ILogger logger = loggerFactory.CreateLogger("LanDisk.Batch");
            try
            {
                RequestSession session = context.Session();
                if (session.Device is null && session.Admin is null)
                    return ApiHelpers.Fail(StatusCodes.Status401Unauthorized, "NOT_AUTHENTICATED", "尚未配对。");

                BatchTicket? prepared = runtime.BatchTickets.Take(ticket ?? string.Empty, OwnerId(session));
                if (prepared is null)
                    return ApiHelpers.Fail(StatusCodes.Status404NotFound, "TICKET_INVALID",
                        "下载凭证不存在、已过期或已被使用。请重新选择要下载的项目。");

                // Re-authorise with the same rules as any other file endpoint: the share may have
                // been disabled, deleted, or the device's permission revoked since the ticket was
                // issued, and none of that may be skipped just because a ticket exists.
                if (!ApiHelpers.TryAuthorizeShare(context, runtime, prepared.ShareId, ShareCapabilities.Read,
                        out ShareDefinition share, out IResult? authError))
                {
                    return authError!;
                }

                BatchZipPlan plan = prepared.Plan;

                // The ticket stores share + relative path + object identity, never a trusted absolute
                // path. Everything is resolved again here, before a single byte is streamed, so a
                // swapped disk, replaced file or newly introduced junction fails loudly instead of
                // silently archiving a different object.
                BatchRevalidation revalidation = runtime.Batch.RevalidateAll(share, plan);
                if (!revalidation.Ok)
                {
                    runtime.Audit.Write(new AuditEntry
                    {
                        Category = "download",
                        Action = "batch-zip",
                        DeviceId = session.Device?.Id,
                        DeviceName = session.DisplayName,
                        ShareId = share.Id,
                        Detail = $"changed={revalidation.Changed.Count} of {plan.Entries.Count}",
                        Success = false
                    });

                    return Results.Json(new
                    {
                        error = new
                        {
                            code = BatchService.ChangedSinceSelection,
                            message = "选择的项目在下载开始前发生了变化，为避免下载到不同的内容，本次归档已取消。请刷新后重新选择。",
                            extra = new
                            {
                                changedCount = revalidation.Changed.Count,
                                totalCount = plan.Entries.Count,
                                items = revalidation.Changed.Select(c => new
                                {
                                    path = c.Path.Replace('\\', '/'),
                                    errorCode = c.ErrorCode,
                                    error = c.Error
                                })
                            }
                        }
                    }, ApiHelpers.Json, statusCode: StatusCodes.Status409Conflict);
                }

                runtime.Audit.Write(new AuditEntry
                {
                    Category = "download",
                    Action = "batch-zip",
                    DeviceId = session.Device?.Id,
                    DeviceName = session.DisplayName,
                    ShareId = share.Id,
                    Detail = $"files={plan.Entries.Count} bytes={plan.TotalBytes} skipped={plan.Skipped.Count} ticket=yes revalidated=yes",
                    Success = true
                });

                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = "application/zip";
                context.Response.Headers.ContentDisposition = MimeTypes.ContentDisposition(prepared.ArchiveName, attachment: true);
                context.Response.Headers["X-LanDisk-Archive-Files"] = plan.Entries.Count.ToString();
                context.Response.Headers["X-LanDisk-Archive-Skipped"] = plan.Skipped.Count.ToString();
                context.Response.Headers["X-LanDisk-Archive-Bytes"] = plan.TotalBytes.ToString();
                if (plan.Truncated) context.Response.Headers["X-LanDisk-Archive-Truncated"] = "1";

                var bodyControl = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpBodyControlFeature>();
                bool previousAllowSync = bodyControl?.AllowSynchronousIO ?? false;
                if (bodyControl is not null) bodyControl.AllowSynchronousIO = true;

                try
                {
                    BatchZipStreamReport report = await runtime.Batch.WriteAsync(context.Response.Body, share, plan, context.RequestAborted);
                    logger.LogInformation("批量归档完成：写入 {Written} 项，传输期失败 {Failed} 项，跳过 {Skipped} 项",
                        report.WrittenEntries, report.FailedDuringTransfer.Count, report.Skipped.Count);
                }
                finally
                {
                    if (bodyControl is not null) bodyControl.AllowSynchronousIO = previousAllowSync;
                }

                return Results.Empty;
            }
            catch (OperationCanceledException)
            {
                return Results.Empty;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "批量归档失败");
                if (context.Response.HasStarted) return Results.Empty;
                return ApiHelpers.MapException(ex);
            }
        });

        // ---- storage information ------------------------------------------

        app.MapGet("/api/storage", (HttpContext context, LanDiskRuntime runtime, string? shareId) =>
        {
            try
            {
                RequestSession session = context.Session();
                if (session.Device is null && session.Admin is null)
                    return ApiHelpers.Fail(StatusCodes.Status401Unauthorized, "NOT_AUTHENTICATED", "尚未配对。");

                DeviceAccessLevel level = session.Device?.Level ?? DeviceAccessLevel.ReadWrite;
                IEnumerable<ShareDefinition> shares = runtime.Shares.All()
                    .Where(s => session.Admin is not null || (s.Enabled && LanDiskRuntime.Allows(s, level, ShareCapabilities.Read)))
                    .Where(s => string.IsNullOrEmpty(shareId) || s.Id == shareId);

                var items = new List<object>();
                foreach (ShareDefinition share in shares)
                {
                    long total = 0, free = 0;
                    try
                    {
                        var drive = new DriveInfo(Path.GetPathRoot(share.RootPath) ?? share.RootPath);
                        if (drive.IsReady)
                        {
                            total = drive.TotalSize;
                            free = drive.AvailableFreeSpace;
                        }
                    }
                    catch { }

                    items.Add(new
                    {
                        shareId = share.Id,
                        name = share.DisplayName,
                        state = share.State.ToString(),
                        stateDetail = share.StateDetail,
                        totalBytes = total,
                        freeBytes = free,
                        usedBytes = Math.Max(0, total - free),
                        totalText = PathRules.FormatSize(total),
                        freeText = PathRules.FormatSize(free)
                    });
                }

                return Results.Json(new { items }, ApiHelpers.Json);
            }
            catch (Exception ex)
            {
                return ApiHelpers.MapException(ex);
            }
        });
    }

    /// <summary>
    /// Stable identity used for favourites and preferences. The local administrator is a single
    /// principal; a paired device is identified by its device id.
    /// </summary>
    internal static string OwnerId(RequestSession session)
        => session.Device?.Id ?? "local-admin";

    private static object PreferenceDto(UiPreference preference) => new
    {
        sort = preference.Sort,
        descending = preference.Descending,
        view = preference.View
    };

    private static string? NormalizeSort(string? sort)
    {
        if (string.IsNullOrWhiteSpace(sort)) return null;
        return sort.Trim().ToLowerInvariant() switch
        {
            "name" => "Name",
            "size" => "Size",
            "modified" or "time" => "Modified",
            "type" => "Type",
            _ => null
        };
    }

    private static string? NormalizeView(string? view)
    {
        if (string.IsNullOrWhiteSpace(view)) return null;
        return view.Trim().ToLowerInvariant() switch
        {
            "list" => "list",
            "grid" => "grid",
            _ => null
        };
    }
}
