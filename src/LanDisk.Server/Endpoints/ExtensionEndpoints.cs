using System.Text.Json.Nodes;
using LanDisk.Core.Extensions;
using LanDisk.Core.Model;
using LanDisk.Server.Services;

namespace LanDisk.Server.Endpoints;

public static class ExtensionEndpoints
{
    /// <summary>
    /// The extension surface.
    ///
    /// <para><b>Upload</b> needs <see cref="DeviceCapability.ExtensionInstall"/>; <b>running a command</b> needs
    /// <see cref="DeviceCapability.ExtensionExecute"/>. Neither is implied by file access. A phone can only
    /// name an extension id, a command id and schema-valid arguments — never a path, a working directory or a
    /// command line, because the entrypoint is resolved from the locally approved manifest.</para>
    ///
    /// <para><b>Nothing here executes an extension in this process.</b> Execution is delegated to
    /// <c>LanDisk.ExtensionHost.exe</c>.</para>
    /// </summary>
    public static void MapExtensionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/extensions");

        group.MapPost("/upload", async (HttpContext context, LanDiskRuntime runtime) =>
        {
            RequestSession session = context.Session();
            if (!MayInstall(session)) return ApiHelpers.Fail(StatusCodes.Status403Forbidden, "EXTENSION_INSTALL_REQUIRED",
                "这台设备没有被授予上传扩展的权限。");

            string fileName = context.Request.Headers["X-LanDisk-FileName"].ToString();
            if (string.IsNullOrWhiteSpace(fileName)) fileName = "extension.ldx";
            fileName = Path.GetFileName(fileName);
            if (!fileName.EndsWith(".ldx", StringComparison.OrdinalIgnoreCase))
                return ApiHelpers.Fail(StatusCodes.Status400BadRequest, "NOT_LDX", "扩展包必须是 .ldx 文件。");

            using var buffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
            byte[] bytes = buffer.ToArray();
            if (bytes.Length == 0) return ApiHelpers.Fail(StatusCodes.Status400BadRequest, "EMPTY_PACKAGE", "扩展包为空。");

            OperationOutcome<ExtensionPackageRecord> uploaded = runtime.Extensions.Upload(bytes, fileName);
            if (!uploaded.Ok) return ApiHelpers.Fail(StatusCodes.Status400BadRequest, "PACKAGE_REJECTED", uploaded.Message ?? "扩展包被拒绝。");

            ExtensionPackageRecord record = uploaded.Value!;
            // Uploading never approves: the package is quarantined until the local operator approves it.
            runtime.Audit.Write(new AuditEntry
            {
                Category = "extension",
                Action = "upload",
                DeviceId = session.Device?.Id,
                DeviceName = session.DisplayName,
                Source = session.IsAdmin ? OperationActor.LocalAdmin.SourceToken : "device",
                Detail = $"extension={record.Id} version={record.Version} sha256={record.PackageSha256} state={record.State}",
                Success = true
            });

            return Results.Json(new
            {
                ok = true,
                extension = PackageDto(record),
                note = "已上传并隔离（PendingApproval）。必须由本机操作员在「扩展程序」页批准后才能运行，且批准会固定当前包的 SHA256。"
            }, ApiHelpers.Json);
        });

        group.MapGet("/commands", (HttpContext context, LanDiskRuntime runtime) =>
        {
            RequestSession session = context.Session();
            if (!MayExecute(session))
                return ApiHelpers.Fail(StatusCodes.Status403Forbidden, "EXTENSION_EXECUTE_REQUIRED", "这台设备没有被授予执行扩展命令的权限。");

            // A phone only sees approved extensions and the commands they declared.
            var approved = runtime.Extensions.Packages()
                .Where(p => ExtensionPackage.CanRun(p.State))
                .Select(p => new
                {
                    id = p.Id,
                    name = p.Name,
                    version = p.Version,
                    publisher = p.Publisher,
                    commands = p.Commands.Select(c => new { id = c.Id, name = c.Name, description = c.Description, argumentsSchema = c.ArgumentsSchema })
                })
                .ToList();

            return Results.Json(new { extensions = approved }, ApiHelpers.Json);
        });

        group.MapPost("/execute", async (HttpContext context, LanDiskRuntime runtime, ExtensionExecuteRequest request) =>
        {
            RequestSession session = context.Session();
            if (!MayExecute(session))
                return ApiHelpers.Fail(StatusCodes.Status403Forbidden, "EXTENSION_EXECUTE_REQUIRED", "这台设备没有被授予执行扩展命令的权限。");

            OperationOutcome<ExtensionTaskRecord> result = await runtime.Extensions.ExecuteAsync(
                request.ExtensionId ?? string.Empty, request.CommandId ?? string.Empty, request.Arguments,
                session.Device, context.RequestAborted);

            if (!result.Ok) return ApiHelpers.Fail(StatusCodes.Status400BadRequest, result.Code ?? "EXECUTE_REJECTED", result.Message ?? "无法执行该命令。");

            ExtensionTaskRecord task = result.Value!;
            runtime.Audit.Write(new AuditEntry
            {
                Category = "extension",
                Action = "execute",
                DeviceId = session.Device?.Id,
                DeviceName = session.DisplayName,
                Source = session.IsAdmin ? OperationActor.LocalAdmin.SourceToken : "device",
                Detail = $"task={task.Id} extension={task.ExtensionId} command={task.CommandId} state={task.State}",
                Success = task.State == ExtensionTaskState.Succeeded
            });

            return Results.Json(new { ok = true, task = TaskDto(task) }, ApiHelpers.Json);
        });

        group.MapGet("/tasks", (HttpContext context, LanDiskRuntime runtime) =>
        {
            RequestSession session = context.Session();
            if (session.Device is null && session.Admin is null)
                return ApiHelpers.Fail(StatusCodes.Status401Unauthorized, "NOT_AUTHENTICATED", "尚未配对。");

            // A device only ever sees its own tasks.
            IEnumerable<ExtensionTaskRecord> tasks = session.Admin is not null
                ? runtime.Extensions.Tasks()
                : runtime.Extensions.Tasks().Where(t => t.SourceDeviceId == session.Device!.Id);

            return Results.Json(new { tasks = tasks.Select(TaskDto) }, ApiHelpers.Json);
        });

        // ---- local administration ------------------------------------------

        var admin = app.MapGroup("/api/admin/extensions");

        admin.MapGet("/", (HttpContext context, LanDiskRuntime runtime) =>
        {
            if (!AdminEndpoints.RequireAdmin(context, runtime, out IResult? error)) return error!;
            return Results.Json(new
            {
                packages = runtime.Extensions.Packages().Select(PackageDto),
                tasks = runtime.Extensions.Tasks().Select(TaskDto)
            }, ApiHelpers.Json);
        });

        admin.MapPost("/{id}/approve", (HttpContext context, LanDiskRuntime runtime, string id, ExtensionApproveRequest request) =>
        {
            if (!AdminEndpoints.RequireAdmin(context, runtime, out IResult? error)) return error!;

            OperationOutcome<ExtensionPackageRecord> approved = runtime.Extensions.Approve(id, request.Sha256 ?? string.Empty);
            if (!approved.Ok) return ApiHelpers.Fail(StatusCodes.Status400BadRequest, approved.Code ?? "APPROVE_REJECTED", approved.Message ?? "无法批准。");

            runtime.Audit.Write(new AuditEntry
            {
                Category = "extension",
                Action = "approve",
                DeviceName = OperationActor.LocalAdminName,
                Source = OperationActor.LocalAdmin.SourceToken,
                Detail = $"extension={id} sha256={approved.Value!.PackageSha256}",
                Success = true
            });

            return Results.Json(new { ok = true, extension = PackageDto(approved.Value!) }, ApiHelpers.Json);
        });

        admin.MapPost("/{id}/enabled", (HttpContext context, LanDiskRuntime runtime, string id, ExtensionEnableRequest request) =>
        {
            if (!AdminEndpoints.RequireAdmin(context, runtime, out IResult? error)) return error!;

            OperationOutcome<ExtensionPackageRecord> updated = runtime.Extensions.SetEnabled(id, request.Enabled);
            if (!updated.Ok) return ApiHelpers.Fail(StatusCodes.Status404NotFound, "NOT_FOUND", updated.Message ?? "扩展不存在。");

            runtime.Audit.Write(new AuditEntry
            {
                Category = "extension",
                Action = "set-enabled",
                DeviceName = OperationActor.LocalAdminName,
                Source = OperationActor.LocalAdmin.SourceToken,
                Detail = $"extension={id} state={updated.Value!.State}",
                Success = true
            });

            return Results.Json(new { ok = true, extension = PackageDto(updated.Value!) }, ApiHelpers.Json);
        });

        admin.MapPost("/tasks/{taskId}/cancel", (HttpContext context, LanDiskRuntime runtime, string taskId) =>
        {
            if (!AdminEndpoints.RequireAdmin(context, runtime, out IResult? error)) return error!;

            OperationOutcome<bool> cancelled = runtime.Extensions.Cancel(taskId);
            runtime.Audit.Write(new AuditEntry
            {
                Category = "extension",
                Action = "cancel",
                DeviceName = OperationActor.LocalAdminName,
                Source = OperationActor.LocalAdmin.SourceToken,
                Detail = $"task={taskId} ok={cancelled.Ok}",
                Success = cancelled.Ok
            });

            return cancelled.Ok
                ? Results.Json(new { ok = true }, ApiHelpers.Json)
                : ApiHelpers.Fail(StatusCodes.Status400BadRequest, "CANCEL_REJECTED", cancelled.Message ?? "无法取消。");
        });
    }

    private static bool MayInstall(RequestSession session)
        => session.IsAdmin || (session.IsDevice && session.Permissions.InstallExtensions);

    private static bool MayExecute(RequestSession session)
        => session.IsAdmin || (session.IsDevice && session.Permissions.ExecuteExtensions);

    private static object PackageDto(ExtensionPackageRecord record) => new
    {
        id = record.Id,
        name = record.Name,
        version = record.Version,
        publisher = record.Publisher,
        entrypoint = Path.GetFileName(record.Entrypoint),
        entrypointPath = record.Entrypoint,
        sha256 = record.PackageSha256,
        bytes = record.PackageBytes,
        state = record.State.ToString(),
        approvedAt = record.ApprovedAt,
        approvedBy = record.ApprovedBy,
        uploadedAt = record.UploadedAt,
        timeoutSeconds = record.TimeoutSeconds,
        commands = record.Commands.Select(c => new { id = c.Id, name = c.Name, description = c.Description, argumentsSchema = c.ArgumentsSchema })
    };

    private static object TaskDto(ExtensionTaskRecord task) => new
    {
        id = task.Id,
        extensionId = task.ExtensionId,
        extensionName = task.ExtensionName,
        commandId = task.CommandId,
        arguments = task.ArgumentsSummary,
        deviceId = task.SourceDeviceId,
        deviceName = task.SourceDeviceName,
        state = task.State.ToString(),
        startedAt = task.StartedAt,
        finishedAt = task.FinishedAt,
        durationMs = task.FinishedAt is null ? (long?)null : (long)(task.FinishedAt.Value - task.StartedAt).TotalMilliseconds,
        exitCode = task.ExitCode,
        log = task.Log,
        error = task.Error
    };

    public sealed record ExtensionExecuteRequest(string? ExtensionId, string? CommandId, JsonNode? Arguments);
    public sealed record ExtensionApproveRequest(string? Sha256);
    public sealed record ExtensionEnableRequest(bool Enabled);
}
