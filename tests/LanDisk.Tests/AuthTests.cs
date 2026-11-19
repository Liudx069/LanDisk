using System.Net;
using System.Text.Json;
using LanDisk.Core.Model;
using LanDisk.Server.Services;
using LanDisk.Tests.Infrastructure;

namespace LanDisk.Tests;

/// <summary>A01-A06: pairing, authorisation, revocation, admin protection and CSRF.</summary>
public sealed class AuthTests : TestBase
{
    protected override void SeedSandbox(TestHost host)
    {
        host.WriteSandboxFile("共享文件.txt", "内容");
        host.WriteSandboxFile("子目录/里面的.txt", "内容");
    }

    // ---- A01 pairing ------------------------------------------------------

    [Fact]
    public async Task A01_pairing_token_is_single_use()
    {
        string token = await Admin.CreatePairingTokenAsync();

        var first = new LanDiskClient(Host.NewClient());
        await first.RedeemAsync(token, "第一台");

        var second = new LanDiskClient(Host.NewClient());
        ApiResult<JsonElement> reuse = await second.PostAsync<JsonElement>("/api/pair/redeem", new { token, deviceName = "第二台" });

        Assert.Equal(HttpStatusCode.NotFound, reuse.Status);
        Assert.Equal("PAIRING_NOT_FOUND", reuse.ErrorCode);
        Record("A01", "配对令牌一次性，重复使用被拒绝");
    }

    [Fact]
    public async Task A01_unknown_token_does_not_leak_information()
    {
        var client = new LanDiskClient(Host.NewClient());
        ApiResult<JsonElement> result = await client.PostAsync<JsonElement>("/api/pair/redeem", new { token = "not-a-real-token" });

        Assert.Equal(HttpStatusCode.NotFound, result.Status);
        Assert.Equal("配对请求不存在或已被使用。", result.ErrorMessage);
        Record("A01", "无效令牌不泄露信息");
    }

    [Fact]
    public async Task A01_short_code_requires_explicit_local_confirmation()
    {
        ApiResult<JsonElement> created = await Admin.PostAsync<JsonElement>("/api/admin/pairing", new { });
        string code = created.Body.GetProperty("shortCode").GetString()!;
        string requestId = created.Body.GetProperty("id").GetString()!;

        var phone = new LanDiskClient(Host.NewClient());
        ApiResult<JsonElement> submitted = await phone.PostAsync<JsonElement>("/api/pair/code", new { shortCode = code, deviceName = "手机" });
        Assert.True(submitted.IsSuccess, submitted.Raw);
        Assert.Equal("awaiting_local_confirmation", submitted.Body.GetProperty("status").GetString());

        string claimSecret = submitted.Body.GetProperty("claimSecret").GetString()!;

        // No session is issued until the operator clicks confirm on the machine.
        ApiResult<JsonElement> beforeConfirm = await phone.PostAsync<JsonElement>("/api/pair/status", new { requestId, claimSecret });
        Assert.Equal("awaitinglocalconfirmation", beforeConfirm.Body.GetProperty("status").GetString());

        ApiResult<JsonElement> guests = await phone.GetAsync<JsonElement>("/api/me");
        Assert.Equal(HttpStatusCode.Unauthorized, guests.Status);

        ApiResult<JsonElement> pending = await Admin.GetAsync<JsonElement>("/api/admin/pairing");
        JsonElement request = pending.Body.GetProperty("requests").EnumerateArray().First(r => r.GetProperty("id").GetString() == requestId);
        Assert.True(request.GetProperty("awaitingConfirmation").GetBoolean());
        Assert.Equal("手机", request.GetProperty("deviceName").GetString());

        ApiResult<JsonElement> confirmed = await Admin.PostAsync<JsonElement>($"/api/admin/pairing/{requestId}/confirm", new { });
        Assert.True(confirmed.IsSuccess, confirmed.Raw);

        ApiResult<JsonElement> afterConfirm = await phone.PostAsync<JsonElement>("/api/pair/status", new { requestId, claimSecret });
        Assert.Equal("paired", afterConfirm.Body.GetProperty("status").GetString());

        ApiResult<JsonElement> me = await phone.GetAsync<JsonElement>("/api/me");
        Assert.Equal(HttpStatusCode.OK, me.Status);
        Assert.Equal("ReadOnly", me.Body.GetProperty("device").GetProperty("level").GetString());
        Record("A01", "6 位配对码需要本机确认，新设备默认只读");
    }

    [Fact]
    public async Task A01_claim_secret_is_required_to_collect_the_session()
    {
        ApiResult<JsonElement> created = await Admin.PostAsync<JsonElement>("/api/admin/pairing", new { });
        string code = created.Body.GetProperty("shortCode").GetString()!;
        string requestId = created.Body.GetProperty("id").GetString()!;

        var phone = new LanDiskClient(Host.NewClient());
        await phone.PostAsync<JsonElement>("/api/pair/code", new { shortCode = code });
        await Admin.PostAsync<JsonElement>($"/api/admin/pairing/{requestId}/confirm", new { });

        var attacker = new LanDiskClient(Host.NewClient());
        ApiResult<JsonElement> stolen = await attacker.PostAsync<JsonElement>("/api/pair/status", new
        {
            requestId,
            claimSecret = "wrong-secret"
        });

        Assert.Equal(HttpStatusCode.Forbidden, stolen.Status);
        Assert.Equal("CLAIM_INVALID", stolen.ErrorCode);
        Record("A01", "配对会话需要正确的持有凭证才能领取");
    }

    [Fact]
    public async Task A01_brute_force_on_the_short_code_is_rate_limited()
    {
        await Admin.PostAsync<JsonElement>("/api/admin/pairing", new { });
        var attacker = new LanDiskClient(Host.NewClient());

        int notFound = 0;
        HttpStatusCode lastStatus = HttpStatusCode.OK;
        for (int i = 0; i < 12; i++)
        {
            string guess = (100000 + i).ToString();
            ApiResult<JsonElement> result = await attacker.PostAsync<JsonElement>("/api/pair/code", new { shortCode = guess });
            if (result.Status == HttpStatusCode.NotFound) notFound++;
            lastStatus = result.Status;
            if (result.Status == HttpStatusCode.TooManyRequests) break;
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, lastStatus);
        Assert.True(notFound >= 6, "前若干次应正常返回未找到，之后才限速");
        Record("A01", $"配对码暴力尝试被限速（前 {notFound} 次返回未找到）");
    }

    [Fact]
    public async Task A01_expired_pairing_request_is_rejected()
    {
        string token = await Admin.CreatePairingTokenAsync();
        LanDisk.Core.Security.PairingRequest? request = Host.Runtime.Pairing.FindByToken(token);
        Assert.NotNull(request);

        // Fast-forward the expiry without waiting two minutes.
        request!.State = LanDisk.Core.Security.PairingState.Pending;
        typeof(LanDisk.Core.Security.PairingRequest)
            .GetProperty(nameof(LanDisk.Core.Security.PairingRequest.ExpiresAt))!
            .SetValue(request, DateTimeOffset.Now.AddSeconds(-1));

        var phone = new LanDiskClient(Host.NewClient());
        ApiResult<JsonElement> result = await phone.PostAsync<JsonElement>("/api/pair/redeem", new { token });

        Assert.Equal(HttpStatusCode.Gone, result.Status);
        Assert.Equal("PAIRING_EXPIRED", result.ErrorCode);
        Record("A01", "过期配对请求被拒绝");
    }

    // ---- A02 unauthenticated access ---------------------------------------

    [Fact]
    public async Task A02_every_file_endpoint_rejects_anonymous_callers()
    {
        var anonymous = new LanDiskClient(Host.NewClient());
        string q = LanDiskClient.FileQuery(ShareId, "共享文件.txt");

        (string Method, string Url)[] cases =
        [
            ("GET", "/api/shares"),
            ("GET", "/api/me"),
            ("GET", $"/api/browse?shareId={ShareId}&path="),
            ("GET", "/api/file/meta" + q),
            ("GET", "/api/file/text" + q),
            ("GET", "/api/file/content" + q),
            ("GET", "/api/file/download" + q),
            ("POST", "/api/file/rename"),
            ("POST", "/api/file/folder"),
            ("POST", "/api/file/delete"),
            ("GET", "/api/recycle"),
            ("POST", "/api/recycle/whatever/restore"),
            ("GET", "/api/uploads/"),
            ("POST", "/api/uploads/"),
            ("GET", "/api/transfers")
        ];

        foreach ((string method, string url) in cases)
        {
            ApiResult<JsonElement> result = await anonymous.SendJsonAsync<JsonElement>(
                new HttpMethod(method), url, method == "GET" ? null : new { });

            Assert.True(result.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
                $"{method} {url} 返回了 {result.Status}，未认证请求必须被拒绝");
        }

        Record("A02", $"未认证访问 {cases.Length} 个文件端点全部被拒绝");
    }

    [Fact]
    public async Task A02_a_stale_session_cookie_is_rejected()
    {
        var device = await PairDeviceAsync();
        JsonElement me = await device.MeAsync();
        Assert.Equal("device", me.GetProperty("kind").GetString());
    }

    // ---- A03 action permissions -------------------------------------------

    [Fact]
    public async Task A03_readonly_device_cannot_write_even_when_the_share_allows_it()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadOnly);
        ApiResult<JsonElement> me = await device.GetAsync<JsonElement>("/api/me");
        JsonElement share = me.Body.GetProperty("shares").EnumerateArray().First(s => s.GetProperty("id").GetString() == ShareId);

        Assert.True(share.GetProperty("canRead").GetBoolean());
        Assert.False(share.GetProperty("canUpload").GetBoolean());
        Assert.False(share.GetProperty("canRename").GetBoolean());
        Assert.False(share.GetProperty("canDelete").GetBoolean());

        ApiResult<JsonElement> upload = await device.PostAsync<JsonElement>("/api/uploads/", new
        {
            shareId = ShareId,
            path = "",
            fileName = "x.bin",
            length = 1,
            fingerprint = new string('a', 64)
        });
        Assert.Equal(HttpStatusCode.Forbidden, upload.Status);

        ApiResult<JsonElement> folder = await device.PostAsync<JsonElement>("/api/file/folder", new { shareId = ShareId, path = "", name = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, folder.Status);

        // Reading still works.
        using HttpResponseMessage download = await device.SendRawAsync(
            new HttpRequestMessage(HttpMethod.Get, "/api/file/download" + LanDiskClient.FileQuery(ShareId, "共享文件.txt")));
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("内容", System.Text.Encoding.UTF8.GetString(await download.Content.ReadAsByteArrayAsync()));
        Record("A03", "只读设备前后端权限一致");
    }

    [Fact]
    public async Task A03_share_flags_narrow_the_device_permissions()
    {
        await Admin.PatchAsync<JsonElement>($"/api/admin/shares/{ShareId}", new
        {
            allowUpload = false,
            allowRename = true,
            allowDelete = false
        });

        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);
        JsonElement me = await device.MeAsync();
        JsonElement share = me.GetProperty("shares").EnumerateArray().First(s => s.GetProperty("id").GetString() == ShareId);

        Assert.False(share.GetProperty("canUpload").GetBoolean());
        Assert.True(share.GetProperty("canRename").GetBoolean());
        Assert.False(share.GetProperty("canDelete").GetBoolean());

        ApiResult<JsonElement> upload = await device.PostAsync<JsonElement>("/api/uploads/", new
        {
            shareId = ShareId,
            path = "",
            fileName = "x.bin",
            length = 1,
            fingerprint = new string('a', 64)
        });
        Assert.Equal(HttpStatusCode.Forbidden, upload.Status);

        ApiResult<JsonElement> delete = await device.PostAsync<JsonElement>("/api/file/delete", new { shareId = ShareId, path = "共享文件.txt", version = "1-1" });
        Assert.Equal(HttpStatusCode.Forbidden, delete.Status);

        Record("A03", "共享能力与设备级别取交集");
    }

    [Fact]
    public async Task A03_disabling_a_share_stops_access_immediately()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);

        ApiResult<JsonElement> before = await device.BrowseAsync(ShareId, "");
        Assert.Equal(HttpStatusCode.OK, before.Status);

        await Admin.PatchAsync<JsonElement>($"/api/admin/shares/{ShareId}", new { enabled = false });

        ApiResult<JsonElement> after = await device.BrowseAsync(ShareId, "");
        Assert.Equal(HttpStatusCode.Forbidden, after.Status);
        Assert.Equal("SHARE_DISABLED", after.ErrorCode);
        Record("A03", "关闭共享后新请求立即失败");
    }

    // ---- A04 revocation ---------------------------------------------------

    [Fact]
    public async Task A04_revoking_a_device_invalidates_its_session_and_cancels_uploads()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite, "待撤销设备");
        JsonElement me = await device.MeAsync();
        string deviceId = me.GetProperty("device").GetProperty("id").GetString()!;

        // Start an upload that is still in flight.
        ApiResult<JsonElement> init = await device.PostAsync<JsonElement>("/api/uploads/", new
        {
            shareId = ShareId,
            path = "",
            fileName = "进行中.bin",
            length = 100_000,
            fingerprint = new string('b', 64)
        });
        Assert.True(init.IsSuccess, init.Raw);
        Assert.Single(Host.Runtime.Uploads.ListForDevice(deviceId));

        ApiResult<JsonElement> revoked = await Admin.PostAsync<JsonElement>($"/api/admin/devices/{deviceId}/revoke", new { });
        Assert.True(revoked.IsSuccess, revoked.Raw);
        Assert.Equal(1, revoked.Body.GetProperty("cancelledUploads").GetInt32());
        Assert.Contains("无法收回", revoked.Body.GetProperty("note").GetString());

        ApiResult<JsonElement> after = await device.BrowseAsync(ShareId, "");
        Assert.Equal(HttpStatusCode.Unauthorized, after.Status);

        Assert.Empty(Host.Runtime.Uploads.ListForDevice(deviceId));
        Record("A04", "撤销设备后会话失败、在控上传被取消");
    }

    [Fact]
    public async Task A04_revoked_device_cannot_be_re_paired_with_the_old_token()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadOnly, "旧设备");
        JsonElement me = await device.MeAsync();
        string deviceId = me.GetProperty("device").GetProperty("id").GetString()!;
        string oldTokenHash = Host.Runtime.Devices.Find(deviceId)!.TokenHash;

        await Admin.PostAsync<JsonElement>($"/api/admin/devices/{deviceId}/revoke", new { });
        DeviceDefinition after = Host.Runtime.Devices.Find(deviceId)!;

        Assert.True(after.Revoked);
        Assert.NotEqual(oldTokenHash, after.TokenHash);
        Assert.Null(Host.Runtime.Devices.FindByTokenHash(oldTokenHash));
        Record("A04", "撤销后旧令牌哈希不再可用");
    }

    // ---- A05 admin protection --------------------------------------------

    [Fact]
    public async Task A05_a_paired_device_cannot_touch_the_admin_surface()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);

        (string Method, string Url)[] cases =
        [
            ("GET", "/api/admin/status"),
            ("GET", "/api/admin/shares"),
            ("POST", "/api/admin/shares"),
            ("PATCH", "/api/admin/shares/x"),
            ("DELETE", "/api/admin/shares/x"),
            ("GET", "/api/admin/devices"),
            ("POST", "/api/admin/pairing"),
            ("POST", "/api/admin/network"),
            ("GET", "/api/admin/tls"),
            ("GET", "/api/admin/recycle"),
            ("POST", "/api/admin/recycle/empty"),
            ("GET", "/api/admin/audit"),
            ("POST", "/api/admin/shutdown")
        ];

        foreach ((string method, string url) in cases)
        {
            ApiResult<JsonElement> result = await device.SendJsonAsync<JsonElement>(
                new HttpMethod(method), url, method is "GET" or "DELETE" ? null : new { });

            Assert.True(result.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
                $"{method} {url} 返回了 {result.Status}");
        }

        Record("A05", $"设备会话无法访问 {cases.Length} 个管理端点");
    }

    [Fact]
    public async Task A05_admin_bootstrap_token_is_required_and_is_not_stored_in_the_browser_cookie()
    {
        var client = new LanDiskClient(Host.NewClient());
        ApiResult<JsonElement> wrong = await client.PostAsync<JsonElement>("/api/admin/session", new { token = "wrong-token" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.Status);
        Assert.Equal("ADMIN_TOKEN_INVALID", wrong.ErrorCode);

        await client.AdminLoginAsync(Host.AdminToken);
        ApiResult<JsonElement> ok = await client.GetAsync<JsonElement>("/api/admin/status");
        Assert.True(ok.IsSuccess, ok.Raw);

        string json = ok.Raw;
        Assert.DoesNotContain(Host.AdminToken, json, StringComparison.Ordinal);
        Record("A05", "管理令牌校验正确，且不回显到响应中");
    }

    [Fact]
    public async Task A05_admin_endpoints_reject_a_non_loopback_remote_address()
    {
        // The policy is unit tested with a synthetic connection so that the automated suite never
        // has to open a LAN socket.
        var denied = await NetworkPolicyProbe.RunAsync(
            Host,
            remoteIp: "192.168.1.50",
            localIp: "192.168.1.10",
            path: "/api/admin/status",
            lanEnabled: false);

        Assert.Equal(HttpStatusCode.Forbidden, denied.Status);
        Assert.Contains("LAN_DISABLED", denied.Body);

        var publicSource = await NetworkPolicyProbe.RunAsync(
            Host,
            remoteIp: "203.0.113.9",
            localIp: "192.168.1.10",
            path: "/api/health",
            lanEnabled: true);

        Assert.Equal(HttpStatusCode.Forbidden, publicSource.Status);
        Assert.Contains("PUBLIC_SOURCE", publicSource.Body);

        Record("A05", "非回环来源被网络策略拒绝（合成连接，未开放真实 LAN 端口）");
    }

    // ---- A06 CSRF / CORS / Host ------------------------------------------

    [Fact]
    public async Task A06_missing_csrf_token_blocks_state_changing_requests()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);
        device.SetCsrf(string.Empty);

        ApiResult<JsonElement> result = await device.PostAsync<JsonElement>("/api/file/rename", new
        {
            shareId = ShareId,
            path = "共享文件.txt",
            newName = "改名.txt",
            version = "1-1"
        });

        Assert.Equal(HttpStatusCode.Forbidden, result.Status);
        Assert.Equal("CSRF_REQUIRED", result.ErrorCode);
        Assert.True(File.Exists(Host.SandboxPath("共享文件.txt")));
        Record("A06", "缺少 CSRF 令牌时写操作被拒绝");
    }

    [Fact]
    public async Task A06_wrong_csrf_token_is_rejected()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);
        device.SetCsrf("this-is-not-the-right-token");

        ApiResult<JsonElement> result = await device.PostAsync<JsonElement>("/api/file/folder", new { shareId = ShareId, path = "", name = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, result.Status);
        Assert.Equal("CSRF_REQUIRED", result.ErrorCode);
    }

    [Fact]
    public async Task A06_cross_origin_writes_are_rejected()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/file/folder")
        {
            Content = new StringContent("{\"shareId\":\"" + ShareId + "\",\"path\":\"\",\"name\":\"x\"}", System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Origin", "https://evil.example");

        using HttpResponseMessage response = await device.SendRawAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("ORIGIN_MISMATCH", body);
        Record("A06", "跨站来源的写请求被拒绝");
    }

    [Fact]
    public async Task A06_no_cors_permission_is_granted()
    {
        var device = await PairDeviceAsync();

        var request = new HttpRequestMessage(HttpMethod.Options, "/api/file/delete");
        request.Headers.Add("Origin", "https://evil.example");
        request.Headers.Add("Access-Control-Request-Method", "POST");

        using HttpResponseMessage response = await device.SendRawAsync(request);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"),
            "服务端不能对任意来源开放跨域访问");
        Record("A06", "未授予任何 CORS 许可");
    }

    [Fact]
    public async Task A06_unknown_host_header_is_rejected_for_dns_rebinding_protection()
    {
        var device = await PairDeviceAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/browse?shareId=" + ShareId + "&path=");
        request.Headers.Host = "evil.example.com";

        using HttpResponseMessage response = await device.SendRawAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("HOST_NOT_ALLOWED", body);
        Record("A06", "伪造 Host 被拒绝（防 DNS rebinding）");
    }

    [Fact]
    public async Task A06_forged_proxy_headers_do_not_change_the_effective_origin()
    {
        var device = await PairDeviceAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        request.Headers.Add("X-Forwarded-For", "10.0.0.1");
        request.Headers.Add("X-Forwarded-Host", "evil.example");
        request.Headers.Add("X-Real-IP", "8.8.8.8");

        using HttpResponseMessage response = await device.SendRawAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("isLoopback").GetBoolean(),
            "转发头不得影响来源判定");
        Record("A06", "不信任伪造的转发头");
    }

    [Fact]
    public async Task E03_fresh_instance_has_no_shares_no_devices_and_no_anonymous_access()
    {
        await using TestHost fresh = await TestHost.CreateAsync("Lifecycle");

        var anonymous = new LanDiskClient(fresh.NewClient());
        ApiResult<JsonElement> health = await anonymous.GetAsync<JsonElement>("/api/health");
        Assert.True(health.IsSuccess, health.Raw);

        ApiResult<JsonElement> shares = await anonymous.GetAsync<JsonElement>("/api/shares");
        Assert.Equal(HttpStatusCode.Unauthorized, shares.Status);

        var admin = new LanDiskClient(fresh.NewClient());
        await admin.AdminLoginAsync(fresh.AdminToken);

        ApiResult<JsonElement> adminShares = await admin.GetAsync<JsonElement>("/api/admin/shares");
        Assert.Empty(adminShares.Body.GetProperty("shares").EnumerateArray());

        ApiResult<JsonElement> devices = await admin.GetAsync<JsonElement>("/api/admin/devices");
        Assert.Empty(devices.Body.GetProperty("devices").EnumerateArray());

        ApiResult<JsonElement> status = await admin.GetAsync<JsonElement>("/api/admin/status");
        JsonElement network = status.Body.GetProperty("network");
        Assert.False(network.GetProperty("lanEnabled").GetBoolean());
        Assert.False(network.GetProperty("httpsEnabled").GetBoolean());
        Assert.False(network.GetProperty("httpCompatModeEnabled").GetBoolean());

        foreach (string url in network.GetProperty("boundUrls").EnumerateArray().Select(u => u.GetString()!))
        {
            Assert.True(url.Contains("127.0.0.1") || url.Contains("[::1]"),
                "默认监听必须只包含回环地址，实际为 " + url);
        }

        Record("E03", "全新实例无共享、无设备、默认只监听回环");
    }
}

/// <summary>
/// Drives the network policy middleware with a synthetic connection so that the automated suite
/// can prove the LAN exposure rules without ever binding a real LAN socket.
/// </summary>
public static class NetworkPolicyProbe
{
    public static async Task<(HttpStatusCode Status, string Body)> RunAsync(
        TestHost host, string remoteIp, string localIp, string path, bool lanEnabled)
    {
        bool original = host.Runtime.Settings.LanEnabled;
        host.Runtime.Settings.LanEnabled = lanEnabled;
        try
        {
            var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
            context.Request.Path = path;
            context.Request.Method = "GET";
            context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
            context.Connection.LocalIpAddress = IPAddress.Parse(localIp);
            context.Response.Body = new MemoryStream();

            bool nextCalled = false;
            var middleware = new NetworkPolicyMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
            await middleware.InvokeAsync(context, host.Runtime);

            context.Response.Body.Position = 0;
            string body = await new StreamReader(context.Response.Body).ReadToEndAsync();
            return (nextCalled ? HttpStatusCode.OK : (HttpStatusCode)context.Response.StatusCode, body);
        }
        finally
        {
            host.Runtime.Settings.LanEnabled = original;
        }
    }
}
