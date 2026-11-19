using LanDisk.Core.Configuration;
using LanDisk.Core.Model;
using LanDisk.Core.Paths;
using LanDisk.Core.Security;
using LanDisk.Server.Services;

namespace LanDisk.Server.Endpoints;

public static class AuthEndpoints
{
    public sealed record RedeemTokenRequest(string? Token, string? DeviceName);
    public sealed record RedeemCodeRequest(string? ShortCode, string? DeviceName);
    public sealed record PairStatusRequest(string? RequestId, string? ClaimSecret, string? DeviceName);

    public static void MapPairEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/pair");

        // Redeem a 256-bit one time token that the local operator handed over via QR code or link.
        group.MapPost("/redeem", async (HttpContext context, LanDiskRuntime runtime, RedeemTokenRequest request) =>
        {
            RequestSession session = context.Session();
            if (!session.IsLoopback)
            {
                // Remote (phone) side; rate limited by source address.
                if (!runtime.Pairing.TryAcquireTokenAttempt(session.RemoteAddress, out TimeSpan retry))
                    return ApiHelpers.Fail(StatusCodes.Status429TooManyRequests, "RATE_LIMITED",
                        $"尝试过于频繁，请在 {Math.Ceiling(retry.TotalSeconds)} 秒后重试。");
            }

            if (string.IsNullOrWhiteSpace(request.Token))
                return ApiHelpers.Fail(StatusCodes.Status400BadRequest, "TOKEN_REQUIRED", "缺少配对令牌。");

            PairingRequest? pairing = runtime.Pairing.FindByToken(request.Token.Trim());
            if (pairing is null)
                return ApiHelpers.Fail(StatusCodes.Status404NotFound, "PAIRING_NOT_FOUND", "配对请求不存在或已被使用。");

            if (pairing.IsExpired)
            {
                pairing.State = PairingState.Expired;
                return ApiHelpers.Fail(StatusCodes.Status410Gone, "PAIRING_EXPIRED", "配对请求已过期，请在本机重新生成。");
            }

            if (pairing.State != PairingState.Pending)
                return ApiHelpers.Fail(StatusCodes.Status409Conflict, "PAIRING_USED", "该配对请求已不再可用。");

            string deviceName = SanitizeDeviceName(request.DeviceName)
                                ?? DeriveDeviceName(context.Request.Headers.UserAgent.ToString());

            return await IssueDeviceAsync(context, runtime, pairing, deviceName);
        });

        // Six digit fallback: always requires an explicit click in the local management page.
        group.MapPost("/code", (HttpContext context, LanDiskRuntime runtime, RedeemCodeRequest request) =>
        {
            RequestSession session = context.Session();
            if (!runtime.Pairing.TryAcquireCodeAttempt(session.RemoteAddress, out TimeSpan retry))
                return ApiHelpers.Fail(StatusCodes.Status429TooManyRequests, "RATE_LIMITED",
                    $"尝试过于频繁，请在 {Math.Ceiling(retry.TotalSeconds)} 秒后重试。");

            if (string.IsNullOrWhiteSpace(request.ShortCode) || request.ShortCode.Trim().Length != 6)
                return ApiHelpers.Fail(StatusCodes.Status400BadRequest, "CODE_INVALID", "请输入 6 位配对码。");

            PairingRequest? pairing = runtime.Pairing.FindByShortCode(request.ShortCode.Trim());
            if (pairing is null || pairing.IsExpired)
                return ApiHelpers.Fail(StatusCodes.Status404NotFound, "PAIRING_NOT_FOUND", "配对码无效或已过期。");

            if (pairing.State != PairingState.Pending)
                return ApiHelpers.Fail(StatusCodes.Status409Conflict, "PAIRING_USED", "该配对请求已不再可用。");

            pairing.State = PairingState.AwaitingLocalConfirmation;
            pairing.DeviceName = SanitizeDeviceName(request.DeviceName) ?? DeriveDeviceName(context.Request.Headers.UserAgent.ToString());
            pairing.DeviceUserAgent = context.Request.Headers.UserAgent.ToString();
            pairing.RemoteAddress = session.RemoteAddress;
            pairing.ClaimSecret = SecretTokens.NewToken();

            return Results.Json(new
            {
                status = "awaiting_local_confirmation",
                requestId = pairing.Id,
                claimSecret = pairing.ClaimSecret,
                deviceName = pairing.DeviceName,
                expiresAt = pairing.ExpiresAt
            }, ApiHelpers.Json);
        });

        // The phone polls until the local operator confirms.
        group.MapPost("/status", (HttpContext context, LanDiskRuntime runtime, PairStatusRequest request) =>
        {
            if (string.IsNullOrWhiteSpace(request.RequestId) || string.IsNullOrWhiteSpace(request.ClaimSecret))
                return ApiHelpers.Fail(StatusCodes.Status400BadRequest, "REQUEST_INVALID", "缺少请求参数。");

            PairingRequest? pairing = runtime.Pairing.Find(request.RequestId);
            if (pairing is null)
                return ApiHelpers.Fail(StatusCodes.Status404NotFound, "PAIRING_NOT_FOUND", "配对请求不存在。");

            if (pairing.ClaimSecret is null || !SecretTokens.FixedTimeEquals(pairing.ClaimSecret, request.ClaimSecret))
                return ApiHelpers.Fail(StatusCodes.Status403Forbidden, "CLAIM_INVALID", "配对凭证不正确。");

            if (pairing.IsExpired && pairing.State is PairingState.Pending or PairingState.AwaitingLocalConfirmation)
            {
                pairing.State = PairingState.Expired;
            }

            if (pairing.State == PairingState.Redeemed && pairing.IssuedDeviceId is not null)
            {
                DeviceDefinition? device = runtime.Devices.Find(pairing.IssuedDeviceId);
                if (device is null || device.Revoked)
                    return ApiHelpers.Fail(StatusCodes.Status403Forbidden, "DEVICE_REVOKED", "该设备已被本机撤销。");

                string rawToken = SecretTokens.NewToken();
                runtime.Devices.Update(device.Id, d => d.TokenHash = SecretTokens.Hash(rawToken));
                ApiHelpers.SetSessionCookie(context, SessionKeys.DeviceCookie, rawToken, TimeSpan.FromDays(180));
                ApiHelpers.SetCsrfCookie(context, device.CsrfToken);

                return Results.Json(new
                {
                    status = "paired",
                    device = Describe(device)
                }, ApiHelpers.Json);
            }

            return Results.Json(new { status = pairing.State.ToString().ToLowerInvariant() }, ApiHelpers.Json);
        });

        // Device session management.
        app.MapGet("/api/me", (HttpContext context, LanDiskRuntime runtime) =>
        {
            RequestSession session = context.Session();
            if (session.Device is null && session.Admin is null)
                return ApiHelpers.Fail(StatusCodes.Status401Unauthorized, "NOT_AUTHENTICATED", "尚未配对。");

            if (session.Admin is not null)
            {
                return Results.Json(new
                {
                    kind = "admin",
                    isLoopback = session.IsLoopback,
                    csrfToken = session.Admin.CsrfToken,
                    shares = runtime.Shares.All().Select(AdminShareDto).ToList()
                }, ApiHelpers.Json);
            }

            DeviceDefinition device = session.Device!;
            ApiHelpers.SetCsrfCookie(context, device.CsrfToken);

            return Results.Json(new
            {
                kind = "device",
                isLoopback = session.IsLoopback,
                csrfToken = device.CsrfToken,
                device = Describe(device),
                // The current authority of this phone, recomputed from the live record: this is what the
                // mobile UI reads after a refresh, so a change made on the Windows side shows up here.
                permissions = Permissions(device, session.RemoteAddress),
                presence = Presence(device),
                shares = runtime.Shares.All().Where(s => s.Enabled).Select(s => DeviceShareDto(runtime, s, device)).ToList()
            }, ApiHelpers.Json);
        });

        // A deliberately tiny authenticated request the phone can repeat every few seconds: it proves the
        // session is still valid, keeps presence honest, and returns the authority + theme version the UI
        // needs to re-render itself. It carries no payload in either direction.
        app.MapPost("/api/heartbeat", (HttpContext context, LanDiskRuntime runtime) =>
        {
            RequestSession session = context.Session();
            if (session.Device is null && session.Admin is null)
                return ApiHelpers.Fail(StatusCodes.Status401Unauthorized, "NOT_AUTHENTICATED", "尚未配对。");

            if (session.Admin is not null)
            {
                return Results.Json(new { ok = true, kind = "admin", serverTime = DateTimeOffset.Now }, ApiHelpers.Json);
            }

            DeviceDefinition device = session.Device!;
            return Results.Json(new
            {
                ok = true,
                kind = "device",
                serverTime = DateTimeOffset.Now,
                device = Describe(device),
                permissions = Permissions(device, session.RemoteAddress),
                presence = Presence(device),
                // Lets the phone notice that the operator changed its appearance without polling anything
                // extra: it re-fetches the profile only when the number moves.
                themeRevision = runtime.MobileTheme.Revision,
                // Same idea for authority: this moves when the level *or* any capability changes, so the
                // phone re-reads /api/me exactly when something it can do has changed. Comparing a single
                // boolean field used to miss every other change and could not distinguish "unchanged" from
                // "already applied".
                permissionRevision = device.PermissionRevision
            }, ApiHelpers.Json);
        });

        // The appearance the operator chose for the phones. Readable by any authenticated caller (a phone
        // needs it to draw itself); it contains colour roles only — no CSS, no script, no secrets.
        app.MapGet("/api/mobile-theme", (HttpContext context, LanDiskRuntime runtime) =>
        {
            RequestSession session = context.Session();
            if (session.Device is null && session.Admin is null)
                return ApiHelpers.Fail(StatusCodes.Status401Unauthorized, "NOT_AUTHENTICATED", "尚未配对。");

            // Explicit, not inherited: the theme must never be served from Safari's cache, an old
            // localStorage copy or a service worker, because a stale profile is exactly how the Owner's phone
            // kept showing the previous appearance after the operator changed it.
            context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate, max-age=0";
            context.Response.Headers.Pragma = "no-cache";
            context.Response.Headers.Expires = "0";

            // A phone that prefers dark resolves a "follow the device" profile itself; the server sends the
            // profile as data so both appearances are available without another round trip.
            ThemeProfile dark = runtime.MobileTheme.Resolve(devicePrefersDark: true);
            ThemeProfile light = runtime.MobileTheme.Resolve(devicePrefersDark: false);
            bool followsDevice = runtime.MobileTheme.FollowsDevice;

            return Results.Json(new
            {
                revision = runtime.MobileTheme.Revision,
                profileId = runtime.MobileTheme.ProfileId,
                // True when the operator chose "follow the device", which is the only appearance decision the
                // phone is allowed to make for itself. Otherwise the server has already decided, and the two
                // halves below are identical.
                followsDevice,
                appearance = followsDevice ? dark.Appearance : dark.Appearance,
                dark = new { id = dark.Id, name = dark.Name, appearance = dark.Appearance, roles = dark.Roles },
                light = new { id = light.Id, name = light.Name, appearance = light.Appearance, roles = light.Roles }
            }, ApiHelpers.Json);
        });

        app.MapPost("/api/logout", (HttpContext context) =>
        {
            context.Response.Cookies.Delete(SessionKeys.DeviceCookie, new CookieOptions { Path = "/" });
            context.Response.Cookies.Delete(SessionKeys.AdminCookie, new CookieOptions { Path = "/" });
            return Results.Json(new { ok = true }, ApiHelpers.Json);
        });
    }

    internal static async Task<IResult> IssueDeviceAsync(HttpContext context, LanDiskRuntime runtime, PairingRequest pairing, string deviceName)
    {
        string rawToken = SecretTokens.NewToken();
        var device = new DeviceDefinition
        {
            Name = deviceName,
            Level = DeviceAccessLevel.ReadOnly, // every newly paired device starts read only
            TokenHash = SecretTokens.Hash(rawToken),
            LastSeenIp = context.Session().RemoteAddress,
            UserAgent = context.Request.Headers.UserAgent.ToString()
        };

        runtime.Devices.Add(device);
        runtime.Pairing.Complete(pairing, device.Id);

        ApiHelpers.SetSessionCookie(context, SessionKeys.DeviceCookie, rawToken, TimeSpan.FromDays(180));
        ApiHelpers.SetCsrfCookie(context, device.CsrfToken);

        runtime.Audit.Write(new AuditEntry
        {
            Category = "pairing",
            Action = "paired",
            DeviceId = device.Id,
            DeviceName = device.Name,
            Detail = "remote=" + context.Session().RemoteAddress,
            Success = true
        });

        await Task.CompletedTask;

        return Results.Json(new
        {
            status = "paired",
            device = Describe(device)
        }, ApiHelpers.Json);
    }

    internal static object Describe(DeviceDefinition device) => new
    {
        id = device.Id,
        name = device.Name,
        level = device.Level.ToString(),
        createdAt = device.CreatedAt,
        lastSeenAt = device.LastSeenAt,
        revoked = device.Revoked
    };

    internal static object DeviceShareDto(LanDiskRuntime runtime, ShareDefinition share, DeviceDefinition device)
    {
        ShareCapabilities effective = LanDiskRuntime.EffectiveCapabilities(share, device.Level);
        return new
        {
            id = share.Id,
            name = share.DisplayName,
            state = share.State.ToString(),
            stateDetail = share.StateDetail,
            capabilities = effective.ToString(),
            canRead = effective.HasFlag(ShareCapabilities.Read),
            canUpload = effective.HasFlag(ShareCapabilities.Upload),
            canRename = effective.HasFlag(ShareCapabilities.Rename),
            canDelete = effective.HasFlag(ShareCapabilities.Delete)
        };
    }
    internal static object AdminShareDto(ShareDefinition share) => new
    {
        id = share.Id,
        name = share.DisplayName,
        rootPath = share.RootPath,
        volumeGuidPath = share.VolumeGuidPath,
        mountPoint = share.MountPointAtCreation,
        capabilities = share.Capabilities.ToString(),
        enabled = share.Enabled,
        state = share.State.ToString(),
        stateDetail = share.StateDetail,
        createdAt = share.CreatedAt,
        note = share.Note
    };

    /// <summary>
    /// What the phone may currently do, as a flat object the mobile UI can read directly. Computed from the
    /// live device record on every call — never from something the session remembered at pairing time.
    /// </summary>
    internal static object Permissions(DeviceDefinition device, string remoteAddress)
    {
        DevicePermissions permissions = DevicePermissions.For(device);
        return new
        {
            writeFiles = permissions.WriteFiles,
            installExtensions = permissions.InstallExtensions,
            executeExtensions = permissions.ExecuteExtensions,
            viewSystem = permissions.ViewSystem,
            level = device.Level.ToString(),
            lastSeenIp = remoteAddress
        };
    }

    internal static object Presence(DeviceDefinition device)
    {
        DevicePresence presence = DeviceActivity.Classify(device, DateTimeOffset.Now);
        return new
        {
            state = DeviceActivity.ToWire(presence),
            lastSeenAt = device.LastSeenAt,
            lastSeenIp = device.LastSeenIp,
            onlineWindowSeconds = (int)DeviceActivity.OnlineWindow.TotalSeconds,
            recentlyActiveWindowSeconds = (int)DeviceActivity.RecentlyActiveWindow.TotalSeconds
        };
    }

    internal static string DeriveDeviceName(string userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent)) return "未命名设备";
        string ua = userAgent;
        if (ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase)) return "iPhone";
        if (ua.Contains("iPad", StringComparison.OrdinalIgnoreCase)) return "iPad";
        if (ua.Contains("Android", StringComparison.OrdinalIgnoreCase)) return "Android 设备";
        if (ua.Contains("Macintosh", StringComparison.OrdinalIgnoreCase)) return "Mac";
        if (ua.Contains("Windows", StringComparison.OrdinalIgnoreCase)) return "Windows 设备";
        return "未命名设备";
    }

    internal static string? SanitizeDeviceName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string trimmed = name.Trim();
        if (trimmed.Length > 64) trimmed = trimmed[..64];
        return new string(trimmed.Where(c => !char.IsControl(c)).ToArray());
    }
}
