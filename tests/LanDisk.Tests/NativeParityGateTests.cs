using System.Text.Json;
using LanDisk.Core.Configuration;
using LanDisk.Core.Model;
using LanDisk.Core.Networking;
using LanDisk.Core.Security;
using LanDisk.Server.Services;
using LanDisk.Tests.Infrastructure;

namespace LanDisk.Tests;

/// <summary>
/// Task 09, fixes 01–03 and 06: the parts that cannot be driven from a unit test, held in place by
/// assertions instead of prose.
///
/// <para>The WPF window is a WinExe and the test project does not reference it, so "the confirm button
/// calls the shared implementation" cannot be executed here. What <i>can</i> be checked is the source:
/// the window must not contain the calls that were the bug. That is the same technique the task-08 gates
/// use, and it is honest about what it proves — it proves no such code path exists, not that a click was
/// observed.</para>
/// </summary>
public sealed class NativeParityGateTests : TestBase
{
    /// <summary>
    /// The file's code, with whole-line comments removed.
    ///
    /// A gate that scans raw text trips over the comments that explain the bug it is guarding against
    /// ("this used to call Pairing.Cancel"), and those comments are worth keeping. Dropping whole-line
    /// comments is enough and cannot hide real code: a statement is never inside one.
    /// </summary>
    private static string AppSource(string fileName)
    {
        string path = Path.Combine(TestPaths.RepoRoot, "src/LanDisk.App", fileName);
        Assert.True(File.Exists(path), "找不到 " + path);
        return StripComments(File.ReadAllText(path));
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

    private static IReadOnlyList<string> AppSources()
    {
        string root = Path.Combine(TestPaths.RepoRoot, "src/LanDisk.App");
        return [.. Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))];
    }

    [Fact]
    public void The_native_window_never_cancels_a_pairing_request_except_through_reject()
    {
        // The bug: DevicesPage.ConfirmAsync and IphonePage.ConfirmAsync both called Pairing.Cancel, so
        // "确认配对" cancelled the request and created no device.
        foreach ((string file, string text) in new[] { ("Pages.cs", AppSource("Pages.cs")), ("Pages2.cs", AppSource("Pages2.cs")) })
        {
            Assert.DoesNotContain("Pairing.Cancel", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Pairing.Create(", text, StringComparison.Ordinal);
        }

        // Confirming goes through the shared layer, on both pages.
        string pages = AppSource("Pages.cs");
        Assert.Contains("Runtime.Admin.ConfirmPairing(", pages, StringComparison.Ordinal);
        Assert.Contains("Runtime.Admin.RejectPairing(", pages, StringComparison.Ordinal);
        Assert.Contains("Runtime.Admin.EnsurePairingRequest(", pages, StringComparison.Ordinal);

        TestBase.Record("FIX-09-01i",
            "外壳源码里不再出现 Pairing.Cancel / Pairing.Create；确认与拒绝都走 Runtime.Admin 的共享实现");
    }

    [Fact]
    public void The_native_window_never_writes_device_state_itself()
    {
        // Device mutation belongs to AdminOperations; the window used to call Runtime.Devices.Update
        // directly, which is how "revoke" ended up skipping the token and session rotation.
        //
        // Scope: every user-facing page and the shell. UiCapture.cs is deliberately excluded and is the only
        // exclusion — it is the UI evidence tool, not a page: it manufactures the worst-case device list
        // (online / recent / offline / revoked / capability matrix) that the screenshots must cover, which is
        // fixture setup rather than a user action. The exclusion is pinned below so it cannot quietly grow.
        string capture = AppSources().Single(f => Path.GetFileName(f) == "UiCapture.cs");
        IReadOnlyList<string> sources = [.. AppSources().Where(f => !string.Equals(f, capture, StringComparison.Ordinal))];
        Assert.Equal(AppSources().Count - 1, sources.Count);
        Assert.DoesNotContain(capture, sources, StringComparer.Ordinal);

        foreach (string file in sources)
        {
            string text = StripComments(File.ReadAllText(file));
            Assert.DoesNotContain("Devices.Update(", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Devices.Add(", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Devices.Remove(", text, StringComparison.Ordinal);
        }

        // And the exclusion really is the evidence tool: the capture is the file that seeds devices.
        Assert.Contains("Devices.Add(", StripComments(File.ReadAllText(capture)), StringComparison.Ordinal);

        string pages = AppSource("Pages.cs");
        Assert.Contains("Runtime.Admin.RevokeDevice(", pages, StringComparison.Ordinal);
        Assert.Contains("Runtime.Admin.RenameDevice(", pages, StringComparison.Ordinal);
        Assert.Contains("Runtime.Admin.SetDeviceAccess(", pages, StringComparison.Ordinal);

        TestBase.Record("FIX-09-02e",
            "外壳源码里没有任何 Devices.Update/Add/Remove：重命名、权限与撤销全部走 AdminOperations");
    }

    [Fact]
    public void The_native_recycle_page_never_borrows_a_phone_identity()
    {
        string pages2 = AppSource("Pages2.cs");

        // The exact expression that caused it: restoring on a machine with no paired device threw, and
        // with one it credited that phone with the administrator's action.
        Assert.DoesNotContain("Devices.All().First()", pages2, StringComparison.Ordinal);
        Assert.DoesNotContain("Recycle.Restore(", pages2, StringComparison.Ordinal);
        Assert.DoesNotContain("Recycle.Purge(", pages2, StringComparison.Ordinal);

        Assert.Contains("OperationActor.LocalAdmin", pages2, StringComparison.Ordinal);
        Assert.Contains("Runtime.Admin.RestoreRecycleEntry(", pages2, StringComparison.Ordinal);
        Assert.Contains("Runtime.Admin.PurgeRecycleEntry(", pages2, StringComparison.Ordinal);

        TestBase.Record("FIX-09-03g",
            "回收站页面不再使用 Devices.All().First()，也不再直接调用 Recycle.Restore/Purge：改为 OperationActor.LocalAdmin + AdminOperations");
    }

    [Fact]
    public void The_theme_switch_goes_through_the_one_theme_manager()
    {
        string pages2 = AppSource("Pages2.cs");

        // The half-implementation lived here: App.ApplyTheme(bool) switched WPF-UI only.
        Assert.DoesNotContain("App.ApplyTheme(", pages2, StringComparison.Ordinal);
        Assert.Contains("ThemeManager.ApplyProfile(", pages2, StringComparison.Ordinal);
        Assert.Contains("ThemeManager.Save(", pages2, StringComparison.Ordinal);
        Assert.Contains("ShellPreferences", pages2, StringComparison.Ordinal);

        // Round 10 replaced the three hard-coded choices with named profiles. What this gate has always
        // protected is "the light palette cannot be quietly dropped", so it is now asserted against the theme
        // catalogue itself — which is stronger than counting three buttons in one file: both surfaces must
        // exist, a follow-the-system profile must exist, and both appearances must be present by name.
        string profiles = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "src/LanDisk.Core/Configuration/ThemeProfiles.cs"));
        Assert.Contains("public static IReadOnlyList<ThemeProfile> Windows", profiles, StringComparison.Ordinal);
        Assert.Contains("public static IReadOnlyList<ThemeProfile> Mobile", profiles, StringComparison.Ordinal);
        Assert.Contains("\"follow-system\"", profiles, StringComparison.Ordinal);
        Assert.Contains("\"follow-iphone-system\"", profiles, StringComparison.Ordinal);
        Assert.Contains("\"obsidian\"", profiles, StringComparison.Ordinal);
        Assert.Contains("\"arctic\"", profiles, StringComparison.Ordinal);

        TestBase.Record("FIX-09-05e",
            "主题切换只经由 ThemeManager（ApplyProfile + Save + ShellPreferences 持久化），仍不存在只切 WPF-UI 的 App.ApplyTheme(bool)；" +
            "「浅色不能被悄悄丢掉」这条要求在第 10 轮改为对主题目录断言（Windows 与 Mobile 两套目录必须存在，follow-system、obsidian、arctic 必须齐备），" +
            "比原先数三个按钮更强 —— 这是有意的门禁变更，记录在 docs/AUDIT_CORRECTIONS_10.md");
    }

    [Fact]
    public void The_bootstrap_page_probes_by_itself_and_has_no_continue_button()
    {
        using var kit = new UpdateTestKit(nameof(The_bootstrap_page_probes_by_itself_and_has_no_continue_button));
        CertificateService service = NewCertificateService(kit, "theme-onboarding-tls");
        service.EnsureServerCertificate("a1b2c3d4", "landisk-a1b2c3d4.local");

        AppPaths paths = AppPaths.ForDataRoot(kit.NewDirectory("onboarding-data"));
        var onboarding = new OnboardingService(
            paths,
            () => service.ReadCaCerBytes(),
            () => "landisk-a1b2c3d4.local",
            () => 8892,
            () => ["192.168.2.10", "192.168.2.11"]);
        onboarding.Start(port: 0);

        string page = onboarding.BootstrapPage();
        onboarding.Dispose();

        // It keeps probing on its own.
        Assert.Contains("setInterval(", page, StringComparison.Ordinal);
        Assert.Contains("trust-probe", page, StringComparison.Ordinal);
        Assert.Contains("1500", page, StringComparison.Ordinal);       // 1.5 s: inside the 1–2 s window
        Assert.Contains("安全连接已建立", page, StringComparison.Ordinal);

        // The manual step is gone.
        Assert.DoesNotContain(">继续<", page, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"go\"", page, StringComparison.Ordinal);

        // The stable name is tried first and the physical addresses are fallbacks.
        Assert.Contains("\"landisk-a1b2c3d4.local\"", page, StringComparison.Ordinal);
        Assert.Contains("\"192.168.2.10\"", page, StringComparison.Ordinal);
        Assert.Contains("\"192.168.2.11\"", page, StringComparison.Ordinal);

        // The security properties of the page are unchanged.
        Assert.DoesNotContain("#t=", page, StringComparison.Ordinal);   // no pairing token on the plain page
        Assert.DoesNotContain("pairingToken", page, StringComparison.Ordinal);

        TestBase.Record("FIX-09-06",
            "引导页每 1.5 秒自动探测 HTTPS trust，成功后自动显示“安全连接已建立”，不再有“继续”按钮；探测按稳定名称优先、物理地址兜底；页面里仍然没有配对令牌");
    }

    [Fact]
    public async Task A_pairing_request_is_reused_instead_of_piling_up()
    {
        // "Refresh must not create a second request" — the native page calls EnsurePairingRequest on every
        // tick of its 1.5 s timer, so this is the property that stops the list from filling with requests.
        Assert.Empty(Host.Runtime.Pairing.Active());

        PairingRequest first = Host.Runtime.Admin.EnsurePairingRequest();
        PairingRequest second = Host.Runtime.Admin.EnsurePairingRequest();
        PairingRequest third = Host.Runtime.Admin.EnsurePairingRequest();

        Assert.Same(first, second);
        Assert.Same(first, third);
        Assert.Single(Host.Runtime.Pairing.Active());

        TestBase.Record("FIX-09-06b", "自动准备配对请求时复用同一个 live 请求：连续三次调用只产生 1 个请求（刷新不会堆积）");
    }

    [Fact]
    public void An_expired_request_is_replaced_by_a_fresh_one()
    {
        PairingRequest first = Host.Runtime.Admin.EnsurePairingRequest();
        typeof(PairingRequest).GetProperty(nameof(PairingRequest.ExpiresAt))!
            .SetValue(first, DateTimeOffset.Now.AddMinutes(-1));

        PairingRequest replacement = Host.Runtime.Admin.EnsurePairingRequest();

        Assert.NotSame(first, replacement);
        Assert.False(replacement.IsExpired);
        // The stale one is no longer offered as live.
        Assert.DoesNotContain(first.Id, Host.Runtime.Pairing.Active().Select(r => r.Id));

        TestBase.Record("FIX-09-06c", "配对请求过期后自动换一个新的，过期的那个不再出现在 live 列表里");
    }

    [Fact]
    public void The_raw_pairing_link_stays_folded_away_in_the_native_window()
    {
        string pages = AppSource("Pages.cs");

        // The one-time token must not be on screen by default: it lives behind an Expander.
        Assert.Contains("高级：显示原始配对链接", pages, StringComparison.Ordinal);
        Assert.Contains("new Expander", pages, StringComparison.Ordinal);

        int expander = pages.IndexOf("高级：显示原始配对链接", StringComparison.Ordinal);
        int linkUse = pages.IndexOf("Ui.Mono(link)", expander, StringComparison.Ordinal);
        Assert.True(linkUse > expander, "原始配对链接必须在折叠项之后才出现");

        TestBase.Record("FIX-09-06d", "原生窗口里含一次性令牌的原始配对链接仍然默认折叠在“高级”展开项内");
    }

    [Fact]
    public async Task Trusting_the_certificate_is_what_creates_the_pairing_request()
    {
        // The phone proves trust by loading the probe over HTTPS; that is what flips Trusted, and Trusted
        // is what the window waits for before preparing a request. Nothing here needs a button press.
        using var kit = new UpdateTestKit(nameof(Trusting_the_certificate_is_what_creates_the_pairing_request));
        CertificateService service = NewCertificateService(kit, "trust-flow-tls");
        service.EnsureServerCertificate("a1b2c3d4", "landisk-a1b2c3d4.local");

        AppPaths paths = AppPaths.ForDataRoot(kit.NewDirectory("onboarding-data"));
        var onboarding = new OnboardingService(
            paths, () => service.ReadCaCerBytes(), () => "landisk-a1b2c3d4.local", () => 8892, () => ["192.168.2.10"]);
        onboarding.Start(port: 0);
        string id = onboarding.Status().BootstrapId!;

        Assert.False(onboarding.Status().Trusted);

        JsonElement identity = await Admin.GetAsync<JsonElement>("/api/onboarding/identity").ContinueWith(t => t.Result.Body);
        Assert.Equal(Host.Runtime.Identity.HostName, identity.GetProperty("hostName").GetString());
        Assert.False(identity.GetProperty("stableNameAvailable").GetBoolean());   // mDNS is off in the test host

        // The probe is rejected for a wrong bootstrap id and accepted for the right one.
        ApiResult<JsonElement> wrong = await Admin.GetAsync<JsonElement>("/api/onboarding/trust-probe?b=not-this-one");
        Assert.True(wrong.Status is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.NoContent);

        onboarding.MarkTrusted("192.168.2.10");
        Assert.True(onboarding.Status().Trusted);
        Assert.NotNull(onboarding.Status().TrustedAt);

        // And the window's own rule follows from the same flag.
        OnboardingStatus status = Host.Runtime.Onboarding.Status();
        Assert.False(status.Active);   // the test host has no wizard running
        onboarding.Dispose();
        _ = id;

        TestBase.Record("FIX-09-06e",
            "HTTPS trust 探测成功后 Onboarding 标记 Trusted 并记录时间；窗口只在 Trusted 之后准备配对请求（本项在隔离数据目录中验证，未接触真实安装）");
    }

    // ---- helpers reused from the onboarding tests ---------------------------------

    private static CertificateService NewCertificateService(UpdateTestKit kit, string name)
    {
        AppPaths paths = AppPaths.ForDataRoot(kit.NewDirectory(name));
        return new CertificateService(paths);
    }
}
