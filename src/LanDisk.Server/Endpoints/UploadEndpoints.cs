using LanDisk.Core.Model;
using LanDisk.Core.Storage;
using LanDisk.Server.Services;

namespace LanDisk.Server.Endpoints;

public static class UploadEndpoints
{
    public sealed record InitUploadRequest(
        string? ShareId,
        string? Path,
        string? FileName,
        long? Length,
        string? Fingerprint);

    public sealed record ResumeUploadRequest(string? Fingerprint, string? PrefixSha256);
    public sealed record CompleteUploadRequest(string? Sha256, string? ConflictPolicy);

    public static void MapUploadEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/uploads");

        group.MapPost("/", (HttpContext context, LanDiskRuntime runtime, InitUploadRequest request) =>
        {
            try
            {
                if (!ApiHelpers.TryAuthorizeShare(context, runtime, request.ShareId, ShareCapabilities.Upload, out ShareDefinition share, out IResult? error))
                    return error!;
                if (context.Session().Device is null && context.Session().Admin is null)
                    return ApiHelpers.Fail(StatusCodes.Status401Unauthorized, "NOT_AUTHENTICATED", "尚未配对。");

                RequestSession session = context.Session();
                DeviceDefinition device = session.Device ?? LocalAdminDevice();

                UploadSession upload = runtime.Uploads.Init(
                    device,
                    share,
                    request.Path,
                    request.FileName ?? string.Empty,
                    request.Length ?? 0,
                    request.Fingerprint ?? string.Empty);

                runtime.Transfers.Upsert(new TransferRecord
                {
                    Id = upload.Id,
                    Direction = TransferDirection.Upload,
                    ShareId = share.Id,
                    ShareName = share.DisplayName,
                    RelativePath = string.IsNullOrEmpty(upload.TargetRelativePath) ? upload.FileName : upload.TargetRelativePath + "\\" + upload.FileName,
                    FileName = upload.FileName,
                    TotalBytes = upload.DeclaredLength,
                    TransferredBytes = 0,
                    State = TransferState.InProgress,
                    DeviceId = device.Id,
                    DeviceName = device.Name
                });

                return Results.Json(new
                {
                    uploadId = upload.Id,
                    chunkSize = runtime.Settings.MaxChunkBytes,
                    receivedBytes = 0,
                    declaredLength = upload.DeclaredLength,
                    expiresAt = upload.ExpiresAt,
                    orderedChunksRequired = true
                }, ApiHelpers.Json);
            }
            catch (Exception ex)
            {
                return ApiHelpers.MapException(ex);
            }
        });

        group.MapGet("/", (HttpContext context, LanDiskRuntime runtime) =>
        {
            RequestSession session = context.Session();
            if (session.Device is null && session.Admin is null)
                return ApiHelpers.Fail(StatusCodes.Status401Unauthorized, "NOT_AUTHENTICATED", "尚未配对。");

            string deviceId = session.Device?.Id ?? "local-admin";
            var list = runtime.Uploads.ListForDevice(deviceId).Select(Describe).ToList();
            return Results.Json(new { uploads = list }, ApiHelpers.Json);
        });

        group.MapPost("/{uploadId}/resume", (HttpContext context, LanDiskRuntime runtime, string uploadId, ResumeUploadRequest request) =>
        {
            try
            {
                RequestSession session = context.Session();
                string deviceId = session.Device?.Id ?? "local-admin";

                UploadSession upload = runtime.Uploads.Resume(uploadId, deviceId, request.Fingerprint ?? string.Empty, request.PrefixSha256);

                // Resuming is writing too: the same live re-check applies (see the chunk endpoint).
                IResult? lost = RevokedWhileUploading(context, runtime, upload);
                if (lost is not null) return lost;

                return Results.Json(new
                {
                    uploadId = upload.Id,
                    receivedBytes = upload.ReceivedBytes,
                    declaredLength = upload.DeclaredLength,
                    prefixHash = upload.RunningHashHex,
                    chunkSize = runtime.Settings.MaxChunkBytes
                }, ApiHelpers.Json);
            }
            catch (Exception ex)
            {
                return ApiHelpers.MapException(ex);
            }
        });

        // Chunks are written strictly in order. Out of order chunks are answered with 409 and the
        // offset the server actually expects, which is what makes resumption unambiguous.
        group.MapPut("/{uploadId}/chunk", async (HttpContext context, LanDiskRuntime runtime, string uploadId, long? offset) =>
        {
            try
            {
                RequestSession session = context.Session();
                if (session.Device is null && session.Admin is null)
                    return ApiHelpers.Fail(StatusCodes.Status401Unauthorized, "NOT_AUTHENTICATED", "尚未配对。");

                string deviceId = session.Device?.Id ?? "local-admin";
                UploadSession? upload = runtime.Uploads.Find(uploadId, deviceId);
                if (upload is null)
                    return ApiHelpers.Fail(StatusCodes.Status404NotFound, "UPLOAD_NOT_FOUND", "上传会话不存在。");

                // Authority is re-checked before every chunk, not just when the upload started: an operator
                // who takes write access away while a transfer is running must not have to watch it finish.
                IResult? lost = RevokedWhileUploading(context, runtime, upload);
                if (lost is not null) return lost;

                long contentLength = context.Request.ContentLength ?? -1;
                if (contentLength <= 0)
                    return ApiHelpers.Fail(StatusCodes.Status411LengthRequired, "LENGTH_REQUIRED", "分块请求必须带 Content-Length。");

                string? chunkHash = context.Request.Headers["X-Chunk-SHA256"].ToString();

                ChunkWriteResult result = await runtime.Uploads.AppendChunkAsync(
                    upload, offset ?? 0, context.Request.Body, contentLength,
                    string.IsNullOrWhiteSpace(chunkHash) ? null : chunkHash,
                    context.RequestAborted);

                runtime.Transfers.Upsert(new TransferRecord
                {
                    Id = upload.Id,
                    Direction = TransferDirection.Upload,
                    ShareId = upload.ShareId,
                    ShareName = runtime.Shares.Find(upload.ShareId)?.DisplayName ?? string.Empty,
                    RelativePath = string.IsNullOrEmpty(upload.TargetRelativePath) ? upload.FileName : upload.TargetRelativePath + "\\" + upload.FileName,
                    FileName = upload.FileName,
                    TotalBytes = upload.DeclaredLength,
                    TransferredBytes = result.ReceivedBytes,
                    State = TransferState.InProgress,
                    DeviceId = deviceId,
                    DeviceName = session.DisplayName
                });

                return Results.Json(new
                {
                    uploadId = upload.Id,
                    receivedBytes = result.ReceivedBytes,
                    declaredLength = result.DeclaredLength,
                    isComplete = result.IsComplete,
                    nextOffset = result.ReceivedBytes
                }, ApiHelpers.Json);
            }
            catch (Exception ex)
            {
                return ApiHelpers.MapException(ex);
            }
        });

        group.MapPost("/{uploadId}/complete", async (HttpContext context, LanDiskRuntime runtime, string uploadId, CompleteUploadRequest request) =>
        {
            try
            {
                RequestSession session = context.Session();
                string deviceId = session.Device?.Id ?? "local-admin";

                UploadSession? upload = runtime.Uploads.Find(uploadId, deviceId);
                if (upload is null)
                    return ApiHelpers.Fail(StatusCodes.Status404NotFound, "UPLOAD_NOT_FOUND", "上传会话不存在。");

                // Publishing the file is the last write of the transfer, so it is checked like the rest:
                // a downgrade that landed while the bytes were still streaming must not produce a file.
                IResult? lost = RevokedWhileUploading(context, runtime, upload);
                if (lost is not null) return lost;

                ShareDefinition? share = runtime.Shares.Find(upload.ShareId);
                if (share is null)
                    return ApiHelpers.Fail(StatusCodes.Status404NotFound, "SHARE_NOT_FOUND", "共享已不存在。");

                DeviceDefinition device = session.Device ?? LocalAdminDevice();
                string policy = string.Equals(request.ConflictPolicy, "rename", StringComparison.OrdinalIgnoreCase) ? "rename" : "fail";

                UploadSession completed = await runtime.Uploads.CompleteAsync(
                    share, device, upload, request.Sha256, policy, context.RequestAborted);

                runtime.InvalidateScans(share.Id);

                runtime.Transfers.Upsert(new TransferRecord
                {
                    Id = completed.Id,
                    Direction = TransferDirection.Upload,
                    ShareId = share.Id,
                    ShareName = share.DisplayName,
                    RelativePath = completed.FinalRelativePath ?? completed.FileName,
                    FileName = completed.FileName,
                    TotalBytes = completed.DeclaredLength,
                    TransferredBytes = completed.DeclaredLength,
                    State = TransferState.Committed,
                    DeviceId = device.Id,
                    DeviceName = device.Name
                });

                return Results.Json(new
                {
                    ok = true,
                    path = (completed.FinalRelativePath ?? string.Empty).Replace('\\', '/'),
                    name = System.IO.Path.GetFileName(completed.FinalRelativePath ?? completed.FileName),
                    bytes = completed.DeclaredLength,
                    sha256 = completed.RunningHashHex
                }, ApiHelpers.Json);
            }
            catch (Exception ex)
            {
                return ApiHelpers.MapException(ex);
            }
        });

        group.MapDelete("/{uploadId}", (HttpContext context, LanDiskRuntime runtime, string uploadId) =>
        {
            RequestSession session = context.Session();
            string deviceId = session.Device?.Id ?? "local-admin";
            UploadSession? upload = runtime.Uploads.Find(uploadId, deviceId);
            if (upload is null)
                return ApiHelpers.Fail(StatusCodes.Status404NotFound, "UPLOAD_NOT_FOUND", "上传会话不存在。");

            runtime.Uploads.Cancel(upload, "客户端取消");

            runtime.Transfers.Upsert(new TransferRecord
            {
                Id = upload.Id,
                Direction = TransferDirection.Upload,
                ShareId = upload.ShareId,
                ShareName = runtime.Shares.Find(upload.ShareId)?.DisplayName ?? string.Empty,
                RelativePath = upload.FileName,
                FileName = upload.FileName,
                TotalBytes = upload.DeclaredLength,
                TransferredBytes = upload.ReceivedBytes,
                State = TransferState.Cancelled,
                DeviceId = deviceId,
                DeviceName = session.DisplayName
            });

            return Results.Json(new { ok = true }, ApiHelpers.Json);
        });

        app.MapGet("/api/transfers", (HttpContext context, LanDiskRuntime runtime, int? limit) =>
        {
            RequestSession session = context.Session();
            if (session.Device is null && session.Admin is null)
                return ApiHelpers.Fail(StatusCodes.Status401Unauthorized, "NOT_AUTHENTICATED", "尚未配对。");

            int take = Math.Clamp(limit ?? 50, 1, 200);
            IReadOnlyList<TransferRecord> records = session.Admin is not null
                ? runtime.Transfers.Recent(take)
                : runtime.Transfers.ForDevice(session.Device!.Id, take);

            return Results.Json(new
            {
                transfers = records.Select(t => new
                {
                    id = t.Id,
                    direction = t.Direction.ToString(),
                    shareId = t.ShareId,
                    shareName = t.ShareName,
                    path = t.RelativePath.Replace('\\', '/'),
                    fileName = t.FileName,
                    totalBytes = t.TotalBytes,
                    transferredBytes = t.TransferredBytes,
                    state = t.State.ToString(),
                    deviceName = t.DeviceName,
                    startedAt = t.StartedAt,
                    updatedAt = t.UpdatedAt,
                    error = t.Error
                })
            }, ApiHelpers.Json);
        });
    }

    private static DeviceDefinition LocalAdminDevice() => new()
    {
        Id = "local-admin",
        Name = "本机管理端",
        Level = DeviceAccessLevel.ReadWrite
    };

    /// <summary>
    /// Re-checks write authority for a transfer that is already running.
    ///
    /// <para>Returns null when the upload may continue. When it may not, the session is cancelled first (so
    /// the staging bytes stop growing and nothing can be published later) and the caller answers 403 with a
    /// code the client can act on. Cancelling rather than merely refusing is deliberate: a permission
    /// downgrade has to end the transfer, not pause it until the next chunk asks again.</para>
    /// </summary>
    private static IResult? RevokedWhileUploading(HttpContext context, LanDiskRuntime runtime, UploadSession upload)
    {
        if (ApiHelpers.TryAuthorizeShare(context, runtime, upload.ShareId, ShareCapabilities.Upload, out _, out _))
            return null;

        runtime.Uploads.Cancel(upload, "上传期间设备权限被撤销或降低");
        return ApiHelpers.Fail(StatusCodes.Status403Forbidden, "UPLOAD_PERMISSION_REVOKED",
            "该设备的写入权限已在本机上被更改，正在进行的上传已取消。");
    }

    private static object Describe(UploadSession session) => new
    {
        uploadId = session.Id,
        shareId = session.ShareId,
        path = session.TargetRelativePath.Replace('\\', '/'),
        fileName = session.FileName,
        declaredLength = session.DeclaredLength,
        receivedBytes = session.ReceivedBytes,
        state = session.State.ToString(),
        createdAt = session.CreatedAt,
        expiresAt = session.ExpiresAt,
        fingerprint = session.ContentFingerprint,
        prefixHash = session.RunningHashHex
    };
}
