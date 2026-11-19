using System.Net;
using System.Text.Json;
using LanDisk.Core.Model;
using LanDisk.Server.Services;
using LanDisk.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;

namespace LanDisk.Tests;

/// <summary>
/// The authentication changes from this round: the local management page is entered without a
/// token, and that shortcut is available only to a genuine same-origin loopback browser request.
/// </summary>
public sealed class AdminAutoEntryTests : TestBase
{
    protected override void SeedSandbox(TestHost host) => host.WriteSandboxFile("文件.txt", "内容");

    // ---- automatic local entry -------------------------------------------

    [Fact]
    public async Task AUTOENTRY_loopback_get_session_succeeds_without_any_token()
    {
        var client = new LanDiskClient(Host.NewClient());

        ApiResult<JsonElement> entry = await client.GetAsync<JsonElement>("/api/admin/session");
        Assert.Equal(HttpStatusCode.OK, entry.Status);
        Assert.True(entry.Body.GetProperty("ok").GetBoolean());
        Assert.False(entry.Body.GetProperty("tokenRequired").GetBoolean());

        client.SetCsrf(entry.Body.GetProperty("csrfToken").GetString()!);

        ApiResult<JsonElement> status = await client.GetAsync<JsonElement>("/api/admin/status");
        Assert.True(status.IsSuccess, status.Raw);
        // Assert against the running assembly rather than a literal, so bumping the product version
        // does not turn a behaviour test into a version test.
        Assert.Equal(LanDisk.Server.Endpoints.BuildInfo.Version, status.Body.GetProperty("version").GetString());

        Record("AUTOENTRY", "loopback 首次打开管理页即自动建立会话，无需输入任何 token");
    }

    [Fact]
    public async Task AUTOENTRY_post_session_without_a_token_also_works_on_loopback()
    {
        var client = new LanDiskClient(Host.NewClient());
        ApiResult<JsonElement> entry = await client.PostAsync<JsonElement>("/api/admin/session", new { });
        Assert.Equal(HttpStatusCode.OK, entry.Status);
        Record("AUTOENTRY", "POST /api/admin/session 在 loopback 上同样自动进入");
    }

    [Fact]
    public async Task AUTOENTRY_is_idempotent_and_returns_the_same_session()
    {
        var client = new LanDiskClient(Host.NewClient());
        string first = (await client.GetAsync<JsonElement>("/api/admin/session")).Body.GetProperty("csrfToken").GetString()!;
        string second = (await client.GetAsync<JsonElement>("/api/admin/session")).Body.GetProperty("csrfToken").GetString()!;
        Assert.Equal(first, second);
        Record("AUTOENTRY", "重复进入管理端复用同一会话");
    }

    [Fact]
    public async Task AUTOENTRY_repeated_without_cookies_issues_a_fresh_session_each_time()
    {
        // A browser profile that blocks cookies simply gets a new session per request; it still
        // never needs a token.
        using var noCookies = Host.NewCookieObservationClient();

        for (int i = 0; i < 3; i++)
        {
            using HttpResponseMessage response = await noCookies.GetAsync("/api/admin/session");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Record("AUTOENTRY", "禁用 cookie 的浏览器仍可自动进入（每次得到新会话）");
    }

    // ---- the shortcut must not leak to other origins ----------------------

    [Fact]
    public async Task AUTOENTRY_cross_site_fetch_metadata_is_refused()
    {
        using HttpResponseMessage response = await SendRaw(runtime =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/session");
            request.Headers.Add("Sec-Fetch-Site", "cross-site");
            request.Headers.Add("Origin", "https://evil.example");
            return request;
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("ORIGIN_MISMATCH", body);

        Record("AUTOENTRY", "跨站请求不能自动获得管理会话");
    }

    [Fact]
    public async Task AUTOENTRY_cross_site_without_origin_header_is_refused()
    {
        using HttpResponseMessage response = await SendRaw(_ =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/session");
            // Same-site metadata alone must be enough: the header is browser controlled.
            request.Headers.Add("Sec-Fetch-Site", "cross-site");
            return request;
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("CROSS_SITE", body);

        Record("AUTOENTRY", "仅凭 Sec-Fetch-Site: cross-site 即拒绝自动进入");
    }

    [Fact]
    public async Task AUTOENTRY_forged_forwarded_headers_do_not_create_a_local_caller()
    {
        // The socket peer is loopback here, so automatic entry is legitimate; what matters is that
        // the forwarded headers are ignored rather than consulted. A remote client cannot become
        // local by adding them, which is asserted directly against the policy below.
        using HttpResponseMessage response = await SendRaw(_ =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/session");
            request.Headers.Add("X-Forwarded-For", "127.0.0.1");
            request.Headers.Add("X-Forwarded-Host", "localhost");
            request.Headers.Add("Forwarded", "for=127.0.0.1;host=localhost");
            return request;
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Direct policy check with a synthetic non-loopback connection.
        Assert.False(RequestOrigin.IsLocalSocket(SyntheticContext(IPAddress.Parse("192.168.1.77"), IPAddress.Parse("192.168.1.10"))));
        Assert.True(RequestOrigin.IsLocalSocket(SyntheticContext(IPAddress.Loopback, IPAddress.Loopback)));

        Record("AUTOENTRY", "转发头不参与本机判定：伪造 X-Forwarded-For 不能把远程请求变成 loopback");
    }

    [Fact]
    public void AUTOENTRY_policy_rejects_wrong_origin_and_accepts_same_origin()
    {
        DefaultHttpContext context = SyntheticContext(IPAddress.Loopback, IPAddress.Loopback);
        context.Request.Host = new HostString("127.0.0.1", 8891);

        // No Origin and no Fetch metadata: a plain navigation, which is allowed.
        Assert.True(RequestOrigin.IsTrustedBrowserRequest(context, out _));

        context.Request.Headers.Origin = "http://127.0.0.1:8891";
        Assert.True(RequestOrigin.IsTrustedBrowserRequest(context, out _));

        context.Request.Headers.Origin = "http://127.0.0.1:9999";
        Assert.False(RequestOrigin.IsTrustedBrowserRequest(context, out string portReason));
        Assert.Equal("ORIGIN_MISMATCH", portReason);

        context.Request.Headers.Origin = "https://evil.example";
        Assert.False(RequestOrigin.IsTrustedBrowserRequest(context, out string originReason));
        Assert.Equal("ORIGIN_MISMATCH", originReason);

        context.Request.Headers.Origin = string.Empty;
        context.Request.Headers["Sec-Fetch-Site"] = "same-origin";
        Assert.True(RequestOrigin.IsTrustedBrowserRequest(context, out _));

        context.Request.Headers["Sec-Fetch-Site"] = "none";
        Assert.True(RequestOrigin.IsTrustedBrowserRequest(context, out _));

        context.Request.Headers["Sec-Fetch-Site"] = "same-site";
        Assert.False(RequestOrigin.IsTrustedBrowserRequest(context, out string siteReason));
        Assert.Equal("CROSS_SITE", siteReason);
    }

    // ---- the recovery token still works but is not the normal path --------

    [Fact]
    public async Task AUTOENTRY_wrong_recovery_token_is_still_rejected()
    {
        var client = new LanDiskClient(Host.NewClient());
        ApiResult<JsonElement> result = await client.PostAsync<JsonElement>("/api/admin/session", new { token = "not-the-token" });
        Assert.Equal(HttpStatusCode.Unauthorized, result.Status);
        Assert.Equal("ADMIN_TOKEN_INVALID", result.ErrorCode);

        Record("AUTOENTRY", "显式提供错误令牌时仍然拒绝（兼容路径没有被削弱）");
    }

    [Fact]
    public async Task AUTOENTRY_token_requirement_endpoint_reports_availability()
    {
        var client = new LanDiskClient(Host.NewClient());
        ApiResult<JsonElement> result = await client.GetAsync<JsonElement>("/api/admin/session/token-required");

        Assert.Equal(HttpStatusCode.OK, result.Status);
        Assert.True(result.Body.GetProperty("loopback").GetBoolean());
        Assert.True(result.Body.GetProperty("autoEntryAvailable").GetBoolean());
        Assert.True(result.Body.GetProperty("fallbackAvailable").GetBoolean());

        Record("AUTOENTRY", "管理端可查询自动进入是否可用");
    }

    // ---- cookie and secret hygiene ---------------------------------------

    [Fact]
    public async Task COOKIE_device_session_is_httponly_samesite_and_never_a_readable_secret()
    {
        using var observer = Host.NewCookieObservationClient();

        string token = await Admin.CreatePairingTokenAsync();

        using var redeem = new HttpRequestMessage(HttpMethod.Post, "/api/pair/redeem")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { token, deviceName = "cookie 检查" }),
                System.Text.Encoding.UTF8, "application/json")
        };

        using HttpResponseMessage response = await observer.SendAsync(redeem);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string[] setCookies = response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values)
            ? values.ToArray()
            : [];

        string session = setCookies.First(c => c.StartsWith("landisk_session=", StringComparison.Ordinal));
        Assert.Contains("httponly", session, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", session, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", session, StringComparison.OrdinalIgnoreCase);

        // The CSRF token is deliberately readable by the page; the session token is not.
        string csrf = setCookies.First(c => c.StartsWith("landisk_csrf=", StringComparison.Ordinal));
        Assert.DoesNotContain("httponly", csrf, StringComparison.OrdinalIgnoreCase);

        Record("COOKIE", "设备会话 cookie 为 HttpOnly + SameSite=Strict，CSRF cookie 可被页面读取");
        _ = session;
    }

    [Fact]
    public async Task COOKIE_admin_session_is_httponly_and_token_free_in_the_url()
    {
        using var observer = Host.NewCookieObservationClient();
        using HttpResponseMessage response = await observer.GetAsync("/api/admin/session");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string[] setCookies = response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values)
            ? values.ToArray()
            : [];

        string admin = setCookies.First(c => c.StartsWith("landisk_admin=", StringComparison.Ordinal));
        Assert.Contains("httponly", admin, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", admin, StringComparison.OrdinalIgnoreCase);

        Record("COOKIE", "管理会话 cookie 为 HttpOnly + SameSite=Strict，登录不再依赖 URL 中的令牌");
    }

    [Fact]
    public async Task PAIRING_url_carries_the_one_time_token_in_the_fragment_only()
    {
        ApiResult<JsonElement> created = await Admin.PostAsync<JsonElement>("/api/admin/pairing", new { });
        string url = created.Body.GetProperty("url").GetString()!;

        // The fragment is never sent to a server and never appears in a Referer, so the one time
        // credential cannot leak through logs or history the way a query parameter would.
        Assert.Contains("#t=", url);
        Assert.DoesNotContain("?t=", url);
        Assert.DoesNotContain("token=", url.Split('#')[0]);

        string token = created.Body.GetProperty("token").GetString()!;
        Assert.True(token.Length >= 40, "一次性配对令牌应有足够随机性");

        Record("PAIRING", "配对链接把一次性令牌放在 URL 片段中，不出现在查询串");
    }

    [Fact]
    public async Task PAIRING_secret_never_appears_in_the_audit_log()
    {
        ApiResult<JsonElement> created = await Admin.PostAsync<JsonElement>("/api/admin/pairing", new { });
        string token = created.Body.GetProperty("token").GetString()!;
        string shortCode = created.Body.GetProperty("shortCode").GetString()!;

        var phone = new LanDiskClient(Host.NewClient());
        await phone.RedeemAsync(token, "日志检查设备");

        string audit = (await Admin.GetAsync<JsonElement>("/api/admin/audit?limit=200")).Raw;
        Assert.DoesNotContain(token, audit, StringComparison.Ordinal);
        Assert.DoesNotContain(shortCode, audit, StringComparison.Ordinal);
        Assert.DoesNotContain(Host.Runtime.AdminBootstrapToken, audit, StringComparison.Ordinal);

        Record("PAIRING", "一次性令牌与短码不出现在审计日志中");
    }

    // ---- persistence across a restart ------------------------------------

    [Fact]
    public async Task RESTART_paired_device_stays_signed_in_after_the_server_restarts()
    {
        var jar = TestHost.NewCookieJar();
        var device = new LanDiskClient(Host.NewClient(sharedCookies: jar));

        string token = await Admin.CreatePairingTokenAsync();
        JsonElement paired = await device.RedeemAsync(token, "重启设备");
        string deviceId = paired.GetProperty("id").GetString()!;

        Assert.Contains("文件.txt", LanDiskClient.Names(await device.BrowseAsync(ShareId, "")));

        // Stop this instance and start a new one over the same data directory.
        await using TestHost reopened = await TestHost.ReopenAsync(Host);

        // The admin cookie jar is separate, so a fresh admin client is used to inspect state.
        var admin2 = new LanDiskClient(reopened.NewClient());
        await admin2.AdminLoginAsync(reopened.AdminToken);
        JsonElement devices = (await admin2.GetAsync<JsonElement>("/api/admin/devices")).Body;
        Assert.Contains(devices.GetProperty("devices").EnumerateArray(),
            d => d.GetProperty("id").GetString() == deviceId);

        // The device replays its original cookie against the new process.
        var device2 = new LanDiskClient(reopened.NewClient(sharedCookies: jar));
        ApiResult<JsonElement> me = await device2.GetAsync<JsonElement>("/api/me");
        Assert.Equal(HttpStatusCode.OK, me.Status);
        Assert.Equal(deviceId, me.Body.GetProperty("device").GetProperty("id").GetString());

        // And it can still browse through the restarted server.
        ApiResult<JsonElement> browse = await device2.BrowseAsync(ShareId, "");
        Assert.Equal(HttpStatusCode.OK, browse.Status);

        Record("RESTART", "服务器重启后已配对设备凭原 cookie 直接可用，无需重新配对");
    }

    [Fact]
    public async Task RESTART_revoked_device_stays_revoked_after_a_restart()
    {
        var jar = TestHost.NewCookieJar();
        var device = new LanDiskClient(Host.NewClient(sharedCookies: jar));
        JsonElement paired = await device.RedeemAsync(await Admin.CreatePairingTokenAsync(), "撤销设备");
        string deviceId = paired.GetProperty("id").GetString()!;

        await Admin.PostAsync<JsonElement>($"/api/admin/devices/{deviceId}/revoke", new { });

        await using TestHost reopened = await TestHost.ReopenAsync(Host);
        var device2 = new LanDiskClient(reopened.NewClient(sharedCookies: jar));

        ApiResult<JsonElement> me = await device2.GetAsync<JsonElement>("/api/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.Status);

        Record("RESTART", "撤销状态在重启后依然有效，旧 cookie 不能复活会话");
    }

    [Fact]
    public async Task RESTART_ui_preferences_and_favorites_survive()
    {
        var jar = TestHost.NewCookieJar();
        var device = new LanDiskClient(Host.NewClient(sharedCookies: jar));
        await device.RedeemAsync(await Admin.CreatePairingTokenAsync(), "偏好设备");

        await device.PatchAsync<JsonElement>("/api/preferences", new { sort = "modified", descending = true, view = "grid" });
        await device.PostAsync<JsonElement>("/api/favorites", new { shareId = ShareId, path = "文件.txt" });

        await using TestHost reopened = await TestHost.ReopenAsync(Host);
        var device2 = new LanDiskClient(reopened.NewClient(sharedCookies: jar));

        JsonElement preferences = (await device2.GetAsync<JsonElement>("/api/preferences")).Body;
        Assert.Equal("Modified", preferences.GetProperty("sort").GetString());
        Assert.True(preferences.GetProperty("descending").GetBoolean());
        Assert.Equal("grid", preferences.GetProperty("view").GetString());

        JsonElement favorites = (await device2.GetAsync<JsonElement>("/api/favorites")).Body;
        Assert.Single(favorites.GetProperty("items").EnumerateArray());

        Record("RESTART", "排序偏好与收藏在重启后保留");
    }

    // ---- helpers ----------------------------------------------------------

    private async Task<HttpResponseMessage> SendRaw(Func<LanDiskRuntime, HttpRequestMessage> factory)
    {
        var client = new LanDiskClient(Host.NewClient());
        return await client.SendRawAsync(factory(Host.Runtime));
    }

    private static DefaultHttpContext SyntheticContext(IPAddress remote, IPAddress local)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = remote;
        context.Connection.LocalIpAddress = local;
        return context;
    }
}
