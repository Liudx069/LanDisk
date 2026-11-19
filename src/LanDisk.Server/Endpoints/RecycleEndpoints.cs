using LanDisk.Core.Model;
using LanDisk.Core.Paths;
using LanDisk.Server.Services;

namespace LanDisk.Server.Endpoints;

public static class RecycleEndpoints
{
    public sealed record RestoreRequest(string? AlternateName);

    public static void MapRecycleEndpoints(this WebApplication app)
    {
        app.MapGet("/api/recycle", (HttpContext context, LanDiskRuntime runtime) =>
        {
            RequestSession session = context.Session();
            if (session.Device is null && session.Admin is null)
                return ApiHelpers.Fail(StatusCodes.Status401Unauthorized, "NOT_AUTHENTICATED", "尚未配对。");

            IReadOnlyList<RecycleEntry> entries;
            if (session.Admin is not null)
            {
                entries = runtime.Recycle.List();
            }
            else
            {
                DeviceDefinition device = session.Device!;
                // A phone only sees objects that were deleted from shares it may currently
                // delete in, so a revoked permission also hides the ability to restore.
                entries = runtime.Recycle.List(entry =>
                {
                    ShareDefinition? share = runtime.Shares.Find(entry.ShareId);
                    return share is not null && LanDiskRuntime.Allows(share, device.Level, ShareCapabilities.Delete);
                });
            }

            return Results.Json(new
            {
                entries = entries.Select(e => new
                {
                    id = e.Id,
                    shareId = e.ShareId,
                    shareName = runtime.Shares.Find(e.ShareId)?.DisplayName ?? "(已删除的共享)",
                    path = e.OriginalRelativePath.Replace('\\', '/'),
                    name = e.DisplayName,
                    isDirectory = e.IsDirectory,
                    length = e.Length,
                    sizeText = PathRules.FormatSize(e.Length),
                    deletedAt = e.DeletedAt,
                    deletedBy = e.DeletedByDeviceName,
                    state = e.State.ToString()
                })
            }, ApiHelpers.Json);
        });

        app.MapPost("/api/recycle/{id}/restore", (HttpContext context, LanDiskRuntime runtime, string id, RestoreRequest? request) =>
        {
            try
            {
                RequestSession session = context.Session();
                if (session.Device is null && session.Admin is null)
                    return ApiHelpers.Fail(StatusCodes.Status401Unauthorized, "NOT_AUTHENTICATED", "尚未配对。");

                RecycleEntry? entry = runtime.Recycle.Find(id);
                if (entry is null)
                    return ApiHelpers.Fail(StatusCodes.Status404NotFound, "NOT_FOUND", "回收站条目不存在。");

                ShareDefinition? share = runtime.Shares.Find(entry.ShareId);
                if (share is null)
                    return ApiHelpers.Fail(StatusCodes.Status404NotFound, "SHARE_NOT_FOUND", "原共享已被删除，无法恢复。");

                DeviceAccessLevel level = session.Device?.Level ?? DeviceAccessLevel.ReadWrite;
                if (!LanDiskRuntime.Allows(share, level, ShareCapabilities.Delete))
                    return ApiHelpers.Fail(StatusCodes.Status403Forbidden, "PERMISSION_DENIED", "当前设备没有该共享的删除/恢复权限。");

                // The actor is explicit: the loopback administrator is not a phone and must not be
                // recorded as one, and a restore on a machine with no paired device must still work.
                OperationActor actor = session.Device is null ? OperationActor.LocalAdmin : OperationActor.FromDevice(session.Device);
                RecycleEntry restored = runtime.Recycle.Restore(id, share, actor, request?.AlternateName);
                runtime.InvalidateScans(share.Id);

                string target = restored.OriginalRelativePath;
                if (!string.IsNullOrEmpty(request?.AlternateName))
                {
                    string parent = System.IO.Path.GetDirectoryName(restored.OriginalRelativePath)?.Replace('/', '\\') ?? string.Empty;
                    target = parent.Length == 0 ? request.AlternateName! : parent + "\\" + request.AlternateName;
                }

                return Results.Json(new
                {
                    ok = true,
                    path = target.Replace('\\', '/'),
                    name = System.IO.Path.GetFileName(target)
                }, ApiHelpers.Json);
            }
            catch (Exception ex)
            {
                return ApiHelpers.MapException(ex);
            }
        });
    }
}
