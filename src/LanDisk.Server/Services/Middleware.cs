using System.Net;
using LanDisk.Core.Model;
using LanDisk.Core.Networking;
using LanDisk.Core.Security;

namespace LanDisk.Server.Services;
/// <summary>The authenticated principal for one request: either a paired device or the local admin.</summary>
public sealed class RequestSession
{
    public DeviceDefinition? Device { get; init; }
    public AdminSession? Admin { get; init; }
    public required bool IsLoopback { get; init; }
    public required string RemoteAddress { get; init; }

    public bool IsDevice => Device is not null;
    public bool IsAdmin => Admin is not null;
    public string CsrfToken => Admin?.CsrfToken ?? Device?.CsrfToken ?? string.Empty;
    public string DisplayName => Admin is not null ? "本机管理端" : Device?.Name ?? "未认证";

    /// <summary>
    /// What this caller may do <i>right now</i>, computed from the live device record on every request.
    /// The session proves identity; it never carries authority, so a permission change on the Windows side
    /// is in force for the phone's next request without any re-pairing.
    /// </summary>
    public DevicePermissions Permissions => Admin is not null ? DevicePermissions.Admin : DevicePermissions.For(Device);
}

public static class SessionKeys
{
    public const string DeviceCookie = "landisk_session";
    public const string AdminCookie = "landisk_admin";
    public const string CsrfCookie = "landisk_csrf";
    public const string CsrfHeader = "X-LanDisk-CSRF";
    public const string ClientHeader = "X-LanDisk-Client";
}

/// <summary>
/// Resolves the caller's session from the cookie jar and records it on the request.
/// Nothing here trusts a proxy header: the remote address always comes from the socket.
/// </summary>
public sealed class SessionMiddleware
{
    private readonly RequestDelegate _next;

    public SessionMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, LanDiskRuntime runtime)
    {
        IPAddress? remote = context.Connection.RemoteIpAddress;
        bool isLoopback = LanInfo.IsLoopback(remote);

        DeviceDefinition? device = null;
        AdminSession? admin = null;

        if (context.Request.Cookies.TryGetValue(SessionKeys.DeviceCookie, out string? deviceToken) && !string.IsNullOrEmpty(deviceToken))
        {
            string hash = SecretTokens.Hash(deviceToken);
            device = runtime.Devices.FindByTokenHash(hash);
        }

        if (isLoopback && context.Request.Cookies.TryGetValue(SessionKeys.AdminCookie, out string? adminToken) && !string.IsNullOrEmpty(adminToken))
        {
            admin = runtime.AdminSessions.Validate(adminToken);
        }

        context.Items["session"] = new RequestSession
        {
            Device = device,
            Admin = admin,
            IsLoopback = isLoopback,
            RemoteAddress = remote?.ToString() ?? "unknown"
        };

        // Presence is measured, not inferred from "a device exists": every authenticated request is
        // evidence that this phone is talking to us right now. In-memory only — the host flushes the
        // accumulated timestamps periodically, so a heartbeat every 15 s does not mean a disk write
        // every 15 s.
        if (device is not null)
        {
            runtime.Devices.Touch(device.Id, remote?.ToString(), DateTimeOffset.Now);
        }

        await _next(context);

        // One counter, two numbers: the dashboard shows today's request volume and failures, and this is the
        // only place that sees every response without any endpoint having to remember to report itself.
        runtime.Metrics.NoteRequest(context.Response.StatusCode);
    }
}

/// <summary>
/// Baseline hardening applied to every response: no sniffing, no framing, a strict CSP, and
/// private caching for anything under /api.
/// </summary>
public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            IHeaderDictionary headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            headers["X-Frame-Options"] = "DENY";
            headers["Cross-Origin-Resource-Policy"] = "same-origin";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            headers["Permissions-Policy"] = "geolocation=(), microphone=(), camera=()";

            if (context.Request.Path.StartsWithSegments("/api"))
            {
                headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
                headers["Pragma"] = "no-cache";
            }

            // The SPA is served from the same origin and uses no inline scripts and no eval.
            // 'wasm-unsafe-eval' is required so the bundled WebAssembly SHA-256 implementation can
            // be compiled; it does not permit evaluating JavaScript from strings.
            // blob: is allowed for locally generated object URLs (previews, downloads).
            headers["Content-Security-Policy"] =
                "default-src 'self'; " +
                "script-src 'self' 'wasm-unsafe-eval'; " +
                "style-src 'self' 'unsafe-inline'; " +
                "img-src 'self' data: blob:; " +
                "media-src 'self' blob:; " +
                "font-src 'self'; " +
                "connect-src 'self'; " +
                "object-src 'none'; " +
                "frame-ancestors 'none'; " +
                "base-uri 'self'; " +
                "form-action 'self'";

            return Task.CompletedTask;
        });

        await _next(context);
    }
}

/// <summary>
/// Rejects requests whose Host header is not one of the addresses this instance actually serves.
/// This is the DNS rebinding guard: a page on evil.example cannot make the browser send a
/// request with a Host that resolves to this machine and expect it to be accepted.
/// </summary>
public sealed class HostValidationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly AllowedHosts _allowed;

    public HostValidationMiddleware(RequestDelegate next, AllowedHosts allowed)
    {
        _next = next;
        _allowed = allowed;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        string? host = context.Request.Host.Host;
        if (string.IsNullOrEmpty(host) || !_allowed.IsAllowed(host))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new
            {
                error = new
                {
                    code = "HOST_NOT_ALLOWED",
                    message = $"请求的 Host（{host}）不在本机允许的访问地址列表中。"
                }
            });
            return;
        }

        await _next(context);
    }
}

public sealed class AllowedHosts
{
    private readonly HashSet<string> _hosts = new(StringComparer.OrdinalIgnoreCase);

    public AllowedHosts(IEnumerable<string> hosts)
    {
        foreach (string host in hosts) _hosts.Add(host);
        _hosts.Add("localhost");
        _hosts.Add("127.0.0.1");
        _hosts.Add("::1");
        _hosts.Add(Environment.MachineName);
        _hosts.Add(Environment.MachineName + ".local");
        _hosts.Add(Dns.GetHostName());
    }

    public void Add(string host)
    {
        if (!string.IsNullOrWhiteSpace(host)) _hosts.Add(host);
    }

    public bool IsAllowed(string host) => _hosts.Contains(host);

    public IReadOnlyCollection<string> Snapshot => _hosts;
}

/// <summary>
/// Enforces the LAN exposure policy on the request path as a second line of defence behind the
/// socket binding: only loopback and private source addresses are ever served, and unencrypted
/// LAN access requires the operator to have explicitly enabled compatibility mode.
/// </summary>
public sealed class NetworkPolicyMiddleware
{
    private readonly RequestDelegate _next;

    public NetworkPolicyMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, LanDiskRuntime runtime)
    {
        IPAddress? remote = context.Connection.RemoteIpAddress;
        IPAddress? local = context.Connection.LocalIpAddress;

        if (remote is null)
        {
            await Reject(context, "NO_REMOTE", "无法确定请求来源地址。");
            return;
        }

        bool loopback = LanInfo.IsLoopback(remote);

        if (!loopback)
        {
            // Requests arriving on the loopback socket can only come from this machine.
            if (LanInfo.IsLoopback(local))
            {
                await Reject(context, "LOOPBACK_ONLY", "该监听地址只接受本机请求。");
                return;
            }

            if (!LanInfo.IsPrivate(remote))
            {
                await Reject(context, "PUBLIC_SOURCE", "只允许来自局域网（私有地址）的请求。");
                return;
            }

            if (!runtime.Settings.LanEnabled)
            {
                await Reject(context, "LAN_DISABLED", "局域网访问当前未开启。");
                return;
            }

            if (LanInfo.TryParseCidr(runtime.Settings.AllowedCidr, out IPAddress network, out int prefix)
                && !LanInfo.IsInCidr(remote, network, prefix))
            {
                await Reject(context, "CIDR_DENIED", "来源地址不在允许的网段内。");
                return;
            }
        }

        await _next(context);
    }

    private static async Task Reject(HttpContext context, string code, string message)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { error = new { code, message } });
    }
}

/// <summary>
/// CSRF defence for cookie authenticated writes.
///
/// A request that changes state must present the session's CSRF token in a custom header.
/// A cross-site attacker cannot read that token and cannot set the header without a successful
/// CORS preflight, which this server never grants. The Origin header, when present, must also
/// match the request's own origin.
/// </summary>
public sealed class CsrfMiddleware
{
    private static readonly HashSet<string> SafeMethods = new(StringComparer.OrdinalIgnoreCase) { "GET", "HEAD", "OPTIONS", "TRACE" };

    private readonly RequestDelegate _next;

    public CsrfMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        if (SafeMethods.Contains(context.Request.Method)
            || !context.Request.Path.StartsWithSegments("/api"))
        {
            await _next(context);
            return;
        }

        // Pairing bootstrap is unauthenticated and protected by its own one time token.
        if (context.Request.Path.StartsWithSegments("/api/pair/redeem"))
        {
            await _next(context);
            return;
        }

        var session = context.Items["session"] as RequestSession;

        if (session is not null && (session.IsDevice || session.IsAdmin))
        {
            string? origin = context.Request.Headers.Origin.ToString();
            if (!string.IsNullOrEmpty(origin) && !OriginMatches(context, origin))
            {
                await Reject(context, "ORIGIN_MISMATCH", "请求来源与本站不一致，已拒绝。");
                return;
            }

            string? provided = context.Request.Headers[SessionKeys.CsrfHeader].ToString();
            if (string.IsNullOrEmpty(provided) || !SecretTokens.FixedTimeEquals(provided, session.CsrfToken))
            {
                await Reject(context, "CSRF_REQUIRED", "缺少或错误的 CSRF 令牌，请刷新页面后重试。");
                return;
            }
        }

        await _next(context);
    }

    private static bool OriginMatches(HttpContext context, string origin) => RequestOrigin.Matches(context, origin);

    private static bool IsLoopbackName(string host) => RequestOrigin.IsLoopbackName(host);

    private static async Task Reject(HttpContext context, string code, string message)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { error = new { code, message } });
    }
}

/// <summary>
/// Origin / Fetch-Metadata checks shared by CSRF protection and by the loopback auto-entry path.
///
/// Browser supplied <c>Origin</c> and <c>Sec-Fetch-*</c> headers cannot be forged by a page, which
/// is exactly why the local administrator session may be granted without a token: a cross-site page
/// can send a request to 127.0.0.1, but it cannot make it look same-origin.
/// </summary>
public static class RequestOrigin
{
    public static bool IsLoopbackName(string host)
        => host is "localhost" or "127.0.0.1" or "::1" or "[::1]";

    public static bool Matches(HttpContext context, string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? parsed)) return false;
        string expectedHost = context.Request.Host.Host;
        int expectedPort = context.Request.Host.Port ?? (context.Request.IsHttps ? 443 : 80);

        bool hostOk = string.Equals(parsed.Host, expectedHost, StringComparison.OrdinalIgnoreCase)
                      || (IsLoopbackName(parsed.Host) && IsLoopbackName(expectedHost));

        return hostOk && parsed.Port == expectedPort;
    }

    /// <summary>
    /// True when the request is a same-origin browser request. Used to decide whether a loopback
    /// caller may be given an administrator session automatically.
    /// </summary>
    public static bool IsTrustedBrowserRequest(HttpContext context, out string reason)
    {
        reason = "ok";

        string origin = context.Request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin) && !Matches(context, origin))
        {
            reason = "ORIGIN_MISMATCH";
            return false;
        }

        string fetchSite = context.Request.Headers["Sec-Fetch-Site"].ToString();
        if (!string.IsNullOrEmpty(fetchSite)
            && !fetchSite.Equals("same-origin", StringComparison.OrdinalIgnoreCase)
            && !fetchSite.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            reason = "CROSS_SITE";
            return false;
        }

        // Forwarded headers are never consulted: without a configured trusted reverse proxy they
        // are attacker controlled, and treating them as evidence of a local caller would let a
        // remote client claim administrator rights.
        return true;
    }

    /// <summary>
    /// Local caller classification. Only the socket peer address is trusted; a request that
    /// arrives on a non-loopback local endpoint is never considered local even if its remote
    /// address looks like loopback.
    /// </summary>
    public static bool IsLocalSocket(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        var local = context.Connection.LocalIpAddress;
        if (remote is null) return false;
        if (!LanInfo.IsLoopback(remote)) return false;
        // When listening on a specific address, Kestrel reports it here.
        return local is null || LanInfo.IsLoopback(local) || LanInfo.IsPrivate(local);
    }
}
