using LanDisk.Core.Networking;
using LanDisk.Server.Services;

namespace LanDisk.Server.Endpoints;

/// <summary>
/// The first-connection wizard.
///
/// <para>Two deliberately different surfaces live here. The admin routes are loopback-only and
/// authenticated like every other management route, because they open and close a listening socket.
/// The trust probe is reachable from the LAN and unauthenticated <i>by design</i>: its only job is to
/// record that a phone successfully completed a TLS handshake against our own root CA, which is the
/// signal the wizard waits for. It reveals nothing, changes nothing, and only exists while the wizard
/// is open.</para>
/// </summary>
public static class OnboardingEndpoints
{
    public static void MapOnboardingEndpoints(this WebApplication app)
    {
        // ---- public: HTTPS trust probe -------------------------------------
        //
        // The bootstrap page (plain HTTP, port 8890) loads this URL as an image — over HTTPS. Two facts have
        // to be established by *this* request before the wizard is allowed to believe anything:
        //
        //   1. it arrived over TLS, so the phone really completed a handshake with our own root CA, and
        //   2. its Host header names a host this installation is allowed to advertise, so a forged header
        //      cannot put a pairing link (or a "trusted" flag) somewhere else.
        //
        // Only when both hold is the wizard marked trusted, and only then does the caller see 204. Anything
        // else is answered as "nothing here": a plain-HTTP probe is not a weaker trust signal, it is not a
        // trust signal at all, and treating one as valid would let any local process declare the phone
        // trusted just by knowing the bootstrap id. No credentials, no state, no response body either way.
        app.MapGet("/api/onboarding/trust-probe", (HttpContext context, LanDiskRuntime runtime) =>
        {
            string? id = context.Request.Query["b"];
            if (!runtime.Onboarding.IsActive || !runtime.Onboarding.MatchesBootstrapId(id))
                return Results.StatusCode(StatusCodes.Status404NotFound);

            if (!context.Request.IsHttps)
                return Results.StatusCode(StatusCodes.Status404NotFound);

            // Records the host only when it is the stable name or an address the classifier currently
            // accepts; a forged Host, a public name, a VPN/TUN address or a Hyper-V switch address is
            // refused inside OnboardingService, and a refused host is not evidence of anything.
            if (!runtime.Onboarding.RecordVerifiedHttpsHost(context.Request.Host.Host))
                return Results.StatusCode(StatusCodes.Status404NotFound);

            runtime.Onboarding.MarkTrusted(context.Connection.RemoteIpAddress?.ToString());

            context.Response.Headers.CacheControl = "no-store";
            return Results.StatusCode(StatusCodes.Status204NoContent);
        });

        // ---- public: plain-language identity, no secrets --------------------
        app.MapGet("/api/onboarding/identity", (LanDiskRuntime runtime) => Results.Json(new
        {
            product = "LanDisk",
            hostName = runtime.Identity.HostName,
            httpsPort = runtime.Settings.HttpsPort,
            httpsUrl = runtime.Identity.HttpsOrigin(runtime.Settings.HttpsPort),
            stableNameAvailable = runtime.Mdns is { IsRunning: true },
            addresses = runtime.LanAddresses()
        }, ApiHelpers.Json));

        var group = app.MapGroup("/api/admin/onboarding");

        group.MapGet("/", (HttpContext context, LanDiskRuntime runtime) =>
        {
            if (!AdminEndpoints.RequireAdmin(context, runtime, out IResult? error)) return error!;

            OnboardingStatus status = runtime.Onboarding.Status();
            return Results.Json(new
            {
                active = status.Active,
                bootstrapId = status.BootstrapId,
                bootstrapUrl = status.BootstrapUrl,
                expiresAt = status.ExpiresAt,
                trusted = status.Trusted,
                trustedAt = status.TrustedAt,
                hostName = status.HostName,
                httpsUrl = status.HttpsUrl,
                addresses = status.Addresses,
                listeners = status.Listeners,
                port = status.Port,
                lastError = status.LastError,
                stableNameAvailable = runtime.Mdns is { IsRunning: true },
                mdnsError = runtime.Mdns?.LastError,
                certificate = CertificateView(runtime)
            }, ApiHelpers.Json);
        });

        group.MapPost("/start", (HttpContext context, LanDiskRuntime runtime) =>
        {
            if (!AdminEndpoints.RequireAdmin(context, runtime, out IResult? error)) return error!;

            int port = 0;
            if (int.TryParse(context.Request.Query["port"], out int requested) && requested is > 0 and < 65536)
                port = requested;

            OnboardingStatus status = runtime.Onboarding.Start(port > 0 ? port : OnboardingService.DefaultBootstrapPort);

            return Results.Json(new
            {
                active = status.Active,
                bootstrapUrl = status.BootstrapUrl,
                expiresAt = status.ExpiresAt,
                port = status.Port,
                lastError = status.LastError
            }, ApiHelpers.Json);
        });

        group.MapPost("/stop", (HttpContext context, LanDiskRuntime runtime) =>
        {
            if (!AdminEndpoints.RequireAdmin(context, runtime, out IResult? error)) return error!;

            runtime.Onboarding.Stop("用户结束了首次连接向导");
            return Results.Json(new { ok = true, active = runtime.Onboarding.IsActive }, ApiHelpers.Json);
        });

        // Explicit, warned, and the only path that rotates the root CA. Every phone that trusted the
        // old root has to trust the new one by hand afterwards.
        group.MapPost("/reset-ca", (HttpContext context, LanDiskRuntime runtime) =>
        {
            if (!AdminEndpoints.RequireAdmin(context, runtime, out IResult? error)) return error!;

            string? confirm = context.Request.Query["confirm"];
            if (!string.Equals(confirm, "yes", StringComparison.OrdinalIgnoreCase))
            {
                return ApiHelpers.Fail(StatusCodes.Status400BadRequest, "CONFIRM_REQUIRED",
                    "重置本地根证书会让所有已信任该证书的手机关联失效，必须显式确认。");
            }

            runtime.Certificates.ResetCa(runtime.Identity.InstallId);
            runtime.EnsureCertificates(refresh: true);

            return Results.Json(new
            {
                ok = true,
                warning = "本地根 CA 已更换：所有已安装该证书的 iPhone 必须重新安装并重新开启“完全信任”，否则 Safari 会报证书错误。共享与配对记录不受影响。",
                certificate = CertificateView(runtime)
            }, ApiHelpers.Json);
        });
    }

    private static object CertificateView(LanDiskRuntime runtime)
    {
        runtime.EnsureCertificates();
        CertificateInfo? info = runtime.Certificate;

        return new
        {
            subject = info?.Subject,
            issuer = info?.Issuer,
            notAfter = info?.NotAfter,
            dnsNames = info?.DnsNames,
            ipAddresses = info?.IpAddresses,
            issuedByLocalCa = info?.IsIssuedByLocalCa ?? false,
            rootCaSha256 = info?.RootCaSha256,
            rootCaSubject = info?.RootCaSubject,
            rootCaNotAfter = info?.RootCaNotAfter,
            leafReason = info?.LeafReason,
            reusable = !(runtime.ServerCertificate?.Reissued ?? true)
        };
    }
}
