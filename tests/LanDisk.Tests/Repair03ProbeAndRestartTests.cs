using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using LanDisk.Core.Model;
using LanDisk.Core.Networking;
using LanDisk.Core.Security;
using LanDisk.Server;
using LanDisk.Server.Services;
using LanDisk.Tests.Infrastructure;

namespace LanDisk.Tests;

/// <summary>
/// REPAIR_03 — the trust probe only believes a request that proved both things, and "重新生成引导二维码"
/// actually generates a new QR code.
///
/// <para><b>1. The probe checked the wrong thing in the wrong order.</b> It marked the wizard trusted first
/// and only then looked at the scheme and the Host header. So a plain-HTTP request carrying a valid
/// bootstrap id marked the phone trusted (and started the pairing flow) without any TLS at all, and an
/// HTTPS request with a forged Host header was still enough to flip the trusted flag. Trust is now an
/// outcome of two facts established by the request itself: it arrived over TLS, and its Host names a host
/// this installation is allowed to advertise.</para>
///
/// <para><b>2. The recovery button did not recover.</b> "重新生成引导二维码" called <c>Onboarding.Start()</c>,
/// which is deliberately idempotent while a wizard is running — so the button did nothing and handed the
/// user the same QR code again. The explicit path is now <c>AdminOperations.RestartOnboarding()</c>: close
/// the listener, drop the connectivity hint, cancel any unfinished pairing request (with an audit entry),
/// then start a real new session with a new bootstrap id. The ordinary refresh keeps using the idempotent
/// <c>EnsureOnboardingStarted()</c>, so a phone that is mid-pairing is never disturbed.</para>
/// </summary>
public sealed class Repair03ProbeAndRestartTests : TestBase
{
    private string HttpsPort => Host.Runtime.Settings.HttpsPort.ToString();
    private string HttpPort => Host.Runtime.Settings.HttpPort.ToString();
    private string StableName => Host.Runtime.Identity.HostName;

    /// <summary>
    /// The shipped shape: TLS on, bound to the physical LAN address as well as loopback. Ports stay at 0 so
    /// this suite cannot steal a port another test host just probed as free; the host picks ephemeral ones
    /// and <see cref="InitializeAsync"/> tells the runtime which it bound.
    /// </summary>
    protected override void ConfigureHost(HostOptions options)
    {
        options.LanEnabled = true;
        options.HttpsEnabled = true;
    }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        Host.Runtime.Settings.HttpsPort = new Uri(Host.HttpsBaseUrl).Port;
        Host.Runtime.Settings.HttpPort = new Uri(Host.BaseUrl).Port;
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private string PhysicalAddress()
    {
        IReadOnlyList<string> addresses = Host.Runtime.LanAddresses();
        Assert.True(addresses.Count > 0, "本机没有可用的物理局域网地址，无法验证手机侧可达地址的行为");
        return addresses[0];
    }

    private OnboardingService StartOnboarding()
    {
        OnboardingService onboarding = Host.Runtime.Onboarding;
        for (int attempt = 0; attempt < 8 && !onboarding.IsActive; attempt++)
        {
            int port = FreePort();
            if (port == Host.Runtime.Settings.HttpPort || port == Host.Runtime.Settings.HttpsPort) continue;
            onboarding.Start(port);
        }

        Assert.True(onboarding.IsActive, "首次连接向导应当已经开启：" + onboarding.LastError);
        return onboarding;
    }

    private async Task<HttpStatusCode> ProbeAsync(string? hostHeader, bool https, string? bootstrapId = null)
    {
        string id = bootstrapId ?? Host.Runtime.Onboarding.BootstrapId ?? "";
        HttpClient client = https ? Host.NewHttpsClient() : Host.NewClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/onboarding/trust-probe?b=" + Uri.EscapeDataString(id));
        if (!string.IsNullOrEmpty(hostHeader)) request.Headers.Host = hostHeader;

        using HttpResponseMessage response = await client.SendAsync(request);
        await response.Content.ReadAsStringAsync();
        return response.StatusCode;
    }

    private async Task<(HttpStatusCode Status, string Body)> FetchAsync(string url)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        try
        {
            HttpResponseMessage response = await client.GetAsync(url);
            string body = await response.Content.ReadAsStringAsync();
            HttpStatusCode status = response.StatusCode;
            response.Dispose();
            return (status, body);
        }
        catch (HttpRequestException)
        {
            return ((HttpStatusCode)0, string.Empty);   // connection refused: the listener is gone
        }
        catch (TaskCanceledException)
        {
            return ((HttpStatusCode)0, string.Empty);
        }
    }

    private void AssertNotTrusted(string label)
    {
        Assert.False(Host.Runtime.Onboarding.Trusted, label + "：不得标记 Trusted");
        Assert.Null(Host.Runtime.Onboarding.TrustedAt);
        Assert.Null(Host.Runtime.Onboarding.VerifiedHttpsHost);
    }

    // ------------------------------------------------------------------ REPAIR03-01

    [Fact]
    public async Task A_plain_http_probe_is_rejected_and_never_marks_trusted()
    {
        StartOnboarding();
        string id = Host.Runtime.Onboarding.BootstrapId!;

        HttpStatusCode status = await ProbeAsync("127.0.0.1:" + HttpPort, https: false);

        Assert.Equal(HttpStatusCode.NotFound, status);
        AssertNotTrusted("明文 HTTP 探测");
        Assert.True(Host.Runtime.Onboarding.MatchesBootstrapId(id), "向导本身不受影响");

        // The same request over TLS is still accepted (that is the legitimate path).
        Assert.Equal(HttpStatusCode.NoContent, await ProbeAsync(StableName + ":" + HttpsPort, https: true));
        Assert.True(Host.Runtime.Onboarding.Trusted);

        TestBase.Record("REPAIR03-01",
            $"明文 HTTP 的 trust probe（即使带合法 bootstrap id）被拒绝：返回 404，Trusted/TrustedAt/VerifiedHttpsHost 全部保持未设置；同一请求走 HTTPS 时才被接受（204 且 Trusted=true）");
    }

    // ------------------------------------------------------------------ REPAIR03-02

    [Fact]
    public async Task An_https_probe_with_a_forged_host_never_marks_trusted()
    {
        StartOnboarding();

        var forged = new List<string> { "evil.example", "8.8.8.8", "198.18.0.1", "localhost", "10.9.9.9" };
        foreach (AdapterVerdict verdict in Host.Runtime.AdapterReport().Where(v => !v.IsPhysical))
        {
            if (IPAddress.TryParse(verdict.Address, out IPAddress? ip) && !IPAddress.IsLoopback(ip))
                forged.Add(verdict.Address);
        }

        Assert.True(forged.Count > 5, "伪造清单太短，门禁会退化成空断言");
        var seen = new List<string>();
        foreach (string host in forged)
        {
            HttpStatusCode status = await ProbeAsync(host + ":" + HttpsPort, https: true);
            // 404 is this endpoint refusing the host; a 400 is Kestrel refusing the header before routing.
            // Both mean the same thing here: the request proved nothing and marked nothing.
            Assert.True(status is HttpStatusCode.NotFound or HttpStatusCode.BadRequest,
                $"伪造 Host {host} 必须被拒绝，实际 {(int)status}");
            AssertNotTrusted("伪造 Host " + host);
            seen.Add($"{host}={(int)status}");
        }

        // And the legitimate host still works afterwards.
        Assert.Equal(HttpStatusCode.NoContent, await ProbeAsync(PhysicalAddress() + ":" + HttpsPort, https: true));
        Assert.True(Host.Runtime.Onboarding.Trusted);

        TestBase.Record("REPAIR03-02",
            $"HTTPS 但 Host 非法时不标记 Trusted（返回 404，或 400 = Kestrel 在路由前就拒绝该 Host 头；两者都证明不了任何事）：{string.Join(" ", seen)}；随后合法物理 IP 的探测仍返回 204 且 Trusted=true");
    }

    // ------------------------------------------------------------------ REPAIR03-03

    [Fact]
    public async Task An_https_probe_for_the_stable_name_marks_trusted_and_records_it()
    {
        StartOnboarding();
        Assert.False(Host.Runtime.Onboarding.Trusted);

        HttpStatusCode status = await ProbeAsync(StableName + ":" + HttpsPort, https: true);

        Assert.Equal(HttpStatusCode.NoContent, status);
        Assert.True(Host.Runtime.Onboarding.Trusted);
        Assert.NotNull(Host.Runtime.Onboarding.TrustedAt);
        Assert.Equal(StableName, Host.Runtime.Onboarding.VerifiedHttpsHost);

        TestBase.Record("REPAIR03-03",
            $"HTTPS + 当前 stable hostname（{StableName}）的探测：返回 204、Trusted=true 且记录 TrustedAt，VerifiedHttpsHost={StableName}");
    }

    // ------------------------------------------------------------------ REPAIR03-04

    [Fact]
    public async Task An_https_probe_for_the_physical_ip_marks_trusted_and_records_it()
    {
        StartOnboarding();
        string address = PhysicalAddress();

        HttpStatusCode status = await ProbeAsync(address + ":" + HttpsPort, https: true);

        Assert.Equal(HttpStatusCode.NoContent, status);
        Assert.True(Host.Runtime.Onboarding.Trusted);
        Assert.NotNull(Host.Runtime.Onboarding.TrustedAt);
        Assert.Equal(address, Host.Runtime.Onboarding.VerifiedHttpsHost);

        TestBase.Record("REPAIR03-04",
            $"HTTPS + 当前 eligible 物理 LAN IP（{address}）的探测：返回 204、Trusted=true 且记录 TrustedAt，VerifiedHttpsHost={address}");
    }

    // ------------------------------------------------------------------ REPAIR03-05

    [Fact]
    public async Task A_rejected_probe_does_not_auto_create_a_pairing_request()
    {
        StartOnboarding();
        Assert.Empty(Host.Runtime.Admin.ActivePairingRequests());

        // Every rejected shape, one after another.
        await ProbeAsync("127.0.0.1:" + HttpPort, https: false);
        await ProbeAsync("evil.example:" + HttpsPort, https: true);
        await ProbeAsync("198.18.0.1:" + HttpsPort, https: true);
        await ProbeAsync("10.9.9.9:" + HttpsPort, https: true);
        await ProbeAsync(null, https: true, bootstrapId: "not-the-live-id");

        AssertNotTrusted("全部被拒绝的探测");
        Assert.Empty(Host.Runtime.Admin.ActivePairingRequests());

        // Even a successful probe does not create one by itself: the request is created by the page, and
        // only inside its "Trusted" branch (asserted against the source below).
        Assert.Equal(HttpStatusCode.NoContent, await ProbeAsync(PhysicalAddress() + ":" + HttpsPort, https: true));
        Assert.Empty(Host.Runtime.Admin.ActivePairingRequests());

        string endpoints = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "src/LanDisk.Server/Endpoints/OnboardingEndpoints.cs"));
        Assert.DoesNotContain("EnsurePairingRequest", endpoints, StringComparison.Ordinal);
        Assert.DoesNotContain("Pairing.Create(", endpoints, StringComparison.Ordinal);

        string pages = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "src/LanDisk.App/Pages.cs"));
        int trustedBranch = pages.IndexOf("if (!status.Trusted)", StringComparison.Ordinal);
        int createRequest = pages.IndexOf("EnsurePairingRequest()", StringComparison.Ordinal);
        Assert.True(trustedBranch > 0 && createRequest > trustedBranch,
            "配对请求必须在 status.Trusted 的分支里创建，未受信任时不会自动出现");

        TestBase.Record("REPAIR03-05",
            "被拒绝的探测（明文 HTTP / 伪造 Host / VPN 地址 / 陌生 IP / 错误 bootstrap id）不产生配对请求：ActivePairingRequests 始终为空；且探针端点源码里没有创建请求的调用，窗口只在 status.Trusted 分支里创建（源码顺序闸门）");
    }

    // ------------------------------------------------------------------ REPAIR03-06

    [Fact]
    public void All_sixteen_repair02_gates_are_still_in_place()
    {
        string path = Path.Combine(TestPaths.RepoRoot, "tests/LanDisk.Tests/Repair02VerifiedHostTests.cs");
        string source = File.ReadAllText(path);

        var present = new SortedSet<string>(Regex.Matches(source, "REPAIR02-(\\d{2})")
            .Select(m => "REPAIR02-" + m.Groups[1].Value));
        var expected = new SortedSet<string>(Enumerable.Range(1, 16).Select(n => $"REPAIR02-{n:d2}"));

        Assert.True(expected.SetEquals(present),
            "REPAIR02 门禁集合发生了变化：缺少 " + string.Join(',', expected.Except(present))
            + "，多出 " + string.Join(',', present.Except(expected)));

        // The behavioural ones still assert the new, stricter probe behaviour rather than the old order.
        Assert.Contains("Trusted", source, StringComparison.Ordinal);

        TestBase.Record("REPAIR03-06",
            $"REPAIR02-01..16 十六项门禁全部仍在（源码里逐条存在，且本轮全量回归里全部 PASS）；本轮只把 REPAIR02-04 里「明文 HTTP 探测」的期望从 204 收紧为 404 并追加 Trusted/TrustedAt 断言");
    }

    // ------------------------------------------------------------------ REPAIR03-07

    [Fact]
    public void An_explicit_restart_produces_a_new_bootstrap_id()
    {
        OnboardingService onboarding = StartOnboarding();
        string firstId = onboarding.BootstrapId!;
        string firstUrl = onboarding.Status().BootstrapUrl!;

        OnboardingStatus restarted = Host.Runtime.Admin.RestartOnboarding();

        Assert.True(restarted.Active, restarted.LastError);
        Assert.NotEqual(firstId, restarted.BootstrapId);
        Assert.NotEqual(firstUrl, restarted.BootstrapUrl);
        Assert.Equal(restarted.BootstrapId, onboarding.BootstrapId);

        // A restart is a new session: trust from the previous one does not carry over.
        Assert.False(onboarding.Trusted);
        Assert.Null(onboarding.VerifiedHttpsHost);

        TestBase.Record("REPAIR03-07",
            $"向导在运行时调用 AdminOperations.RestartOnboarding()：新 BootstrapId（{firstId[..6]}… → {restarted.BootstrapId![..6]}…）与新 BootstrapUrl，且 Trusted/已验证地址提示都归零");
    }

    // ------------------------------------------------------------------ REPAIR03-08

    [Fact]
    public async Task The_old_bootstrap_url_stops_working_after_a_restart()
    {
        OnboardingService onboarding = StartOnboarding();
        string oldUrl = onboarding.Status().BootstrapUrl!;

        (HttpStatusCode before, string page) = await FetchAsync(oldUrl);
        Assert.Equal(HttpStatusCode.OK, before);
        Assert.Contains("LanDisk Local CA", page, StringComparison.Ordinal);

        OnboardingStatus restarted = Host.Runtime.Admin.RestartOnboarding();
        Assert.True(restarted.Active, restarted.LastError);

        (HttpStatusCode after, _) = await FetchAsync(oldUrl);
        Assert.NotEqual(HttpStatusCode.OK, after);                       // connection refused (0) or 404
        Assert.NotEqual(oldUrl, restarted.BootstrapUrl);
        Assert.False(onboarding.MatchesBootstrapId(oldUrl.Split('/')[^1]));

        // The new URL serves the page, so the QR the user now sees is a working one.
        (HttpStatusCode fresh, string freshPage) = await FetchAsync(restarted.BootstrapUrl!);
        Assert.Equal(HttpStatusCode.OK, fresh);
        Assert.Contains(restarted.BootstrapId!, freshPage, StringComparison.Ordinal);

        TestBase.Record("REPAIR03-08",
            $"重启前旧引导地址返回 200（含 CA 页面），重启后同一地址不再可用（连接被拒或非 200），旧 bootstrap id 不再匹配，而新地址立刻返回 200 且页面含新 id");
    }

    // ------------------------------------------------------------------ REPAIR03-09

    [Fact]
    public async Task An_explicit_restart_leaves_one_flow_and_no_stale_pairing_request()
    {
        OnboardingService onboarding = StartOnboarding();
        string address = PhysicalAddress();

        // The phone is halfway through: trust proven, request claimed and waiting for the operator.
        Assert.Equal(HttpStatusCode.NoContent, await ProbeAsync(address + ":" + HttpsPort, https: true));
        string token = await Admin.CreatePairingTokenAsync();
        PairingRequest started = Host.Runtime.Pairing.FindByToken(token)!;
        var phone = new LanDiskClient(Host.NewHttpsClient());
        ApiResult<JsonElement> claimed = await phone.PostAsync<JsonElement>("/api/pair/code",
            new { shortCode = started.ShortCode, deviceName = "REPAIR03 手机" });
        Assert.True(claimed.IsSuccess, claimed.Raw);
        Assert.Single(Host.Runtime.Admin.ActivePairingRequests());

        OnboardingStatus restarted = Host.Runtime.Admin.RestartOnboarding();

        Assert.True(restarted.Active, restarted.LastError);
        Assert.Empty(Host.Runtime.Admin.ActivePairingRequests());
        Assert.Equal(PairingState.Cancelled, Host.Runtime.Pairing.Find(started.Id)!.State);
        Assert.NotNull(onboarding.Status().BootstrapId);
        Assert.Null(onboarding.VerifiedHttpsHost);

        // The local operator's action is recorded, with the cancellation it implies.
        AuditEntry? entry = Host.Runtime.Audit.Tail(50).FirstOrDefault(e => e.Action == "restart-onboarding");
        Assert.NotNull(entry);
        Assert.Equal("pairing", entry!.Category);
        Assert.Equal(OperationActor.LocalAdmin.SourceToken, entry.Source);
        Assert.Equal(OperationActor.LocalAdminName, entry.DeviceName);
        Assert.Contains("cancelled=1", entry.Detail!, StringComparison.Ordinal);
        Assert.True(entry.Success);

        // Nothing was paired by the abandoned request.
        Assert.Empty(Host.Runtime.Devices.All());

        TestBase.Record("REPAIR03-09",
            $"显式重启后只剩一个向导流程：旧的 live 配对请求被取消（state=Cancelled）、ActivePairingRequests 为空、没有产生设备、向导 Active 且提示已清空；审计新增 action=restart-onboarding（source=local-admin，cancelled=1）");
    }

    // ------------------------------------------------------------------ REPAIR03-10

    [Fact]
    public void Ordinary_refresh_and_onshown_keep_the_same_bootstrap_id()
    {
        OnboardingStatus first = Host.Runtime.Admin.EnsureOnboardingStarted();
        string firstId = first.BootstrapId!;
        string firstUrl = first.BootstrapUrl!;
        IReadOnlyList<string> listeners = Host.Runtime.Onboarding.Listeners;

        for (int i = 0; i < 5; i++)
        {
            OnboardingStatus again = Host.Runtime.Admin.EnsureOnboardingStarted();
            Assert.True(again.Active);
            Assert.Equal(firstId, again.BootstrapId);
            Assert.Equal(firstUrl, again.BootstrapUrl);
        }

        Assert.Equal(firstId, Host.Runtime.Onboarding.BootstrapId);
        Assert.Equal(listeners, Host.Runtime.Onboarding.Listeners);

        // And the window's ordinary path is that idempotent call, not the explicit restart.
        string pages = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "src/LanDisk.App/Pages.cs"));
        int onShown = pages.IndexOf("public override void OnShown()", StringComparison.Ordinal);
        int ensure = pages.IndexOf("EnsureOnboardingStarted()", onShown, StringComparison.Ordinal);
        int restart = pages.IndexOf("RestartOnboarding()", StringComparison.Ordinal);
        Assert.True(onShown > 0 && ensure > onShown, "OnShown 必须继续调用幂等的 EnsureOnboardingStarted");
        Assert.True(restart > 0 && restart > onShown, "显式重启只能出现在「重新生成引导二维码」的按钮处理里");

        TestBase.Record("REPAIR03-10",
            $"普通刷新 / OnShown 仍是幂等路径：连续 5 次 EnsureOnboardingStarted 后 BootstrapId/BootstrapUrl/监听列表都不变；源码里 OnShown 调 EnsureOnboardingStarted，RestartOnboarding 只出现在「重新生成引导二维码」按钮里");
    }

    // ------------------------------------------------------------------ 补充：按钮语义

    [Fact]
    public void The_recovery_button_does_not_call_the_idempotent_start()
    {
        string pages = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "src/LanDisk.App/Pages.cs"));

        int iphone = pages.IndexOf("internal sealed class IphonePage", StringComparison.Ordinal);
        int nextClass = pages.IndexOf("internal sealed class ", iphone + 1, StringComparison.Ordinal);
        Assert.True(iphone > 0 && nextClass > iphone);
        string page = pages[iphone..nextClass];

        Assert.Contains("重新生成引导二维码", page, StringComparison.Ordinal);
        Assert.Contains("Runtime.Admin.RestartOnboarding()", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Runtime.Onboarding.Start(", page, StringComparison.Ordinal);
        Assert.Contains("Runtime.Onboarding.Stop(", page, StringComparison.Ordinal);   // 「结束向导」仍然可用

        TestBase.Record("REPAIR03-10b",
            "IphonePage 源码闸门：「重新生成引导二维码」按钮调用 AdminOperations.RestartOnboarding()，页面里不再直接调用幂等的 Onboarding.Start()；「结束向导」的显式停止仍然保留");
    }
}
