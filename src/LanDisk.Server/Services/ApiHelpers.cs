using System.Text.Json;
using LanDisk.Core.Model;
using LanDisk.Core.Paths;
using LanDisk.Core.Storage;

namespace LanDisk.Server.Services;

public sealed record ApiError(string Code, string Message, object? Extra = null);

public static class ApiHelpers
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static RequestSession Session(this HttpContext context)
        => context.Items["session"] as RequestSession
           ?? new RequestSession { IsLoopback = false, RemoteAddress = "unknown" };

    public static IResult Fail(int status, string code, string message, object? extra = null)
        => Results.Json(new { error = new ApiError(code, message, extra) }, Json, statusCode: status);

    /// <summary>Maps domain exceptions onto stable HTTP status codes and machine readable error codes.</summary>
    public static IResult MapException(Exception ex, ILogger? logger = null)
    {
        switch (ex)
        {
            case PathSafetyException safety:
                return Fail(StatusCodes.Status400BadRequest, "PATH_" + safety.Reason.ToString().ToUpperInvariant(), safety.Message);

            case PathConflictException conflict:
                return Fail(StatusCodes.Status409Conflict, "CONFLICT", conflict.Message);

            case ShareUnavailableException unavailable:
                return Fail(StatusCodes.Status503ServiceUnavailable, "SHARE_" + unavailable.State.ToString().ToUpperInvariant(), unavailable.Message);

            case UploadOffsetMismatchException offset:
                return Fail(StatusCodes.Status409Conflict, "UPLOAD_OFFSET_MISMATCH", offset.Message,
                    new { expectedOffset = offset.Expected, actualOffset = offset.Actual });

            case FileNotFoundException notFound:
                return Fail(StatusCodes.Status404NotFound, "NOT_FOUND", "目标不存在或已被移动。");

            case DirectoryNotFoundException:
                return Fail(StatusCodes.Status404NotFound, "PATH_NOT_FOUND", "路径不存在。");

            case UnauthorizedAccessException:
                return Fail(StatusCodes.Status403Forbidden, "PERMISSION_DENIED", "Windows 拒绝了该操作，请检查文件或目录权限。");

            case InvalidOperationException invalid:
                return Fail(StatusCodes.Status409Conflict, "INVALID_STATE", invalid.Message);

            case ArgumentException argument:
                return Fail(StatusCodes.Status400BadRequest, "BAD_REQUEST", argument.Message);

            case KeyNotFoundException key:
                return Fail(StatusCodes.Status404NotFound, "NOT_FOUND", key.Message);

            case IOException io:
                logger?.LogWarning(io, "文件操作失败");
                return Fail(StatusCodes.Status409Conflict, "IO_ERROR", "文件系统操作失败：" + io.Message);

            default:
                logger?.LogError(ex, "未处理的服务端错误");
                return Fail(StatusCodes.Status500InternalServerError, "INTERNAL", "服务器内部错误，请查看本机日志。");
        }
    }

    /// <summary>Resolves a share and checks that the caller is allowed to perform the action.</summary>
    public static bool TryAuthorizeShare(
        HttpContext context,
        LanDiskRuntime runtime,
        string? shareId,
        ShareCapabilities required,
        out ShareDefinition share,
        out IResult? error)
    {
        share = null!;
        error = null;

        // Authentication is checked before anything about the requested share, so an
        // unauthenticated caller learns nothing about which shares exist.
        RequestSession session = context.Session();
        if (session.Device is null && session.Admin is null)
        {
            error = Fail(StatusCodes.Status401Unauthorized, "NOT_AUTHENTICATED", "尚未配对或会话已失效。");
            return false;
        }

        if (string.IsNullOrWhiteSpace(shareId))
        {
            error = Fail(StatusCodes.Status400BadRequest, "SHARE_REQUIRED", "缺少 shareId。");
            return false;
        }

        ShareDefinition? found = runtime.Shares.Find(shareId);
        if (found is null)
        {
            error = Fail(StatusCodes.Status404NotFound, "SHARE_NOT_FOUND", "共享不存在。");
            return false;
        }

        if (!found.Enabled)
        {
            error = Fail(StatusCodes.Status403Forbidden, "SHARE_DISABLED", "该共享已被本机关闭。");
            return false;
        }

        // A device sees the intersection of the share's flags and its own authority; the local
        // administrator is not narrowed by the device level. The authority is read from the live device
        // record on this request — never from what the session looked like when the phone paired — so
        // raising or lowering it on the Windows side is in force immediately, in both directions.
        ShareCapabilities effective = session.Device is not null
            ? LanDiskRuntime.EffectiveCapabilities(found, EffectiveLevel(session))
            : found.Capabilities;

        if ((effective & required) != required)
        {
            error = Fail(StatusCodes.Status403Forbidden, "PERMISSION_DENIED",
                "当前设备或共享未授予该操作的权限。");
            return false;
        }

        share = found;
        return true;
    }

    /// <summary>
    /// The file-access level to judge this request by.
    ///
    /// <para>The legacy read-only / read-write switch is still the baseline, and the separate
    /// <see cref="DeviceCapability.FileWrite"/> capability can raise it: an operator who wants to keep a
    /// phone's level at "read only" but still let it upload to one purpose-built share can now say so
    /// without granting anything else.</para>
    /// </summary>
    public static DeviceAccessLevel EffectiveLevel(RequestSession session)
    {
        if (session.Admin is not null) return DeviceAccessLevel.ReadWrite;
        return session.Permissions.WriteFiles ? DeviceAccessLevel.ReadWrite : DeviceAccessLevel.ReadOnly;
    }

    public static void SetCsrfCookie(HttpContext context, string csrfToken)
    {
        context.Response.Cookies.Append(SessionKeys.CsrfCookie, csrfToken, new CookieOptions
        {
            HttpOnly = false,
            SameSite = SameSiteMode.Strict,
            Secure = context.Request.IsHttps,
            Path = "/",
            MaxAge = TimeSpan.FromDays(365)
        });
    }

    public static void SetSessionCookie(HttpContext context, string name, string value, TimeSpan lifetime)
    {
        context.Response.Cookies.Append(name, value, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = context.Request.IsHttps,
            Path = "/",
            MaxAge = lifetime,
            IsEssential = true
        });
    }
}
