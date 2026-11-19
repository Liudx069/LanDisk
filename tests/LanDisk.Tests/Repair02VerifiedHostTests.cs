using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using LanDisk.Core.Model;
using LanDisk.Core.Networking;
using LanDisk.Core.Security;
using LanDisk.Server;
using LanDisk.Server.Services;
using LanDisk.Tests.Infrastructure;

namespace LanDisk.Tests;

/// <summary>
/// REPAIR_02 — the pairing QR must point at the address the phone actually reached, not at a guess.
///
/// <para><b>The defect, as measured on a real iPhone.</b> With a VPN/TUN up, the stable
/// <c>landisk-xxxxxxxx.local</c> name does not resolve for the phone, so
/// <c>https://&lt;stable&gt;:8892/api/onboarding/trust-probe</c> never arrives, while
/// <c>https://192.168.2.2:8892/api/onboarding/trust-probe</c> succeeds and the phone opens LanDisk at that
/// address. The wizard then handed out its pairing QR with the stable name first anyway, and scanning it
/// produced "此网站无法提供安全连接". Nothing about HTTPS, the port, the root CA or the leaf SAN was wrong;
/// the order of the links was.</para>
///
/// <para><b>The rule.</b> Whichever HTTPS host a trust probe actually arrived from is recorded, and the
/// pairing links are ordered by it: measured host, then stable name, then other physical LAN addresses,
/// then loopback. It is never "a VPN exists, so use the IP": if the stable name is the host that worked,
/// the stable name stays first.</para>
///
/// <para>The host shape these tests need (a LAN address bound over TLS) is what a phone sees; the rest of
/// the suite stays on loopback.</para>
/// </summary>
public sealed class Repair02VerifiedHostTests : TestBase
{
    private const string DummyToken = "TESTTOKEN000000000000000000000000";

    private string HttpsPort => Host.Runtime.Settings.HttpsPort.ToString();
    private string HttpPort => Host.Runtime.Settings.HttpPort.ToString();
    private string StableName => Host.Runtime.Identity.HostName;

    /// <summary>
    /// The shipped shape: TLS on, and bound to the physical LAN address as well as loopback, because that
    /// is the only way a phone can reach this machine at all. mDNS stays off so the suite does not fight
    /// for UDP 5353.
    ///
    /// <para>The ports are left at 0 on purpose: pinning a "free" port here would mean this suite holds a
    /// number another test host may have just probed as free, and the loser of that race fails to start
    /// for reasons that have nothing to do with the behaviour under test. The host picks ephemeral ports
    /// like every other suite, and <see cref="InitializeAsync"/> then tells the runtime which ports it
    /// actually bound.</para>
    /// </summary>
    protected override void ConfigureHost(HostOptions options)
    {
        options.LanEnabled = true;
        options.HttpsEnabled = true;
    }

    /// <summary>
    /// After the host is up, the settings must name the ports the process really bound: that is what they
    /// mean in production, and the pairing links are built from them.
    /// </summary>
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

    // ------------------------------------------------------------------ helpers

    /// <summary>The address a phone on this LAN would use. Fails loudly rather than skipping: on a machine
    /// with no LAN adapter this whole repair cannot be verified, and a silent pass would be worse.</summary>
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
            // Never pick one of the two ports Kestrel already owns: the "no link points at 8890" gate
            // below compares against this number.
            if (port == Host.Runtime.Settings.HttpPort || port == Host.Runtime.Settings.HttpsPort) continue;
            onboarding.Start(port);
        }

        Assert.True(onboarding.IsActive, "首次连接向导应当已经开启：" + onboarding.LastError);
        Assert.False(string.IsNullOrEmpty(onboarding.BootstrapId));
        return onboarding;
    }

    /// <summary>
    /// The real probe path: an HTTPS GET carrying the live bootstrap id, with the Host header the phone
    /// would send. The TLS connection goes to loopback (the certificate is self-signed in tests), but the
    /// server's evidence — and therefore what is recorded — is the Host header, exactly as in production.
    /// </summary>
    private async Task<HttpStatusCode> ProbeAsync(string? hostHeader, bool https = true, string? bootstrapId = null)
    {
        string id = bootstrapId ?? Host.Runtime.Onboarding.BootstrapId ?? "";
        HttpClient client = https ? Host.NewHttpsClient() : Host.NewClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/onboarding/trust-probe?b=" + Uri.EscapeDataString(id));
        if (!string.IsNullOrEmpty(hostHeader)) request.Headers.Host = hostHeader;

        using HttpResponseMessage response = await client.SendAsync(request);
        await response.Content.ReadAsStringAsync();
        return response.StatusCode;
    }

    private string[] Urls(string token = DummyToken) => [.. Host.Runtime.Admin.PairingUrls(token)];

    /// <summary>Puts a hint into the service as if a DHCP change had happened underneath it.</summary>
    private void ForceStaleHint(string host)
    {
        object onboarding = Host.Runtime.Onboarding;
        Type type = onboarding.GetType();
        type.GetField("_verifiedHttpsHost", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(onboarding, host);
        type.GetField("_verifiedHttpsAt", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(onboarding, DateTimeOffset.Now);
    }

    private static void AssertDistinct(IReadOnlyList<string> urls)
    {
        Assert.Equal(urls.Count, urls.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    // ------------------------------------------------------------------ REPAIR02-01

    [Fact]
    public async Task A_successful_stable_name_probe_keeps_the_stable_name_first()
    {
        StartOnboarding();

        HttpStatusCode status = await ProbeAsync(StableName + ":" + HttpsPort);
        Assert.Equal(HttpStatusCode.NoContent, status);

        Assert.Equal(StableName, Host.Runtime.Onboarding.VerifiedHttpsHost);
        Assert.NotNull(Host.Runtime.Onboarding.VerifiedHttpsAt);
        _ = PhysicalAddress();

        string[] urls = Urls();
        Assert.Equal($"https://{StableName}:{HttpsPort}/pair#t={DummyToken}", urls[0]);
        AssertDistinct(urls);

        TestBase.Record("REPAIR02-01",
            $"stable .local 的 HTTPS trust probe 成功时记录该 host 并让配对链接以它为首：VerifiedHttpsHost={StableName}，PairingUrls[0]={urls[0]}");
    }

    // ------------------------------------------------------------------ REPAIR02-02

    [Fact]
    public async Task A_successful_physical_ip_probe_puts_that_ip_first()
    {
        StartOnboarding();
        string address = PhysicalAddress();
        Assert.Contains(Host.Runtime.BoundUrls, u => u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                                                    && u.Contains(address, StringComparison.Ordinal));

        HttpStatusCode status = await ProbeAsync(address + ":" + HttpsPort);
        Assert.Equal(HttpStatusCode.NoContent, status);

        Assert.Equal(address, Host.Runtime.Onboarding.VerifiedHttpsHost);

        string[] urls = Urls();
        Assert.Equal($"https://{address}:{HttpsPort}/pair#t={DummyToken}", urls[0]);
        Assert.StartsWith("https://", urls[0], StringComparison.Ordinal);
        AssertDistinct(urls);

        TestBase.Record("REPAIR02-02",
            $"只有物理 IP 的 HTTPS trust probe 成功时（.local 探测未成功）配对链接以该 IP 为首：VerifiedHttpsHost={address}，PairingUrls[0]={urls[0]}");
    }

    // ------------------------------------------------------------------ REPAIR02-03

    [Fact]
    public async Task A_vpn_or_tunnel_never_forces_the_ip()
    {
        bool vpn = Host.Runtime.VpnOrTunnelDetected(out string details);
        StartOnboarding();
        Assert.Equal(HttpStatusCode.NoContent, await ProbeAsync(StableName + ":" + HttpsPort));

        string[] urls = Urls();
        Assert.Equal($"https://{StableName}:{HttpsPort}/pair#t={DummyToken}", urls[0]);

        // The decision must not be able to look at the VPN state at all: only the measured host counts.
        string source = StripComments(File.ReadAllText(
            Path.Combine(TestPaths.RepoRoot, "src/LanDisk.Server/Services/AdminOperations.cs")));
        int start = source.IndexOf("public IReadOnlyList<string> PairingUrls(", StringComparison.Ordinal);
        int end = source.IndexOf("private void Audit(", start, StringComparison.Ordinal);
        Assert.True(start > 0 && end > start);
        string pairing = source[start..end];
        Assert.DoesNotContain("VpnOrTunnelDetected", pairing, StringComparison.Ordinal);
        Assert.DoesNotContain("IsBenchmarkRange", pairing, StringComparison.Ordinal);

        TestBase.Record("REPAIR02-03",
            $"VPN/TUN 检测（本机={vpn}{(vpn ? "：" + details : "")}）不是选择依据：stable 探测成功后 PairingUrls[0] 仍是 {urls[0]}；PairingUrls 源码里不出现 VpnOrTunnelDetected / IsBenchmarkRange");
    }

    // ------------------------------------------------------------------ REPAIR02-04

    [Fact]
    public async Task A_forged_host_header_is_never_recorded()
    {
        StartOnboarding();

        foreach (string forged in new[] { "evil.example", "8.8.8.8", "198.18.0.1", "localhost", "10.9.9.9" })
        {
            Assert.Null(Host.Runtime.Onboarding.NormalizeVerifiedHost(forged));
            await ProbeAsync(forged + ":" + HttpsPort);
            Assert.Null(Host.Runtime.Onboarding.VerifiedHttpsHost);
            Assert.DoesNotContain(Urls(), u => u.Contains(forged, StringComparison.OrdinalIgnoreCase));
        }

        // Every address the classifier rejected on this machine is refused too — the VPN's 198.18 address
        // and the Hyper-V switch address included. The loopback pseudo-interface is rejected by the same
        // rule but is a separate case: it is recorded and then never ranked first (REPAIR02-16).
        IReadOnlyList<AdapterVerdict> rejected = [.. Host.Runtime.AdapterReport().Where(v => !v.IsPhysical)];
        Assert.NotEmpty(rejected);
        int checkedAddresses = 0;
        foreach (AdapterVerdict verdict in rejected)
        {
            if (!IPAddress.TryParse(verdict.Address, out IPAddress? ip) || IPAddress.IsLoopback(ip)) continue;
            checkedAddresses++;
            Assert.Null(Host.Runtime.Onboarding.NormalizeVerifiedHost(verdict.Address));
            await ProbeAsync(verdict.Address + ":" + HttpsPort);
            Assert.Null(Host.Runtime.Onboarding.VerifiedHttpsHost);
        }
        Assert.True(checkedAddresses > 0, "本机没有被排除的网卡地址可供核对，门禁会退化成空断言");

        // A plain-HTTP probe is not evidence about TLS reachability either. REPAIR_03 made that explicit:
        // such a request is refused outright (404) and marks nothing — not the host, and not "trusted".
        Assert.Equal(HttpStatusCode.NotFound, await ProbeAsync("127.0.0.1:" + HttpPort, https: false));
        Assert.Null(Host.Runtime.Onboarding.VerifiedHttpsHost);
        Assert.False(Host.Runtime.Onboarding.Trusted);
        Assert.Null(Host.Runtime.Onboarding.TrustedAt);

        TestBase.Record("REPAIR02-04",
            $"伪造成 evil.example / 8.8.8.8 / 198.18.0.1 / localhost / 10.9.9.9 以及本机全部被排除网卡地址（{checkedAddresses} 条，含 VPN 与 Hyper-V）的 Host 都不能写入 VerifiedHttpsHost；明文 HTTP 探测（REPAIR_03 起）被直接拒绝且不标记 Trusted");
    }

    // ------------------------------------------------------------------ REPAIR02-05

    [Fact]
    public async Task Only_currently_eligible_physical_addresses_are_accepted()
    {
        StartOnboarding();
        string eligible = PhysicalAddress();

        Assert.Equal(eligible, Host.Runtime.Onboarding.NormalizeVerifiedHost(eligible));
        Assert.Equal(eligible, Host.Runtime.Onboarding.NormalizeVerifiedHost(" " + eligible + " "));

        // Private, plausible and completely wrong: not on this machine, so not a phone-reachable address.
        foreach (string stranger in new[] { "10.9.9.9", "192.168.77.77", "172.20.5.5" })
        {
            Assert.Null(Host.Runtime.Onboarding.NormalizeVerifiedHost(stranger));
            await ProbeAsync(stranger + ":" + HttpsPort);
            Assert.Null(Host.Runtime.Onboarding.VerifiedHttpsHost);
        }

        await ProbeAsync(eligible + ":" + HttpsPort);
        Assert.Equal(eligible, Host.Runtime.Onboarding.VerifiedHttpsHost);

        TestBase.Record("REPAIR02-05",
            $"物理 IP 必须来自 PhysicalLanSelector 当前 eligible 集合：{eligible} 被接受，10.9.9.9 / 192.168.77.77 / 172.20.5.5 全部被拒绝");
    }

    // ------------------------------------------------------------------ REPAIR02-06

    [Fact]
    public async Task A_hint_that_is_no_longer_eligible_is_ignored()
    {
        StartOnboarding();
        string address = PhysicalAddress();
        await ProbeAsync(address + ":" + HttpsPort);
        Assert.Equal(address, Host.Runtime.Onboarding.VerifiedHttpsHost);
        Assert.Equal($"https://{address}:{HttpsPort}/pair#t={DummyToken}", Urls()[0]);

        // DHCP moved (or the cable was unplugged) after the probe: the old address is stale.
        ForceStaleHint("10.9.9.9");
        Assert.Equal("10.9.9.9", Host.Runtime.Onboarding.VerifiedHttpsHost);   // still the raw state

        string[] urls = Urls();
        Assert.DoesNotContain(urls, u => u.Contains("10.9.9.9", StringComparison.Ordinal));   // but never offered
        Assert.NotEqual($"https://10.9.9.9:{HttpsPort}/pair#t={DummyToken}", urls[0]);
        Assert.Equal($"https://{address}:{HttpsPort}/pair#t={DummyToken}", urls[0]);          // normal fallback
        Assert.Equal("http://127.0.0.1:" + HttpPort + "/pair#t=" + DummyToken, urls[^1]);     // loopback still last

        // A hint for an address that is eligible but no longer covered by the leaf is dropped as well.
        Host.Runtime.Onboarding.ClearVerifiedHttpsHost("测试：模拟证书不再覆盖该地址");
        Assert.Null(Host.Runtime.Onboarding.VerifiedHttpsHost);

        TestBase.Record("REPAIR02-06",
            $"旧 IP 在地址变化后不再 eligible 时被忽略：注入过期提示 10.9.9.9 后 PairingUrls 不含它、也不以它为首，按 stable -> physical -> loopback 正常回退（首项 {address}，末项 loopback）");
    }

    // ------------------------------------------------------------------ REPAIR02-07

    [Fact]
    public async Task The_hint_survives_the_8890_listener_being_closed()
    {
        OnboardingService onboarding = StartOnboarding();
        string address = PhysicalAddress();
        await ProbeAsync(address + ":" + HttpsPort);
        Assert.Equal(address, Host.Runtime.Onboarding.VerifiedHttpsHost);

        // The host closes the plain-HTTP window as soon as the phone has trusted the CA.
        onboarding.Stop("测试：手机已完成信任");
        Assert.False(onboarding.IsActive);
        Assert.Empty(onboarding.Listeners);

        // The pairing QR is still ahead of the user, so the measurement has to survive the closed listener.
        Assert.Equal(address, Host.Runtime.Onboarding.VerifiedHttpsHost);
        Assert.Equal($"https://{address}:{HttpsPort}/pair#t={DummyToken}", Urls()[0]);

        TestBase.Record("REPAIR02-07",
            $"Onboarding.Stop 关闭 8890 之后本次已验证 host 仍然可用：IsActive=false、监听清空，但 VerifiedHttpsHost={address} 且 PairingUrls[0] 仍是它");
    }

    // ------------------------------------------------------------------ REPAIR02-08

    [Fact]
    public async Task A_new_wizard_session_clears_the_previous_hint()
    {
        OnboardingService onboarding = StartOnboarding();
        string address = PhysicalAddress();

        // What the links look like with no measurement at all (mDNS is not publishing in this host).
        string[] baseline = Urls();

        // The stable name is the host that worked, which changes the first link.
        await ProbeAsync(StableName + ":" + HttpsPort);
        Assert.Equal(StableName, Host.Runtime.Onboarding.VerifiedHttpsHost);
        Assert.Equal($"https://{StableName}:{HttpsPort}/pair#t={DummyToken}", Urls()[0]);
        Assert.NotEqual(baseline[0], Urls()[0]);

        // Starting a session while one is open must not disturb the phone that is pairing right now.
        onboarding.Start(FreePort());
        Assert.Equal(StableName, Host.Runtime.Onboarding.VerifiedHttpsHost);

        // A genuinely new session is a new connection attempt: the old evidence does not apply.
        onboarding.Stop("测试：结束向导");
        OnboardingStatus restarted = onboarding.Start(FreePort());
        Assert.True(restarted.Active);
        Assert.Null(Host.Runtime.Onboarding.VerifiedHttpsHost);
        Assert.Null(Host.Runtime.Onboarding.VerifiedHttpsAt);
        Assert.Equal(baseline, Urls());
        _ = address;

        TestBase.Record("REPAIR02-08",
            $"新一轮 Onboarding.Start 清空旧提示：已在运行时调用 Start 不清空；Stop 后再 Start，VerifiedHttpsHost/VerifiedHttpsAt 归 null 且链接列表回到无提示时的基线（首项 {baseline[0]}，此前被 stable 提示改写为 {StableName}）");
    }

    // ------------------------------------------------------------------ REPAIR02-09

    [Fact]
    public async Task A_successful_pairing_clears_the_hint_and_leaves_the_device_alone()
    {
        OnboardingService onboarding = StartOnboarding();
        string address = PhysicalAddress();

        // The phone side, over HTTPS: claim the request, prove trust, then wait for the operator.
        string token = await Admin.CreatePairingTokenAsync();
        PairingRequest request = Host.Runtime.Pairing.FindByToken(token)!;
        var phone = new LanDiskClient(Host.NewHttpsClient());
        ApiResult<JsonElement> claimed = await phone.PostAsync<JsonElement>("/api/pair/code",
            new { shortCode = request.ShortCode, deviceName = "REPAIR02 手机" });
        Assert.True(claimed.IsSuccess, claimed.Raw);
        string claimSecret = claimed.Body.GetProperty("claimSecret").GetString()!;

        Assert.Equal(HttpStatusCode.NoContent, await ProbeAsync(address + ":" + HttpsPort));
        Assert.Equal(address, Host.Runtime.Onboarding.VerifiedHttpsHost);

        OperationOutcome<DeviceDefinition> confirmed = Host.Runtime.Admin.ConfirmPairing(request.Id);
        Assert.True(confirmed.Ok, confirmed.Message);

        // The temporary hint is gone; the device and its HTTPS session are not.
        Assert.Null(Host.Runtime.Onboarding.VerifiedHttpsHost);
        Assert.Null(Host.Runtime.Onboarding.VerifiedHttpsAt);
        Assert.False(onboarding.IsActive);

        ApiResult<JsonElement> poll = await phone.PostAsync<JsonElement>("/api/pair/status",
            new { requestId = request.Id, claimSecret });
        Assert.True(poll.IsSuccess, poll.Raw);
        Assert.Equal("paired", poll.Body.GetProperty("status").GetString());
        ApiResult<JsonElement> me = await phone.GetAsync<JsonElement>("/api/me");
        Assert.True(me.IsSuccess, me.Raw);
        Assert.Equal("device", me.Body.GetProperty("kind").GetString());
        Assert.Equal(confirmed.Value!.Id, Host.Runtime.Devices.Find(confirmed.Value!.Id)!.Id);

        TestBase.Record("REPAIR02-09",
            "ConfirmPairing 成功后临时提示被清理（VerifiedHttpsHost/VerifiedHttpsAt 归 null），设备已落库且手机在 HTTPS 上仍然处于已配对会话（/api/me kind=device）");
    }

    // ------------------------------------------------------------------ REPAIR02-10

    [Fact]
    public async Task The_link_order_is_verified_then_stable_then_physical_then_loopback()
    {
        OnboardingService onboarding = StartOnboarding();
        string address = PhysicalAddress();

        // (a) measured host = stable name: stable first, then the physical address, then loopback.
        await ProbeAsync(StableName + ":" + HttpsPort);
        string[] stableFirst = Urls();
        Assert.Equal($"https://{StableName}:{HttpsPort}/pair#t={DummyToken}", stableFirst[0]);
        Assert.Contains($"https://{address}:{HttpsPort}/pair#t={DummyToken}", stableFirst);
        Assert.True(Array.IndexOf(stableFirst, $"https://{address}:{HttpsPort}/pair#t={DummyToken}") > 0,
            "其它物理地址必须排在已验证的 stable 名称之后");
        Assert.Equal("http://127.0.0.1:" + HttpPort + "/pair#t=" + DummyToken, stableFirst[^1]);
        AssertDistinct(stableFirst);

        // (b) measured host = physical address: that address first, stable name not offered at all (mDNS is
        //     not publishing here and the phone did not use the name), loopback still last.
        onboarding.Stop("测试：重新开始一轮");
        StartOnboarding();
        Assert.Null(Host.Runtime.Onboarding.VerifiedHttpsHost);
        await ProbeAsync(address + ":" + HttpsPort);
        string[] ipFirst = Urls();
        Assert.Equal($"https://{address}:{HttpsPort}/pair#t={DummyToken}", ipFirst[0]);
        Assert.DoesNotContain($"https://{StableName}:{HttpsPort}/pair#t={DummyToken}", ipFirst);
        Assert.Equal("http://127.0.0.1:" + HttpPort + "/pair#t=" + DummyToken, ipFirst[^1]);
        AssertDistinct(ipFirst);

        TestBase.Record("REPAIR02-10",
            $"PairingUrls 顺序正确且去重：stable 验证时 [0]={stableFirst[0]} 且物理地址在后、loopback 在末；physical 验证时 [0]={ipFirst[0]}、不再提供未验证的 stable；两次列表都无重复项");
    }

    // ------------------------------------------------------------------ REPAIR02-11

    [Fact]
    public async Task The_plain_http_bootstrap_page_still_carries_no_secret()
    {
        OnboardingService onboarding = StartOnboarding();
        string address = PhysicalAddress();
        string token = await Admin.CreatePairingTokenAsync();
        PairingRequest request = Host.Runtime.Pairing.FindByToken(token)!;
        var phone = new LanDiskClient(Host.NewHttpsClient());
        ApiResult<JsonElement> claimed = await phone.PostAsync<JsonElement>("/api/pair/code",
            new { shortCode = request.ShortCode, deviceName = "REPAIR02 泄密检查" });
        Assert.True(claimed.IsSuccess, claimed.Raw);
        string claimSecret = claimed.Body.GetProperty("claimSecret").GetString()!;

        string rendered = onboarding.BootstrapPage();
        string served = await FetchBootstrapAsync(onboarding.Status().BootstrapUrl!);
        Assert.Contains(onboarding.BootstrapId!, served, StringComparison.Ordinal);   // its own session id only

        foreach (string body in new[] { rendered, served })
        {
            foreach (string forbidden in new[]
                     {
                         "#t=", request.Token, request.ShortCode, claimSecret, Host.Runtime.AdminBootstrapToken,
                         "csrf", "cookie", "secret", "token="
                     })
            {
                Assert.DoesNotContain(forbidden, body, StringComparison.OrdinalIgnoreCase);
            }
            Assert.DoesNotContain("admin", body, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("landisk-ca.cer", served, StringComparison.Ordinal);
        Assert.Contains("trust-probe", served, StringComparison.Ordinal);

        TestBase.Record("REPAIR02-11",
            $"明文引导页（内存渲染版与实际 HTTP 返回版都核对）仍是 0 配对令牌、0 管理令牌、0 cookie、0 CSRF、0 设备密钥：本轮真实配对令牌 {request.Token[..6]}… / 短码 / claimSecret / 管理令牌都不出现，只含公开 CA 与 trust 探测（地址 {address}）");
    }

    // ------------------------------------------------------------------ REPAIR02-12

    [Fact]
    public void The_pairing_token_only_ever_travels_over_https()
    {
        StartOnboarding();
        string token = "REPAIR02TOKEN0123456789abcdef";
        string[] urls = Urls(token);

        Assert.All(urls, u => Assert.Contains("/pair#t=" + token, u, StringComparison.Ordinal));
        Assert.All(urls.Where(u => !u.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)),
            u => Assert.StartsWith("https://", u, StringComparison.Ordinal));
        Assert.DoesNotContain(urls, u => u.Contains(":" + Host.Runtime.Onboarding.Port + "/", StringComparison.Ordinal));

        // The plain-HTTP bootstrap page is the one place a token must never appear, in any form.
        string page = Host.Runtime.Onboarding.BootstrapPage();
        Assert.DoesNotContain("#t=", page, StringComparison.Ordinal);
        Assert.DoesNotContain(token, page, StringComparison.Ordinal);

        TestBase.Record("REPAIR02-12",
            $"配对令牌只出现在 HTTPS 的 /pair#t= 链接里（非回环链接一律 https://），且不指向 8890 引导端口；引导页源码与内容都不含该令牌（{urls.Length} 条链接核对）");
    }

    // ------------------------------------------------------------------ REPAIR02-13

    [Fact]
    public void The_leaf_san_never_gains_a_vpn_hyperv_or_wsl_address()
    {
        Host.Runtime.EnsureCertificates();
        CertificateInfo certificate = Host.Runtime.Certificate!;
        Assert.NotEmpty(certificate.IpAddresses);

        IReadOnlyList<string> eligible = Host.Runtime.LanAddresses();
        foreach (string text in certificate.IpAddresses)
        {
            Assert.True(IPAddress.TryParse(text, out IPAddress? ip), "SAN 里的地址必须能解析：" + text);
            Assert.False(PhysicalLanSelector.IsBenchmarkRange(ip!),
                "SAN 不得包含 198.18.0.0/15（VPN/TUN 的虚拟网段）：" + text);
            if (IPAddress.IsLoopback(ip!)) continue;
            Assert.Contains(eligible, a => IPAddress.Parse(a).Equals(ip));
        }

        IReadOnlyList<AdapterVerdict> rejected = [.. Host.Runtime.AdapterReport().Where(v => !v.IsPhysical)];
        Assert.NotEmpty(rejected);
        int rejectedNonLoopback = 0;
        foreach (AdapterVerdict verdict in rejected)
        {
            // Loopback is in the SAN on purpose (the local management page uses it); what must never appear
            // is a tunnel, switch or benchmark address.
            if (IPAddress.TryParse(verdict.Address, out IPAddress? rejectedIp) && IPAddress.IsLoopback(rejectedIp)) continue;
            rejectedNonLoopback++;
            Assert.DoesNotContain(certificate.IpAddresses, a => a == verdict.Address);
        }
        Assert.True(rejectedNonLoopback > 0, "本机没有被排除的网卡地址可供核对，门禁会退化成空断言");

        TestBase.Record("REPAIR02-13",
            $"leaf SAN 未因本轮改动加入任何虚拟地址：SAN IP = [{string.Join(", ", certificate.IpAddresses)}]，全部是回环或当前 eligible 物理地址；本机被排除的 {rejectedNonLoopback} 条非回环网卡地址（含 198.18.0.x 与 Hyper-V）一个都不在其中");
    }

    // ------------------------------------------------------------------ REPAIR02-14

    [Fact]
    public void No_route_dns_or_vpn_configuration_write_was_added()
    {
        string[] forbidden =
        [
            "route add", "route delete", "New-NetRoute", "Remove-NetRoute", "Set-NetIPInterface",
            "Set-NetAdapter", "Set-DnsClientServerAddress", "Set-NetConnectionProfile",
            "Set-VpnConnection", "Add-VpnConnection", "rasdial", "EnableStatic",
            "ManagementObjectSearcher", "netsh interface", "netsh winhttp"
        ];

        string productRoot = Path.Combine(TestPaths.RepoRoot, "src");
        IReadOnlyList<string> files = [.. Directory.EnumerateFiles(productRoot, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))];
        Assert.True(files.Count > 20, $"只扫到 {files.Count} 个产品源文件，门禁会退化成空断言");

        var hits = new List<string>();
        var netshSites = new List<string>();
        var netshProcessStarts = new List<string>();
        var netshRunnerSites = new List<string>();
        foreach (string file in files)
        {
            string relative = Path.GetRelativePath(productRoot, file).Replace('\\', '/');
            string text = File.ReadAllText(file);
            foreach (string pattern in forbidden)
            {
                if (text.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    hits.Add(relative + " → " + pattern);
            }
            if (text.Contains("ProcessStartInfo(\"netsh\")", StringComparison.Ordinal))
                netshProcessStarts.Add(relative);
            // 1.7.6：进程启动集中到 Core 的 BoundedProcessRunner（并行消费两个流、单一总时限覆盖
            // "读完输出 + 进程退出"）。因此"谁在驱动 netsh"要看**谁把 netsh 这个词交给执行器**，
            // 而不是谁 new 了 ProcessStartInfo —— 后者现在只剩安装器的规则助手。
            if (text.Contains("BoundedProcessRunner.Run(\"netsh\"", StringComparison.Ordinal))
                netshRunnerSites.Add(relative);
            foreach (string line in text.Split('\n'))
            {
                // Code only: a doc comment naming the read-only verb is part of the explanation, not a call.
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("///", StringComparison.Ordinal) || trimmed.StartsWith("//", StringComparison.Ordinal)) continue;
                if (line.Contains("netsh", StringComparison.OrdinalIgnoreCase))
                    netshSites.Add(relative + ": " + line.Trim());
                // A single-line /* */ comment is documentation too.
                if (trimmed.StartsWith("/*", StringComparison.Ordinal)) netshSites.RemoveAt(netshSites.Count - 1);
            }
        }

        Assert.Empty(hits);

        // netsh is only ever *firewall* code: the installer's rule helper, plus (since 1.7.2) the shell's
        // read-only probe that lets the self-check actually see whether the rules exist. It never appears in
        // the server, and the probe is asserted to be read-only — see
        // ShellInvariantTests.The_only_netsh_use_in_the_product_is_the_firewall_rule_helper.
        Assert.NotEmpty(netshSites);
        Assert.All(netshSites, site =>
            Assert.True(
                site.StartsWith("LanDisk.Setup/", StringComparison.Ordinal)
                || site.StartsWith("LanDisk.App/FirewallStatus.cs", StringComparison.Ordinal),
                "netsh 出现在不允许的位置：" + site));
        Assert.Equal(["LanDisk.Setup/FirewallHelper.cs"], [.. netshProcessStarts.Order(StringComparer.Ordinal)]);

        // 只有两个位置把 netsh 交给执行器：安装器的规则助手（增删规则）与外壳的只读探针。
        Assert.Equal(["LanDisk.App/FirewallStatus.cs", "LanDisk.Setup/FirewallHelper.cs"],
            [.. netshRunnerSites.Concat(netshProcessStarts).Distinct().Order(StringComparer.Ordinal)]);

        // 执行器本身必须是**通用**的：它不得认识 netsh，也不得内置任何动词。
        // 只看代码行 —— 文档注释里说明这段历史（为什么不再 ReadToEnd→ReadToEnd→WaitForExit）是解释，
        // 不是调用，和上面 netshSites 的判定口径保持一致。
        string runner = File.ReadAllText(Path.Combine(productRoot, "LanDisk.Core/Networking/BoundedProcessRunner.cs"));
        var runnerCode = new List<string>();
        foreach (string line in runner.Split('\n'))
        {
            string t = line.TrimStart();
            if (t.StartsWith("///", StringComparison.Ordinal) || t.StartsWith("//", StringComparison.Ordinal)) continue;
            if (t.StartsWith("/*", StringComparison.Ordinal)) continue;
            runnerCode.Add(line);
        }
        string runnerCodeText = string.Join('\n', runnerCode);
        Assert.DoesNotContain("netsh", runnerCodeText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("advfirewall", runnerCodeText, StringComparison.OrdinalIgnoreCase);

        // And the only verbs behind it are the two firewall-rule verbs.
        string plan = File.ReadAllText(Path.Combine(productRoot, "LanDisk.Setup/FirewallPlan.cs"));
        Assert.Contains("\"advfirewall\", \"firewall\", \"add\", \"rule\"", plan, StringComparison.Ordinal);
        Assert.Contains("\"advfirewall\", \"firewall\", \"delete\", \"rule\"", plan, StringComparison.Ordinal);
        foreach (string verb in new[] { "\"interface\"", "\"winhttp\"", "\"int\"", "\"wlan\"" })
        {
            Assert.DoesNotContain(verb, plan, StringComparison.Ordinal);
        }

        TestBase.Record("REPAIR02-14",
            $"本轮没有新增任何 route / DNS / VPN 配置写入：扫描 {files.Count} 个产品源文件，0 处命中禁用动词；产品中 {netshSites.Count} 处 netsh 全部位于 LanDisk.Setup（防火墙助手与规则计划），唯一的 netsh 进程调用在 FirewallHelper.cs，动词只有 advfirewall 的 add/delete rule");
    }

    // ------------------------------------------------------------------ REPAIR02-15（§十 回归门禁）

    [Fact]
    public async Task The_root_route_still_answers_html_and_never_offers_a_download()
    {
        string address = PhysicalAddress();
        LanDiskClient device = await PairDeviceAsync(name: "REPAIR02 根路由手机");

        foreach ((string label, HttpClient client, string host) in new[]
                 {
                     ("未配对 HTTP/LAN IP", Host.NewClient(), address),
                     ("未配对 HTTPS/LAN IP", Host.NewHttpsClient(), address),
                     ("未配对 HTTPS/stable", Host.NewHttpsClient(), StableName),
                 })
        {
            foreach (HttpMethod method in new[] { HttpMethod.Get, HttpMethod.Head })
            {
                using var request = new HttpRequestMessage(method, "/");
                request.Headers.TryAddWithoutValidation("User-Agent",
                    "Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1");
                request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
                request.Headers.Host = host + ":" + (client.BaseAddress!.Scheme == "https" ? HttpsPort : HttpPort);

                using HttpResponseMessage response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);

                MediaTypeHeaderValue? contentType = response.Content.Headers.ContentType;
                Assert.NotNull(contentType);
                Assert.Equal("text/html", contentType!.MediaType);
                Assert.Equal("utf-8", contentType.CharSet);
                Assert.False(response.Content.Headers.Contains("Content-Disposition"), label + method + " 不得返回 Content-Disposition");
                Assert.False(response.Headers.TryGetValues("Content-Disposition", out _), label + method + " 不得在响应头返回 Content-Disposition");
                Assert.NotEqual("application/octet-stream", contentType.MediaType);
                Assert.NotEqual("text/plain", contentType.MediaType);

                if (method == HttpMethod.Get)
                {
                    string body = await response.Content.ReadAsStringAsync();
                    Assert.Contains("<!doctype html>", body, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("document.txt", body, StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
                }
            }
        }

        // The paired client keeps the same contract.
        using var pairedRequest = new HttpRequestMessage(HttpMethod.Get, "/");
        pairedRequest.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        using HttpResponseMessage paired = await device.Http.SendAsync(pairedRequest);
        Assert.Equal(HttpStatusCode.OK, paired.StatusCode);
        Assert.Equal("text/html", paired.Content.Headers.ContentType?.MediaType);
        Assert.False(paired.Content.Headers.Contains("Content-Disposition"));

        TestBase.Record("REPAIR02-15",
            "根路由回归门禁（§十）：未配对/已配对、HTTP/HTTPS、LAN IP 与 stable host 的 GET/HEAD / 全部返回 text/html; charset=utf-8、无 Content-Disposition、无 application/octet-stream、不出现 document.txt 下载");
    }

    // ------------------------------------------------------------------ REPAIR02-16（补充）

    [Fact]
    public async Task A_loopback_hint_never_becomes_the_phones_first_address()
    {
        StartOnboarding();

        // A browser on this very machine probing itself is a legitimate probe result, but 127.0.0.1 is not
        // a place a phone can go, so it must never be the first address a QR code points at.
        Assert.Equal(HttpStatusCode.NoContent, await ProbeAsync("127.0.0.1:" + HttpsPort));
        Assert.Equal("127.0.0.1", Host.Runtime.Onboarding.VerifiedHttpsHost);

        string[] urls = Urls();
        Assert.DoesNotContain(urls, u => u.Contains("127.0.0.1", StringComparison.Ordinal) && u.StartsWith("https://", StringComparison.Ordinal));
        Assert.Equal("http://127.0.0.1:" + HttpPort + "/pair#t=" + DummyToken, urls[^1]);

        TestBase.Record("REPAIR02-16",
            $"回环探测被记录但永不成为手机首选：VerifiedHttpsHost=127.0.0.1 时 PairingUrls 里没有 https://127.0.0.1…，回环仍只作为最后一项本机诊断链接（{urls[^1]}）");
    }

    // ------------------------------------------------------------------ helpers reused above

    private async Task<string> FetchBootstrapAsync(string url)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        HttpResponseMessage response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        response.Dispose();
        return body;
    }

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
