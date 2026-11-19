using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using LanDisk.Core.Configuration;
using LanDisk.Core.Model;
using LanDisk.Core.Security;
using LanDisk.Server.Services;
using LanDisk.Tests.Infrastructure;

namespace LanDisk.Tests;

/// <summary>
/// REPAIR_01 — the last three defects of the 09 round, each pinned by an executable gate.
///
/// <para><b>1. The wizard did not close itself.</b> Confirming a phone left the plain-HTTP bootstrap
/// listener on 8890 running until the user found and pressed "关闭向导并停止引导页". The stop now lives in
/// <see cref="AdminOperations.ConfirmPairing"/> — after the device is persisted and the request points at
/// it — so the native window and the browser endpoint close it in exactly the same way.</para>
///
/// <para><b>2. "Any unrevoked device exists" was used as "this wizard is finished".</b> On a machine that
/// already had iPhone A, the wizard for iPhone B reported itself complete before B had done anything.
/// Completion is now read from the one pairing request this wizard produced, via
/// <see cref="AdminOperations.CompletionOf"/>.</para>
///
/// <para><b>3. "连接 iPhone" needed a second click.</b> The page showed "开始首次连接向导" and waited for
/// the user to press it. Entering the page now starts the wizard through
/// <see cref="AdminOperations.EnsureOnboardingStarted"/>, which never restarts a running wizard and never
/// disturbs a pairing flow that is already in flight.</para>
///
/// <para>The flows here are driven over real HTTP on the loopback ports the test host actually bound, and
/// the bootstrap listener is proven closed by connecting to it (connection refused), not by reading a
/// field. Everything runs in the test's own data root; the user's real installation is never touched.</para>
/// </summary>
public sealed class Repair01OnboardingTests : TestBase
{
    private AdminOperations Native => Host.Runtime.Admin;

    // ------------------------------------------------------------------ helpers

    /// <summary>A port that is free on this machine right now, so the "listener is really gone" probe
    /// below is about a real socket rather than about the placeholder port 0.</summary>
    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Any, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>Opens the wizard the way the product does: on the private LAN addresses this machine
    /// actually has, on a port this run picked.</summary>
    private OnboardingService StartOnboarding()
    {
        OnboardingService onboarding = Host.Runtime.Onboarding;

        // The bootstrap page needs the CA the phone is supposed to install.
        Host.Runtime.Certificates.EnsureServerCertificate(Host.Runtime.Identity.InstallId, Host.Runtime.Identity.HostName);

        for (int attempt = 0; attempt < 5 && !onboarding.IsActive; attempt++)
        {
            onboarding.Start(FreePort());
        }

        Assert.True(onboarding.IsActive, "首次连接向导应当已经开启：" + onboarding.LastError);
        Assert.NotEmpty(onboarding.Listeners);
        return onboarding;
    }

    /// <summary>Creates a pairing request the way the "连接 iPhone" page does: through the admin surface,
    /// which is the same call the native button makes.</summary>
    private async Task<PairingRequest> CreateRequestAsync()
    {
        ApiResult<JsonElement> created = await Admin.PostAsync<JsonElement>("/api/admin/pairing", new { });
        Assert.True(created.IsSuccess, created.Raw);

        PairingRequest? request = Host.Runtime.Pairing.Find(created.Body.GetProperty("id").GetString()!);
        Assert.NotNull(request);
        Assert.Equal(created.Body.GetProperty("shortCode").GetString(), request!.ShortCode);
        return request;
    }

    /// <summary>The phone side of the six-digit fallback: it claims the request over the network and lands
    /// in "awaiting local confirmation", which is the state the operator's 确认配对 button acts on.</summary>
    private async Task<PhoneSession> ClaimByShortCodeAsync(PairingRequest request, string deviceName, bool overHttps = false)
    {
        var phone = new LanDiskClient(overHttps ? Host.NewHttpsClient() : Host.NewClient());
        ApiResult<JsonElement> claimed = await phone.PostAsync<JsonElement>("/api/pair/code",
            new { shortCode = request.ShortCode, deviceName });

        Assert.True(claimed.IsSuccess, claimed.Raw);
        Assert.Equal("awaiting_local_confirmation", claimed.Body.GetProperty("status").GetString());
        Assert.Equal(request.Id, claimed.Body.GetProperty("requestId").GetString());
        Assert.Equal(PairingState.AwaitingLocalConfirmation, Host.Runtime.Pairing.Find(request.Id)!.State);

        return new PhoneSession(phone, request.Id, claimed.Body.GetProperty("claimSecret").GetString()!);
    }

    /// <summary>Raw HTTP against the hand-rolled bootstrap listener, to prove it is up or down.</summary>
    private static async Task<HttpResponseMessage?> TryFetchBootstrapAsync(string url)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            return await client.GetAsync(url);
        }
        catch (HttpRequestException)
        {
            return null;   // connection refused / reset: the listener is gone
        }
        catch (TaskCanceledException)
        {
            return null;
        }
    }

    private sealed record PhoneSession(LanDiskClient Phone, string RequestId, string ClaimSecret)
    {
        public async Task<JsonElement> PollAsync()
        {
            ApiResult<JsonElement> status = await Phone.PostAsync<JsonElement>("/api/pair/status",
                new { requestId = RequestId, claimSecret = ClaimSecret });
            Assert.True(status.IsSuccess, status.Raw);
            return status.Body;
        }
    }

    // ------------------------------------------------------------------ REPAIR09-01 / 02

    [Fact]
    public async Task Confirming_a_phone_closes_the_wizard_without_any_second_click()
    {
        OnboardingService onboarding = StartOnboarding();
        string bootstrapId = onboarding.BootstrapId!;

        PairingRequest request = await CreateRequestAsync();
        PhoneSession phone = await ClaimByShortCodeAsync(request, "自动收口手机");
        Assert.True(onboarding.IsActive);   // still open while the operator decides

        OperationOutcome<DeviceDefinition> outcome = Native.ConfirmPairing(request.Id);

        Assert.True(outcome.Ok, outcome.Message);
        DeviceDefinition device = outcome.Value!;
        Assert.Equal("自动收口手机", device.Name);
        Assert.Contains(Host.Runtime.Devices.All(), d => d.Id == device.Id);
        Assert.Equal(device.Id, Host.Runtime.Pairing.Find(request.Id)!.IssuedDeviceId);

        // The wizard closed itself, and the phone's own poll confirms it was paired.
        Assert.False(onboarding.IsActive);
        Assert.Null(onboarding.BootstrapId);
        Assert.Null(onboarding.ExpiresAt);
        Assert.False(string.IsNullOrEmpty(bootstrapId));

        JsonElement poll = await phone.PollAsync();
        Assert.Equal("paired", poll.GetProperty("status").GetString());
        Assert.Equal(device.Id, poll.GetProperty("device").GetProperty("id").GetString());

        TestBase.Record("REPAIR09-01",
            "ConfirmPairing 成功后不需要任何第二次点击：设备已落库、请求指向该设备，Onboarding 自动停止（IsActive=false、BootstrapId/过期时间清空），手机轮询立刻拿到 paired");
    }

    [Fact]
    public async Task The_8890_listener_is_really_gone_after_the_confirmation()
    {
        OnboardingService onboarding = StartOnboarding();
        int port = onboarding.Port;
        string url = onboarding.Status().BootstrapUrl!;

        // It is not merely recorded as listening: it answers.
        HttpResponseMessage? before = await TryFetchBootstrapAsync(url);
        Assert.NotNull(before);
        Assert.Equal(HttpStatusCode.OK, before!.StatusCode);
        string served = await before.Content.ReadAsStringAsync();
        Assert.Contains("LanDisk Local CA", served, StringComparison.Ordinal);
        Assert.Contains(port.ToString(), url, StringComparison.Ordinal);

        PairingRequest request = await CreateRequestAsync();
        await ClaimByShortCodeAsync(request, "8890 收口手机");
        Assert.True(Native.ConfirmPairing(request.Id).Ok);

        // Now nothing answers on that port any more.
        Assert.False(onboarding.IsActive);
        Assert.Empty(onboarding.Listeners);
        Assert.Null(await TryFetchBootstrapAsync(url));

        // And the port itself is free again: if the socket were still open this bind would fail.
        var rebind = new TcpListener(IPAddress.Any, port);
        rebind.Start();
        rebind.Stop();

        TestBase.Record("REPAIR09-02",
            $"确认成功后 8890 引导监听真正消失：确认前 GET {url} 返回 200 且含 CA 页面，确认后同一地址连接被拒绝（无响应），端口 {port} 可以重新绑定");
    }

    [Fact]
    public async Task A_refused_confirmation_never_closes_the_wizard()
    {
        OnboardingService onboarding = StartOnboarding();
        string? bootstrapId = onboarding.BootstrapId;

        // Unknown id.
        Assert.False(Native.ConfirmPairing("no-such-request").Ok);
        Assert.True(onboarding.IsActive);
        Assert.Equal(bootstrapId, onboarding.BootstrapId);

        // Known id, wrong state: the phone has not claimed it yet.
        PairingRequest untouched = await CreateRequestAsync();
        OperationOutcome<DeviceDefinition> wrongState = Native.ConfirmPairing(untouched.Id);
        Assert.False(wrongState.Ok);
        Assert.Equal("PAIRING_STATE", wrongState.Code);
        Assert.True(onboarding.IsActive);
        Assert.Equal(bootstrapId, onboarding.BootstrapId);

        // Expired request.
        PairingRequest expired = await CreateRequestAsync();
        await ClaimByShortCodeAsync(expired, "过期手机");
        typeof(PairingRequest).GetProperty(nameof(PairingRequest.ExpiresAt))!
            .SetValue(expired, DateTimeOffset.Now.AddMinutes(-1));
        OperationOutcome<DeviceDefinition> tooLate = Native.ConfirmPairing(expired.Id);
        Assert.False(tooLate.Ok);
        Assert.Equal("PAIRING_EXPIRED", tooLate.Code);
        Assert.True(onboarding.IsActive);
        Assert.Equal(bootstrapId, onboarding.BootstrapId);

        // No device was invented by any of the three refusals, and the wizard kept its identity.
        Assert.Empty(Host.Runtime.Devices.All());

        onboarding.Stop("测试结束");
        TestBase.Record("REPAIR09-01b",
            "三种被拒绝的确认（不存在 / 状态不符 / 已过期）都不会关闭向导：IsActive、BootstrapId 不变，也没有产生任何设备");
    }

    // ------------------------------------------------------------------ REPAIR09-03

    [Fact]
    public async Task An_existing_device_does_not_make_a_new_wizard_look_completed()
    {
        // iPhone A is already paired (the token path issues a device without any local confirmation).
        string tokenA = await Admin.CreatePairingTokenAsync();
        var phoneA = new LanDiskClient(Host.NewClient());
        JsonElement redeemedA = await phoneA.RedeemAsync(tokenA, "已有的 iPhone A");
        string deviceAId = redeemedA.GetProperty("id").GetString()!;

        OnboardingService onboarding = StartOnboarding();

        // B's wizard is open; B has claimed and is waiting for the operator.
        PairingRequest requestB = await CreateRequestAsync();
        PhoneSession phoneB = await ClaimByShortCodeAsync(requestB, "新的 iPhone B");

        // The old rule — "some unrevoked device exists, so we are done" — is true here. The wizard is not.
        Assert.Contains(Host.Runtime.Devices.All(), d => !d.Revoked);
        AdminOperations.PairingCompletion before = Native.CompletionOf(requestB.Id);
        Assert.False(before.Completed);
        Assert.Null(before.DeviceId);
        Assert.True(onboarding.IsActive);

        // Confirming B (through the browser endpoint — the same shared method) completes B and only B.
        ApiResult<JsonElement> confirmed = await Admin.PostAsync<JsonElement>($"/api/admin/pairing/{requestB.Id}/confirm", new { });
        Assert.True(confirmed.IsSuccess, confirmed.Raw);

        AdminOperations.PairingCompletion after = Native.CompletionOf(requestB.Id);
        Assert.True(after.Completed);
        Assert.Equal("新的 iPhone B", after.DeviceName);
        Assert.NotEqual(deviceAId, after.DeviceId);
        Assert.Equal("ReadOnly", after.Level);

        // A is untouched, both devices exist, and the wizard closed.
        DeviceDefinition reloadedA = Host.Runtime.Devices.Find(deviceAId)!;
        Assert.False(reloadedA.Revoked);
        Assert.Equal("已有的 iPhone A", reloadedA.Name);
        Assert.Equal(2, Host.Runtime.Devices.All().Count);
        Assert.False(onboarding.IsActive);

        Assert.Equal("paired", (await phoneB.PollAsync()).GetProperty("status").GetString());

        TestBase.Record("REPAIR09-03",
            "已有 Device-A 不会把 Device-B 的向导显示成已完成：即使“存在未撤销设备”为真，CompletionOf(B) 在 B 确认前仍为 false；B 经 HTTP 端点确认后只有 B 完成，A 的名称/权限/未撤销状态不变");
    }

    // ------------------------------------------------------------------ REPAIR09-04 / 05

    [Fact]
    public async Task Entering_the_page_with_no_wizard_starts_one_and_it_really_serves()
    {
        Assert.False(Host.Runtime.Onboarding.IsActive);

        OnboardingStatus started = Native.EnsureOnboardingStarted();

        Assert.True(started.Active);
        Assert.NotNull(started.BootstrapId);
        Assert.NotEmpty(started.Listeners);
        Assert.NotNull(started.BootstrapUrl);

        // One click means the phone can scan immediately: the freshly started wizard answers.
        HttpResponseMessage? response = await TryFetchBootstrapAsync(started.BootstrapUrl!);
        Assert.NotNull(response);
        Assert.Equal(HttpStatusCode.OK, response!.StatusCode);
        Assert.Contains(started.BootstrapId!, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        Host.Runtime.Onboarding.Stop("测试结束");
        TestBase.Record("REPAIR09-04",
            "没有向导时打开“连接 iPhone”自动开启一次：EnsureOnboardingStarted 返回 Active、生成 BootstrapId 与监听，且新向导的引导地址立即可访问（200 且页面含该 BootstrapId）");
    }

    [Fact]
    public void Repeated_ensure_calls_never_restart_the_wizard_or_change_its_id()
    {
        OnboardingStatus first = Native.EnsureOnboardingStarted();
        string firstId = first.BootstrapId!;
        IReadOnlyList<string> firstListeners = Host.Runtime.Onboarding.Listeners;
        string firstUrl = first.BootstrapUrl!;

        // OnShown, the 1.5 s watch timer and the explicit 刷新 button all end up here.
        for (int i = 0; i < 6; i++)
        {
            OnboardingStatus again = Native.EnsureOnboardingStarted();
            Assert.True(again.Active);
            Assert.Equal(firstId, again.BootstrapId);
            Assert.Equal(firstUrl, again.BootstrapUrl);
        }

        Assert.Equal(firstListeners, Host.Runtime.Onboarding.Listeners);
        Assert.Equal(firstId, Host.Runtime.Onboarding.BootstrapId);

        Host.Runtime.Onboarding.Stop("测试结束");
        TestBase.Record("REPAIR09-05",
            "重复 EnsureOnboardingStarted（等价于多次 OnShown / 1.5 秒刷新 / 手动刷新）不会重启向导：BootstrapId、BootstrapUrl 与监听列表都不变");
    }

    [Fact]
    public async Task A_wizard_that_already_reached_the_pairing_step_is_left_alone()
    {
        // The host closes the plain-HTTP window as soon as the phone has trusted the CA (its job is done),
        // while the pairing request is still live. Re-opening the page must not reset that flow: a new
        // wizard would clear Trusted and hand the phone a bootstrap id it is not looking at.
        OnboardingService onboarding = StartOnboarding();
        PairingRequest live = await CreateRequestAsync();
        await ClaimByShortCodeAsync(live, "已经完成信任的手机");

        onboarding.MarkTrusted("192.168.2.44");
        onboarding.Stop("手机已完成信任");
        Assert.False(onboarding.IsActive);
        Assert.True(onboarding.Status().Trusted);

        OnboardingStatus ensured = Native.EnsureOnboardingStarted();
        Assert.False(ensured.Active);
        Assert.Null(ensured.BootstrapId);
        Assert.True(ensured.Trusted);
        Assert.Empty(onboarding.Listeners);

        // Confirming the flow that was already in flight still works and still closes cleanly.
        Assert.True(Native.ConfirmPairing(live.Id).Ok);
        Assert.False(onboarding.IsActive);
        Assert.Single(Host.Runtime.Devices.All());

        // The explicit recovery path is untouched: "重新生成引导二维码" still opens a fresh wizard.
        OnboardingStatus manual = Host.Runtime.Onboarding.Start();
        Assert.True(manual.Active);
        Assert.False(manual.Trusted);
        Assert.NotNull(manual.BootstrapId);

        Host.Runtime.Onboarding.Stop("测试结束");
        TestBase.Record("REPAIR09-05b",
            "自动开启不会打断已进行中的配对流程：手机已完成信任且请求 live 时 EnsureOnboardingStarted 不重启（Trusted 与 BootstrapId 保持），并且“重新生成引导二维码”的手动恢复入口仍然可用");
    }

    // ------------------------------------------------------------------ REPAIR09-06

    [Fact]
    public async Task Rejecting_leaves_the_wizard_open_and_the_user_can_retry()
    {
        OnboardingService onboarding = StartOnboarding();
        PairingRequest refused = await CreateRequestAsync();
        await ClaimByShortCodeAsync(refused, "被拒绝的手机");

        // Rejected through the browser endpoint: the same shared implementation the native button calls.
        ApiResult<JsonElement> rejected = await Admin.DeleteAsync<JsonElement>($"/api/admin/pairing/{refused.Id}");
        Assert.True(rejected.IsSuccess, rejected.Raw);

        Assert.False(Native.CompletionOf(refused.Id).Completed);
        Assert.Equal(PairingState.Cancelled, Host.Runtime.Pairing.Find(refused.Id)!.State);
        Assert.True(onboarding.IsActive);
        Assert.Empty(Host.Runtime.Devices.All());

        // The operator simply tries again with a fresh request, and the wizard is still the same one.
        PairingRequest retry = await CreateRequestAsync();
        PhoneSession phone = await ClaimByShortCodeAsync(retry, "重试的手机");
        Assert.True(Native.ConfirmPairing(retry.Id).Ok);

        AdminOperations.PairingCompletion done = Native.CompletionOf(retry.Id);
        Assert.True(done.Completed);
        Assert.Equal("重试的手机", done.DeviceName);
        Assert.Equal("paired", (await phone.PollAsync()).GetProperty("status").GetString());
        Assert.False(onboarding.IsActive);
        Assert.Single(Host.Runtime.Devices.All());

        TestBase.Record("REPAIR09-06",
            "拒绝不会把向导标成完成（CompletionOf=false、请求置为 Cancelled、向导仍 Active、没有设备）；用新请求重试可以成功并正常收口");
    }

    // ------------------------------------------------------------------ REPAIR09-07

    [Fact]
    public async Task Https_keeps_serving_after_the_wizard_closes()
    {
        OnboardingService onboarding = StartOnboarding();
        string bootstrapId = onboarding.BootstrapId!;

        // This phone does its whole pairing over HTTPS, which is the real path.
        PairingRequest request = await CreateRequestAsync();
        PhoneSession phone = await ClaimByShortCodeAsync(request, "HTTPS 存活手机", overHttps: true);

        Assert.True(Native.ConfirmPairing(request.Id).Ok);
        Assert.False(onboarding.IsActive);
        Assert.Null(onboarding.BootstrapId);
        Assert.False(string.IsNullOrEmpty(bootstrapId));

        // The HTTPS listener is still up, and the session the phone just earned still works on it.
        JsonElement poll = await phone.PollAsync();
        Assert.Equal("paired", poll.GetProperty("status").GetString());
        string deviceId = poll.GetProperty("device").GetProperty("id").GetString()!;

        ApiResult<JsonElement> me = await phone.Phone.GetAsync<JsonElement>("/api/me");
        Assert.True(me.IsSuccess, me.Raw);
        Assert.Equal("device", me.Body.GetProperty("kind").GetString());

        ApiResult<JsonElement> health = await phone.Phone.GetAsync<JsonElement>("/api/health");
        Assert.True(health.IsSuccess, health.Raw);

        // Reading a share over the same surviving listener is the end-to-end proof.
        ApiResult<JsonElement> browse = await phone.Phone.BrowseAsync(ShareId);
        Assert.True(browse.IsSuccess, browse.Raw);
        Assert.True(browse.Body.TryGetProperty("entries", out _) || browse.Body.TryGetProperty("items", out _));

        // The paired device is intact and the pinned ports and the stable name are unchanged.
        DeviceDefinition device = Host.Runtime.Devices.Find(deviceId)!;
        Assert.Equal("HTTPS 存活手机", device.Name);
        Assert.False(device.Revoked);
        Assert.Equal(8892, new AppSettings().HttpsPort);
        Assert.Equal(8891, new AppSettings().HttpPort);
        Assert.Equal(8890, OnboardingService.DefaultBootstrapPort);
        Assert.StartsWith("landisk-", Host.Runtime.Identity.HostName, StringComparison.Ordinal);
        Assert.EndsWith(".local", Host.Runtime.Identity.HostName, StringComparison.Ordinal);
        Assert.NotEmpty(Host.HttpsBaseUrl);
        Assert.Equal("https://" + Host.Runtime.Identity.HostName + ":" + Host.Runtime.Settings.HttpsPort + "/",
            onboarding.Status().HttpsUrl);

        TestBase.Record("REPAIR09-07",
            "关闭引导页后 HTTPS 仍正常：/api/health 与 /api/me 可用、已配对设备会话有效、共享列表经 HTTPS 可读；8891/8892/8890 端口常量与稳定 landisk-*.local 名称不变");
    }

    // ------------------------------------------------------------------ REPAIR09-08

    [Fact]
    public async Task The_plain_http_bootstrap_page_never_carries_a_secret()
    {
        OnboardingService onboarding = StartOnboarding();
        PairingRequest request = await CreateRequestAsync();
        PhoneSession phone = await ClaimByShortCodeAsync(request, "不泄密的手机");

        string page = onboarding.BootstrapPage();
        HttpResponseMessage live = (await TryFetchBootstrapAsync(onboarding.Status().BootstrapUrl!))!;
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        string served = await live.Content.ReadAsStringAsync();
        live.Dispose();

        foreach (string body in new[] { page, served })
        {
            Assert.DoesNotContain("#t=", body, StringComparison.Ordinal);
            Assert.DoesNotContain("pairingToken", body, StringComparison.Ordinal);
            Assert.DoesNotContain("token=", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("cookie", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("csrf", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("admin", body, StringComparison.OrdinalIgnoreCase);
        }

        // None of the live credentials of this very wizard appear in what the plain page serves.
        Assert.DoesNotContain(request.Token, served, StringComparison.Ordinal);
        Assert.DoesNotContain(request.ShortCode, served, StringComparison.Ordinal);
        Assert.DoesNotContain(phone.ClaimSecret, served, StringComparison.Ordinal);
        Assert.DoesNotContain(Host.Runtime.AdminBootstrapToken, served, StringComparison.Ordinal);

        // The bootstrap id is the one thing that *does* appear, and only because the page has to name its
        // own session in the probe URL. It is not a credential: it grants nothing on its own.
        Assert.Contains(onboarding.BootstrapId!, served, StringComparison.Ordinal);

        // What the page *is* allowed to carry: the public CA and the trust probe.
        Assert.Contains("landisk-ca.cer", served, StringComparison.Ordinal);
        Assert.Contains("trust-probe", served, StringComparison.Ordinal);
        Assert.Contains("完全信任", served, StringComparison.Ordinal);

        // The one-time pairing token only ever travels over a path that already has TLS, and never over the
        // plain-HTTP bootstrap listener. The loopback entry is the local management page on this machine
        // (127.0.0.1 only, never on the network); every LAN-reachable entry must be https.
        IReadOnlyList<string> urls = Native.PairingUrls(request.Token);
        Assert.NotEmpty(urls);
        Assert.All(urls, u =>
        {
            Assert.Contains("#t=" + request.Token, u, StringComparison.Ordinal);
            Assert.DoesNotContain(":" + onboarding.Port + "/", u, StringComparison.Ordinal);
        });
        Assert.All(urls.Where(u => !u.Contains("127.0.0.1", StringComparison.Ordinal)
                                   && !u.Contains("[::1]", StringComparison.Ordinal)),
            u => Assert.StartsWith("https://", u, StringComparison.Ordinal));
        Assert.All(urls.Where(u => u.Contains("127.0.0.1", StringComparison.Ordinal)),
            u => Assert.StartsWith("http://127.0.0.1:", u, StringComparison.Ordinal));

        onboarding.Stop("测试结束");
        TestBase.Record("REPAIR09-08",
            "明文引导页（内存渲染版与实际 HTTP 返回版都核对）不含令牌/claimSecret/管理令牌/cookie/csrf/管理入口，只含公开 CA、完全信任步骤与 trust 探测；一次性配对令牌不出现在任何指向 8890 的链接里，非回环链接一律 https，回环链接只指向 127.0.0.1");
    }

    [Fact]
    public void Pairing_links_for_the_network_are_https_and_never_touch_the_bootstrap_listener()
    {
        string path = Path.Combine(TestPaths.RepoRoot, "src/LanDisk.Server/Services/AdminOperations.cs");
        string text = StripComments(File.ReadAllText(path));

        int start = text.IndexOf("public IReadOnlyList<string> PairingUrls(", StringComparison.Ordinal);
        int end = text.IndexOf("private void Audit(", start, StringComparison.Ordinal);
        Assert.True(start > 0 && end > start, "找不到 PairingUrls");
        string body = text[start..end];

        // A LAN candidate is offered as https when the https listener is up, and only as the documented
        // plain-HTTP fallback otherwise — the order in the source is the order of preference.
        int httpsCheck = body.IndexOf("Listening(\"https://\", candidate.Address)", StringComparison.Ordinal);
        int httpsUrl = body.IndexOf("https://{candidate.Address}", StringComparison.Ordinal);
        int httpCheck = body.IndexOf("Listening(\"http://\", candidate.Address)", StringComparison.Ordinal);
        int httpUrl = body.IndexOf("http://{candidate.Address}", StringComparison.Ordinal);
        Assert.True(httpsCheck > 0 && httpsUrl > httpsCheck && httpCheck > httpsUrl && httpUrl > httpCheck,
            "局域网配对链接必须先尝试 https，再退回到 http 兼容模式");

        // The token is only ever placed after the URL fragment.
        Assert.Contains("/pair#t={token}", body, StringComparison.Ordinal);
        Assert.DoesNotContain("/pair?t=", body, StringComparison.Ordinal);

        // REPAIR_02 widened the rule this gate encodes: the links are now ordered by the host a phone was
        // measured to reach, so this method reads exactly one thing from the wizard — that hint — and still
        // has no way to name the bootstrap listener, its port or its URL. A pairing link can therefore
        // never point at the plain-HTTP 8890 window.
        Assert.Single(Regex.Matches(body, "_runtime\\.Onboarding\\."));
        Assert.Contains("_runtime.Onboarding.VerifiedHttpsHost", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Onboarding.Start", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Onboarding.Stop", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Onboarding.Port", body, StringComparison.Ordinal);
        Assert.DoesNotContain("BootstrapUrl", body, StringComparison.Ordinal);
        Assert.DoesNotContain("DefaultBootstrapPort", body, StringComparison.Ordinal);

        TestBase.Record("REPAIR09-08b",
            "源码闸门：PairingUrls 对局域网地址先给出 https（仅在 https 未监听时才退回 http），一次性令牌只放在 /pair#t= 片段里；该方法从向导只读取「已验证可达 host」这一个成员，仍不引用引导页监听地址或 8890 端口，配对链接不可能指向明文引导页");
    }

    // ------------------------------------------------------------------ source-level gates

    /// <summary>
    /// The two repairs that live in the WPF window cannot be executed from this project (it is a WinExe and
    /// is not referenced by the test project), so the same source-level technique the 09 gates use is
    /// applied: the buggy expressions must be absent, and the fixed call must be present in the right place.
    /// This proves the code path is gone, not that a click was observed — REPAIR09-01..08 above carry the
    /// behavioural evidence.
    /// </summary>
    [Fact]
    public void The_window_no_longer_judges_completion_by_the_device_list()
    {
        string pages = AppSource("Pages.cs");

        // Scope to the page that had the bug: the devices page legitimately talks about revoked devices.
        int iphone = pages.IndexOf("internal sealed class IphonePage", StringComparison.Ordinal);
        Assert.True(iphone > 0, "找不到 IphonePage");
        int nextClass = pages.IndexOf("internal sealed class ", iphone + 1, StringComparison.Ordinal);
        Assert.True(nextClass > iphone, "找不到 IphonePage 的类边界");
        string page = pages[iphone..nextClass];

        // The exact old rule, gone.
        Assert.DoesNotContain("Devices.All().Any(", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Devices.All()", page, StringComparison.Ordinal);

        // Completion now comes from the one request this page produced, and the page auto-starts the wizard.
        Assert.Contains("CompletionOf(", page, StringComparison.Ordinal);
        Assert.Contains("_pairingId", page, StringComparison.Ordinal);

        int onShown = page.IndexOf("public override void OnShown()", StringComparison.Ordinal);
        Assert.True(onShown > 0, "找不到 IphonePage.OnShown");
        int ensureCall = page.IndexOf("EnsureOnboardingStarted()", onShown, StringComparison.Ordinal);
        Assert.True(ensureCall > onShown, "IphonePage.OnShown 必须在进入页面时调用 EnsureOnboardingStarted");

        // It is that call, not a raw Onboarding.Start(), that OnShown makes.
        int rawStart = page.IndexOf("Onboarding.Start(", onShown, StringComparison.Ordinal);
        int ensureEnd = onShown + 400;
        Assert.True(rawStart < 0 || rawStart > ensureEnd, "OnShown 不应自己调用 Onboarding.Start");

        // The recovery and cancel affordances are still there, and the manual "close the wizard" button
        // the user used to have to find is gone from the whole window.
        Assert.Contains("重新生成引导二维码", page, StringComparison.Ordinal);
        Assert.Contains("结束向导", page, StringComparison.Ordinal);
        foreach (string file in AppSources())
        {
            Assert.DoesNotContain("关闭向导并停止引导页", StripComments(File.ReadAllText(file)), StringComparison.Ordinal);
        }

        TestBase.Record("REPAIR09-04b",
            "外壳源码闸门：IphonePage 不再出现 Devices.All()（旧规则是 Any(d => !d.Revoked)），完成判断改为 CompletionOf(本次 _pairingId)；OnShown 调用 EnsureOnboardingStarted() 而不是自己 Onboarding.Start()；“重新生成引导二维码”和“结束向导”都保留，“关闭向导并停止引导页”按钮已从窗口删除");
    }

    [Fact]
    public void The_stop_is_wired_after_the_device_is_persisted_and_only_there()
    {
        string path = Path.Combine(TestPaths.RepoRoot, "src/LanDisk.Server/Services/AdminOperations.cs");
        string text = StripComments(File.ReadAllText(path));

        int confirm = text.IndexOf("public OperationOutcome<DeviceDefinition> ConfirmPairing", StringComparison.Ordinal);
        Assert.True(confirm > 0, "找不到 ConfirmPairing");
        int next = text.IndexOf("public OnboardingStatus EnsureOnboardingStarted(", confirm, StringComparison.Ordinal);
        Assert.True(next > confirm, "找不到 ConfirmPairing 之后的方法边界");
        string body = text[confirm..next];

        int persisted = body.IndexOf("_runtime.Devices.Add(device)", StringComparison.Ordinal);
        int issued = body.IndexOf("pairing.IssuedDeviceId = device.Id", StringComparison.Ordinal);
        int stop = body.IndexOf("_runtime.Onboarding.Stop(", StringComparison.Ordinal);

        Assert.True(persisted > 0 && issued > persisted && stop > issued,
            "停止引导页必须排在“设备已持久化 + 请求已指向该设备”之后");
        Assert.Contains("_runtime.Onboarding.IsActive", body[..stop], StringComparison.Ordinal);

        // It is the shared layer that closes the wizard, and nothing in the HTTP endpoint does it separately.
        string endpoints = StripComments(File.ReadAllText(
            Path.Combine(TestPaths.RepoRoot, "src/LanDisk.Server/Endpoints/AdminEndpoints.cs")));
        Assert.Contains("runtime.Admin.ConfirmPairing(id)", endpoints, StringComparison.Ordinal);
        Assert.DoesNotContain("Onboarding.Stop(", endpoints, StringComparison.Ordinal);

        TestBase.Record("REPAIR09-01c",
            "服务端源码闸门：Onboarding.Stop 位于 ConfirmPairing 内、且在 Devices.Add 与 IssuedDeviceId 赋值之后；HTTP 端点不自行停止引导页，只调用共享的 ConfirmPairing");
    }

    [Fact]
    public void Ensure_only_starts_a_wizard_when_nothing_is_in_flight()
    {
        string path = Path.Combine(TestPaths.RepoRoot, "src/LanDisk.Server/Services/AdminOperations.cs");
        string text = StripComments(File.ReadAllText(path));

        int ensure = text.IndexOf("public OnboardingStatus EnsureOnboardingStarted(", StringComparison.Ordinal);
        int restart = text.IndexOf("public OnboardingStatus RestartOnboarding(", ensure, StringComparison.Ordinal);
        Assert.True(ensure > 0 && restart > ensure, "找不到 EnsureOnboardingStarted 的方法边界");
        string body = text[ensure..restart];

        Assert.Contains("status.Active", body, StringComparison.Ordinal);
        Assert.Contains("Pairing.Active().Count > 0", body, StringComparison.Ordinal);

        int activeCheck = body.IndexOf("status.Active", StringComparison.Ordinal);
        int inFlightCheck = body.IndexOf("Pairing.Active().Count > 0", StringComparison.Ordinal);
        int start = body.IndexOf("Onboarding.Start()", StringComparison.Ordinal);
        Assert.True(activeCheck < inFlightCheck && inFlightCheck < start,
            "必须先判断“已在运行”，再判断“有正在进行的配对流程”，最后才允许开始新向导");

        // Exactly one Start() call inside the ordinary path. REPAIR_03 added a *separate*, explicit
        // RestartOnboarding() for the recovery button (asserted below and by REPAIR03-07/10), which is why
        // this gate is now scoped to this method instead of everything up to CompletionOf.
        Assert.Equal(1, body.Split("Onboarding.Start()").Length - 1);

        // The explicit path is a different method and must do the cleanup *before* it starts anything:
        // release the listener, drop the hint, cancel what is in flight, audit, and only then Start.
        int completion = text.IndexOf("public PairingCompletion CompletionOf(", restart, StringComparison.Ordinal);
        Assert.True(completion > restart);
        string explicitBody = text[restart..completion];
        int stopAt = explicitBody.IndexOf("Onboarding.Stop(", StringComparison.Ordinal);
        int clearAt = explicitBody.IndexOf("ClearVerifiedHttpsHost(", StringComparison.Ordinal);
        int cancelAt = explicitBody.IndexOf("Pairing.Cancel(", StringComparison.Ordinal);
        int auditAt = explicitBody.IndexOf("restart-onboarding", StringComparison.Ordinal);
        int explicitStart = explicitBody.IndexOf("Onboarding.Start()", StringComparison.Ordinal);
        Assert.True(stopAt > 0 && clearAt > stopAt && cancelAt > clearAt && auditAt > cancelAt && explicitStart > auditAt,
            "显式重启必须按 停止监听 → 清提示 → 取消在途请求 → 写审计 → 开启新会话 的顺序执行");
        Assert.Equal(1, explicitBody.Split("Onboarding.Start()").Length - 1);

        TestBase.Record("REPAIR09-05c",
            "服务端源码闸门：EnsureOnboardingStarted 先返回已在运行的向导、再对“有 live 配对请求”直接返回，最后只有一处 Onboarding.Start()；显式恢复路径 RestartOnboarding 另立一处，且顺序为 停止监听 → 清提示 → 取消在途请求 → 写审计 → 开启新会话");
    }

    // ---- helpers reused from the 09 source gates ---------------------------------

    private static string AppSource(string fileName)
    {
        string path = Path.Combine(TestPaths.RepoRoot, "src/LanDisk.App", fileName);
        Assert.True(File.Exists(path), "找不到 " + path);
        return StripComments(File.ReadAllText(path));
    }

    private static IReadOnlyList<string> AppSources()
    {
        string root = Path.Combine(TestPaths.RepoRoot, "src/LanDisk.App");
        return [.. Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))];
    }

    /// <summary>The file's code with whole-line comments removed, so a gate does not trip over the comment
    /// that explains the bug it guards against.</summary>
    private static string StripComments(string text)
    {
        var kept = new List<string>();
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue;
            if (trimmed.StartsWith("*", StringComparison.Ordinal) && !trimmed.StartsWith("*/", StringComparison.Ordinal)) continue;
            if (trimmed.StartsWith("/*", StringComparison.Ordinal) && !trimmed.Contains("*/", StringComparison.Ordinal)) continue;
            kept.Add(line);
        }
        return string.Join('\n', kept);
    }
}
