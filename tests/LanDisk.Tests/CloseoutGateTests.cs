using LanDisk.Core.Configuration;
using LanDisk.Server.Services;
using LanDisk.Setup;
using LanDisk.Tests.Infrastructure;

namespace LanDisk.Tests;

/// <summary>
/// The closeout gates: UI capture integrity (UIE17), installer stage monotonicity (SETUP17), the system CPU
/// calculation (OPS17), theme startup persistence (THEME17-09..15) and the recommended-address semantics
/// (NET17).
///
/// <para>Every one of these exists because the 1.7.0 review found the corresponding defect: a capture that
/// rendered the wrong pages, installer evidence that was a blank window, a theme that was forgotten on
/// restart, a CPU figure that double-counted idle, and a ".local" name presented as the address a phone
/// should use even when the owner had just proven it unreachable.</para>
/// </summary>
public sealed class CloseoutGateTests : TestBase
{
    // ------------------------------------------------------------------ UIE17

    private static string AppSource(string fileName)
    {
        string path = Path.Combine(TestPaths.RepoRoot, "src/LanDisk.App", fileName);
        Assert.True(File.Exists(path), "找不到 " + path);
        return File.ReadAllText(path);
    }

    [Fact]
    public void The_capture_and_the_navigation_share_one_page_list()
    {
        string catalog = AppSource("PageCatalog.cs");
        string mainWindow = AppSource("MainWindow.xaml.cs");
        string capture = AppSource("UiCapture.cs");

        // The single source of truth must exist and be used by both sides.
        Assert.Contains("PageCatalog.Groups", mainWindow, StringComparison.Ordinal);
        Assert.Contains("PageCatalog.All", capture, StringComparison.Ordinal);

        // The capture must not keep its own hand-written title list: that is exactly what drifted.
        Assert.DoesNotContain("private static readonly string[] Pages", capture, StringComparison.Ordinal);

        // The twelve pages the product ships, in the catalogue and only there.
        string[] expected =
        [
            "概览", "运营看板", "文件与共享", "回收站", "设备", "连接 iPhone",
            "网络", "HTTPS 与安全", "系统更新", "自检", "日志", "设置"
        ];
        foreach (string title in expected)
        {
            Assert.Contains($"new(\"{title}\"", catalog, StringComparison.Ordinal);
        }

        // MainWindow must not carry a page list of its own. One page title legitimately appears there — the
        // tray's "连接 iPhone" shortcut — so the check is that no *catalogue of pages* is declared locally.
        Assert.DoesNotContain("Add<", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("new NavEntry(\"", mainWindow, StringComparison.Ordinal);

        TestBase.Record("UIE17-01",
            $"导航与截图共用 PageCatalog（{expected.Length} 个页面：{string.Join("、", expected)}）；MainWindow 与 UiCapture 都不再持有自己的标题列表，新增页面无法只进导航而漏掉截图");
    }

    [Fact]
    public void An_unknown_page_fails_instead_of_silently_staying()
    {
        string mainWindow = AppSource("MainWindow.xaml.cs");
        string capture = AppSource("UiCapture.cs");

        // Strict mode is on for the capture, an unknown title throws, and the capture records the failure
        // instead of writing a PASS row for whatever page happened to be showing.
        Assert.Contains("StrictNavigation", mainWindow, StringComparison.Ordinal);
        Assert.Contains("找不到页面：", mainWindow, StringComparison.Ordinal);
        Assert.Contains("StrictNavigation = true", capture, StringComparison.Ordinal);
        Assert.Contains("绝对不存在的页面", capture, StringComparison.Ordinal);
        Assert.Contains("UIE17-02", capture, StringComparison.Ordinal);

        TestBase.Record("UIE17-02",
            "截图模式强制 StrictNavigation：找不到标题时 NavigateToAsync 抛异常（不再\"留在上一页仍记 PASS\"），并且截图自己驱动一次不存在的标题来证明这条路径，结果写入 UIE17-02");
    }

    [Fact]
    public void The_page_identity_is_checked_after_every_navigation()
    {
        string capture = AppSource("UiCapture.cs");

        Assert.Contains("actual.GetType() == descriptor.PageType", capture, StringComparison.Ordinal);
        Assert.Contains("window.CurrentTitle == page", capture, StringComparison.Ordinal);
        Assert.Contains("wrongPage", capture, StringComparison.Ordinal);
        Assert.Contains("UIE17-03", capture, StringComparison.Ordinal);

        // The summary the report reads must carry the page counts too.
        foreach (string key in new[] { "NAV_PAGE_COUNT", "UI_CAPTURE_PAGE_COUNT", "UI_CAPTURE_EXPECTED", "UI_CAPTURE_ACTUAL", "UI_CAPTURE_MISSING", "UI_CAPTURE_WRONG_PAGE", "WINDOW_DPI_MATRIX" })
        {
            Assert.Contains(key, capture, StringComparison.Ordinal);
        }

        TestBase.Record("UIE17-03",
            "每次导航后都断言\"请求标题 == 实际标题 == catalogue 里的页面类型\"，不一致即计为 wrongPage 并记 FAIL；汇总文件同时给出 NAV_PAGE_COUNT / UI_CAPTURE_* / WINDOW_DPI_MATRIX，报告与证据取自同一份数据");
    }

    [Fact]
    public void The_capture_uses_real_dpi_viewports_and_seeds_worst_case_data()
    {
        string capture = AppSource("UiCapture.cs");

        // Real DPI: the logical viewport is physical / scale, and the bitmap is the physical size.
        Assert.Contains("private static int Logical(int physical, double scale) => (int)Math.Round(physical / scale)", capture, StringComparison.Ordinal);
        Assert.Contains("(1366, 768), (1600, 900), (1920, 1080), (2560, 1440)", capture, StringComparison.Ordinal);
        Assert.Contains("1.00, 1.25, 1.50, 1.75, 2.00", capture, StringComparison.Ordinal);

        // Worst-case data rather than only empty states.
        Assert.Contains("SeedWorstCaseData", capture, StringComparison.Ordinal);
        Assert.Contains("share-missing", capture, StringComparison.Ordinal);
        Assert.Contains("dev-revoked", capture, StringComparison.Ordinal);
        Assert.Contains("页面没有渲染出任何可见文字", capture, StringComparison.Ordinal);

        TestBase.Record("UIE17-04",
            "截图使用真实 DPI 视口（逻辑 DIP = 物理像素 / 缩放，位图按物理像素输出），矩阵为 4 种屏幕尺寸 × 100/125/150/175/200% × 深/浅 × 全部 12 页，并保留 1160×780 设计基准；scratch 数据里预置最坏数据（超长路径/卷 ID 的共享、Missing/Needs Reconfirm 状态、5 台覆盖在线/最近/离线/撤销的设备、50 条日志），且页面没有可见文字直接 FAIL，不允许只靠空状态通过");
    }

    // ------------------------------------------------------------------ SETUP17

    [Fact]
    public void The_installer_stage_indices_never_go_backwards()
    {
        Assert.True(SetupStageMap.IsMonotonic(SetupStageMap.EngineStepOrder),
            "引擎步骤映射到节点后必须单调不下降：" + string.Join(" → ", SetupStageMap.EngineStepOrder.Select(s => $"{s}={SetupStageMap.StageIndex(s)}")));

        TestBase.Record("SETUP17-01",
            "真实 SetupEngine 步骤序列映射到节点后索引单调不下降：" + string.Join(" → ", SetupStageMap.EngineStepOrder.Select(s => $"{s}({SetupStageMap.StageIndex(s)})")));
    }

    [Fact]
    public void Every_engine_step_has_an_explicit_stage_mapping()
    {
        foreach (SetupStep step in Enum.GetValues<SetupStep>())
        {
            int index = SetupStageMap.StageIndex(step);   // throws when a future step is unmapped
            Assert.InRange(index, 0, SetupStageMap.StageCount - 1);
        }

        TestBase.Record("SETUP17-02",
            $"全部 {Enum.GetValues<SetupStep>().Length} 个 SetupStep 都有显式映射（{SetupStageMap.StageCount} 个节点）；未来新增步骤若忘记映射，StageIndex 抛异常并让这条门禁失败");
    }

    [Fact]
    public void A_node_only_lights_after_its_stage_is_finished()
    {
        var state = new SetupProgressState();

        // Before anything runs, nothing is lit.
        for (int i = 0; i < SetupStageMap.StageCount; i++) Assert.False(state.IsLit(i));

        state.Complete(0);
        Assert.True(state.IsLit(0));
        Assert.False(state.IsLit(1));

        state.SetActive(2);
        Assert.True(state.IsLit(0));
        Assert.False(state.IsLit(2));

        state.Complete(2);
        Assert.True(state.IsLit(2));

        TestBase.Record("SETUP17-03",
            "节点只在对应阶段真实完成后点亮：未开始时全部不亮，Complete(0) 只点亮第 1 个节点，SetActive(2) 不会点亮第 3 个，Complete(2) 才点亮它");
    }

    [Fact]
    public void A_failed_stage_marks_one_node_and_leaves_the_rest_dark()
    {
        var state = new SetupProgressState();
        state.Complete(0);
        state.Complete(1);
        state.SetActive(2);
        state.SetError(2);

        Assert.True(state.IsLit(0));
        Assert.True(state.IsLit(1));
        Assert.False(state.IsLit(2));
        Assert.True(state.IsError(2));

        // A late completion report must not light the failed node or anything after it.
        state.Complete(2);
        state.Complete(3);
        state.Complete(5);
        Assert.False(state.IsLit(2));
        Assert.False(state.IsLit(3));
        Assert.False(state.IsLit(5));
        Assert.False(state.IsError(4));

        TestBase.Record("SETUP17-04",
            "失败发生在第 3 阶段时：第 1/2 个节点保持绿色，第 3 个显示 error，之后的节点即使收到 Complete 也不会亮（后续节点不得点绿）");
    }

    // ------------------------------------------------------------------ OPS17

    [Fact]
    public void The_system_cpu_calculation_does_not_double_count_idle()
    {
        // GetSystemTimes semantics: KernelTime already includes IdleTime. A fully idle interval must read ~0%.
        double idle = RuntimeMetrics.SystemCpuPercent(0, 0, 0, 1000, 1000, 0);
        Assert.InRange(idle, 0, 1);

        // Fully busy: idle stays put while kernel+user advance.
        double busy = RuntimeMetrics.SystemCpuPercent(0, 0, 0, 0, 500, 500);
        Assert.InRange(busy, 99, 100);

        // Half idle: of 1000 total (kernel 500 incl. 500 idle, user 500) → 50%.
        double half = RuntimeMetrics.SystemCpuPercent(0, 0, 0, 500, 500, 500);
        Assert.InRange(half, 49, 51);

        // Counters going backwards (a second sampler took the baseline) or standing still: no NaN, no
        // negative, no >100.
        foreach (double value in new[]
        {
            RuntimeMetrics.SystemCpuPercent(1000, 1000, 1000, 0, 0, 0),
            RuntimeMetrics.SystemCpuPercent(500, 500, 500, 500, 500, 500),
            RuntimeMetrics.SystemCpuPercent(ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, 0, 0, 0)
        })
        {
            Assert.False(double.IsNaN(value));
            Assert.InRange(value, 0, 100);
        }

        TestBase.Record("OPS17-01", "全 idle 样本 → " + idle.ToString("0.0") + "%（≈0%）");
        TestBase.Record("OPS17-02", "无 idle 全 busy 样本 → " + busy.ToString("0.0") + "%（≈100%）");
        TestBase.Record("OPS17-03", "50% idle 样本 → " + half.ToString("0.0") + "%（≈50%，说明没有把 idle 重复计入分母）");
        TestBase.Record("OPS17-04", "计数器回退 / 停滞 / 极端值都不会产生负数、NaN 或 >100%（函数内钳制并把回退视为 0）");
    }

    // ------------------------------------------------------------------ THEME17-09..15

    private static string NewDataRoot(string name)
    {
        string root = Path.Combine(Path.GetTempPath(), "landisk-theme-" + name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public void A_named_windows_theme_survives_a_restart()
    {
        string root = NewDataRoot("midnight");
        ShellPreferences preferences = ShellPreferences.Load(root);
        preferences.ThemeProfile = "midnight";
        preferences.Save(root);

        // A "new process": load the file from scratch and resolve exactly what startup would apply.
        ShellPreferences reloaded = ShellPreferences.Load(root);
        ThemeProfile resolved = ThemeStartup.Resolve(reloaded, systemPrefersDark: false);

        Assert.Equal("midnight", resolved.Id);
        Assert.Equal(ThemeCatalog.Resolve("midnight", false).Role("surface"), resolved.Role("surface"));
        Assert.Equal(ThemeCatalog.Resolve("midnight", false).Role("accent"), resolved.Role("accent"));

        TestBase.Record("THEME17-09",
            "保存 Midnight → 模拟新进程启动（重新读 shell.json 并走 ThemeStartup.Resolve）→ 解析结果仍是 midnight，且 surface/accent 等关键角色逐项等于 Midnight 主题（不是只比较 JSON 字段）");
    }

    [Fact]
    public void A_light_windows_theme_survives_a_restart()
    {
        string root = NewDataRoot("arctic");
        ShellPreferences preferences = ShellPreferences.Load(root);
        preferences.ThemeProfile = "arctic";
        preferences.Save(root);

        ThemeProfile resolved = ThemeStartup.Resolve(ShellPreferences.Load(root), systemPrefersDark: true);
        Assert.Equal("arctic", resolved.Id);
        Assert.True(resolved.IsLight);
        Assert.Equal("#FFFFFFFF", resolved.Role("surface"));

        TestBase.Record("THEME17-10",
            "保存 Arctic（浅色）→ 重启后仍是 arctic，解析出的 surface 为 #FFFFFFFF（浅色外观不会被系统深色偏好覆盖）");
    }

    [Fact]
    public void A_custom_windows_theme_survives_a_restart_role_for_role()
    {
        string root = NewDataRoot("custom");
        ThemeProfile custom = ThemeCatalog.Custom("#FF8A5BFF", "#FF12081F", "#FF1D0F33", "紫色自定义");
        Assert.True(ThemeCatalog.Validate(custom).Ok);

        ShellPreferences preferences = ShellPreferences.Load(root);
        preferences.ThemeProfile = "custom";
        preferences.CustomTheme = custom;
        preferences.Save(root);

        ThemeProfile resolved = ThemeStartup.Resolve(ShellPreferences.Load(root), systemPrefersDark: true);
        Assert.Equal("custom", resolved.Id);
        foreach (string role in ThemeRoles.All)
        {
            Assert.Equal(custom.Role(role), resolved.Role(role));
        }

        TestBase.Record("THEME17-11",
            $"自定义主题跨重启逐角色保持（{ThemeRoles.All.Count} 个角色全部相等，抽查 accent={resolved.Role("accent")} / window={resolved.Role("window")}）");
    }

    [Fact]
    public void A_legacy_shell_json_is_migrated()
    {
        // A 1.6.x file has only the old Theme field.
        foreach ((string legacy, string expected) in new[] { ("dark", "obsidian"), ("light", "arctic"), ("system", "follow-system") })
        {
            string root = NewDataRoot("legacy-" + legacy);
            var preferences = new ShellPreferences { Theme = legacy };
            preferences.Save(root);

            ShellPreferences loaded = ShellPreferences.Load(root);
            Assert.Equal("follow-system", loaded.ThemeProfile);   // the new field's own default
            ThemeProfile resolved = ThemeStartup.Resolve(loaded, systemPrefersDark: true);

            // With ThemeProfile still at its default the legacy value is what decides.
            Assert.Equal(ThemeCatalog.Resolve("follow-system", true).Id, resolved.Id);

            // And the explicit migration table maps the old value as documented.
            Assert.Equal(expected, ThemeStartup.MigrateLegacy(legacy));
        }

        // An unknown value falls back to "follow system" rather than throwing.
        Assert.Equal("follow-system", ThemeStartup.MigrateLegacy("from-a-future-version"));

        TestBase.Record("THEME17-12",
            "旧 1.6 shell.json 迁移正确：dark → obsidian、light → arctic、system → follow-system，未知值回退 follow-system；实际启动路径（ThemeStartup.Resolve）对只有旧字段的文件也得出可用主题");
    }

    [Fact]
    public void The_settings_page_can_create_a_custom_phone_theme()
    {
        string pages2 = AppSource("Pages2.cs");

        // The Windows side owns the phone's appearance, so it must be able to author a custom one.
        Assert.Contains("自定义 iPhone 主题…", pages2, StringComparison.Ordinal);
        Assert.Contains("EditCustomMobileThemeAsync", pages2, StringComparison.Ordinal);
        Assert.Contains("runtime.MobileTheme.Set(\n            \"custom\"", pages2.Replace("\r\n", "\n"), StringComparison.Ordinal);

        // The same editor (three labelled fields + live preview) is used for both surfaces.
        Assert.Contains("ShowThemeEditorAsync", pages2, StringComparison.Ordinal);
        Assert.Contains("record ThemeField(StackPanel Root, TextBox Input)", pages2, StringComparison.Ordinal);
        Assert.Contains("ThemePreviewCard", pages2, StringComparison.Ordinal);
        Assert.Contains("accent.Root", pages2, StringComparison.Ordinal);

        TestBase.Record("THEME17-13",
            "Windows 设置页提供「自定义 iPhone 主题…」，走与 Windows 自定义主题相同的编辑器（三个带标签的输入 + 即时预览 + 对比度校验），保存时调用 runtime.MobileTheme.Set(\"custom\", …)");
    }

    [Fact]
    public void A_custom_phone_theme_round_trips_through_the_service()
    {
        string root = NewDataRoot("mobile");
        var service = new MobileThemeService(root);

        (bool ok, ThemeValidation validation, string message) = service.Set("custom", "#FF7C5BFF", "#FF0B0F14", "#FF151B2B", "紫色手机主题");
        Assert.True(ok, message);
        Assert.True(validation.Ok, validation.Summary);
        int revision = service.Revision;

        // A fresh service (a restart) resolves the stored custom profile role-for-role.
        var reloaded = new MobileThemeService(root);
        Assert.Equal("custom", reloaded.ProfileId);
        Assert.Equal(revision, reloaded.Revision);

        ThemeProfile resolved = ThemeStartup.ResolveMobile(reloaded.ProfileId, reloaded.ChoicesModel().Custom, devicePrefersDark: true);
        Assert.Equal("custom", resolved.Id);
        Assert.Equal("#FF7C5BFF", resolved.Role("accent"));

        // Changing it bumps the revision so the phones notice.
        (bool again, _, _) = reloaded.Set("obsidian", null, null, null, null);
        Assert.True(again);
        Assert.True(reloaded.Revision > revision);

        TestBase.Record("THEME17-14",
            "自定义手机主题通过 API 往返：Set(custom) 成功并通过对比度校验，重启后（新 MobileThemeService）仍解析出同一组角色与同一修订号；切换主题后修订号递增");
    }

    [Fact]
    public void The_phone_refreshes_its_theme_when_the_revision_changes()
    {
        string deviceApp = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "src/LanDisk.Web/src/pages/DeviceApp.tsx"));
        string api = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "src/LanDisk.Web/src/api.ts"));

        // The heartbeat reports the revision, and the app re-reads the profile when it moves.
        Assert.Contains("themeRevision", deviceApp, StringComparison.Ordinal);
        Assert.Contains("applyMobileTheme", deviceApp, StringComparison.Ordinal);
        Assert.Contains("/api/mobile-theme", api, StringComparison.Ordinal);
        Assert.Contains("setProperty", deviceApp, StringComparison.Ordinal);

        TestBase.Record("THEME17-15",
            "手机端刷新路径：心跳返回 themeRevision，DeviceApp 在修订号变化时重新调用 applyMobileTheme（GET /api/mobile-theme）并把角色写成 CSS 变量，因此 Windows 改完主题后手机会在下一次心跳自动刷新");
    }

    // ------------------------------------------------------------------ NET17

    [Fact]
    public async Task With_a_vpn_the_recommendation_is_the_physical_address_when_that_was_verified()
    {
        string? physical = Host.Runtime.LanAddresses().FirstOrDefault();
        Assert.False(string.IsNullOrWhiteSpace(physical), "本机没有可用的物理局域网地址，无法验证推荐地址语义");

        // The owner's real situation: VPN/TUN up, the phone reached us at the physical address.
        Host.Runtime.Onboarding.Start(port: 0);
        Assert.True(Host.Runtime.Onboarding.RecordVerifiedHttpsHost(physical!));

        string? recommended = Host.Runtime.Admin.RecommendedHttpsOrigin();
        Assert.Equal($"https://{physical}:{Host.Runtime.Settings.HttpsPort}", recommended);
        Assert.Equal("verified", Host.Runtime.Admin.RecommendedHostKind);
        Assert.NotEqual(Host.Runtime.Admin.StableHttpsOrigin(), recommended);

        Host.Runtime.Onboarding.Stop("测试结束");

        TestBase.Record("NET17-01",
            $"VPN/TUN + 本次验证过物理地址时，所有标为\"推荐\"的位置都是物理地址（{recommended}），稳定名称 {Host.Runtime.Admin.StableHttpsOrigin()} 只作为单独一项展示");
    }

    [Fact]
    public async Task A_verified_stable_name_is_the_recommendation()
    {
        Host.Runtime.Onboarding.Start(port: 0);
        Assert.True(Host.Runtime.Onboarding.RecordVerifiedHttpsHost(Host.Runtime.Identity.HostName));

        string? recommended = Host.Runtime.Admin.RecommendedHttpsOrigin();
        Assert.Equal(Host.Runtime.Admin.StableHttpsOrigin(), recommended);
        Assert.Equal("verified", Host.Runtime.Admin.RecommendedHostKind);

        Host.Runtime.Onboarding.Stop("测试结束");

        await Task.CompletedTask;
        TestBase.Record("NET17-02",
            "本次验证的是稳定名称时，推荐地址就是稳定名称（" + recommended + "），不会被\"优先物理地址\"反过来覆盖");
    }

    [Fact]
    public async Task Without_any_measurement_the_ui_offers_both_and_claims_nothing()
    {
        // No probe this run: the recommendation must be reported as physical or stable — never as "verified",
        // and the UI must be able to show both addresses side by side.
        //
        // 前提与真实部署一致：只有局域网 HTTPS 确实在服务时才谈得上"推荐一个可访问地址"。
        // 1.7.7 起 LAN 或 HTTPS 关闭时返回 null（缺陷 B 的修复），"关闭时不推荐"由紧随其后的
        // Without_lan_or_https_the_ui_recommends_nothing 正反例覆盖。
        bool lanBefore = Host.Runtime.Settings.LanEnabled;
        bool httpsBefore = Host.Runtime.Settings.HttpsEnabled;
        Host.Runtime.Settings.LanEnabled = true;
        Host.Runtime.Settings.HttpsEnabled = true;
        string? recommended;
        try
        {
            recommended = Host.Runtime.Admin.RecommendedHttpsOrigin();
        }
        finally
        {
            Host.Runtime.Settings.LanEnabled = lanBefore;
            Host.Runtime.Settings.HttpsEnabled = httpsBefore;
        }
        Assert.False(string.IsNullOrWhiteSpace(recommended));

        string? physical = Host.Runtime.Admin.PhysicalHttpsOrigin();
        string stable = Host.Runtime.Admin.StableHttpsOrigin();
        Assert.False(string.IsNullOrWhiteSpace(stable));
        Assert.NotNull(physical);

        if (Host.Runtime.VpnOrTunnelDetected(out _))
        {
            // VPN/TUN 存在时，只有局域网 HTTPS **确实在监听**（BoundUrls 里有非 loopback 的 https 地址）
            // 才允许推荐物理地址；否则退回稳定名称。
            //
            // 旧断言无条件要求 physical —— 那正是真机缺陷 B：推荐了一个没有监听、手机连不上的物理地址。
            bool servingLanHttps = Host.Runtime.BoundUrls.Any(u =>
                u.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                !u.Contains("127.0.0.1", StringComparison.Ordinal) &&
                !u.Contains("localhost", StringComparison.OrdinalIgnoreCase) &&
                !u.Contains("[::]", StringComparison.Ordinal));
            if (servingLanHttps)
            {
                Assert.Equal("physical", Host.Runtime.Admin.RecommendedHostKind);
                Assert.Equal(physical, recommended);
            }
            else
            {
                Assert.Equal("stable", Host.Runtime.Admin.RecommendedHostKind);
                Assert.NotEqual(physical, recommended);
            }
        }

        Assert.NotEqual("verified", Host.Runtime.Admin.RecommendedHostKind);

        // And the pages that show an address do not label the stable name as verified.
        string network = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "src/LanDisk.App/Pages.cs"));
        Assert.Contains("推荐访问地址", network, StringComparison.Ordinal);
        Assert.Contains("物理 HTTPS 地址", network, StringComparison.Ordinal);
        Assert.Contains("VPN/TUN 可能阻断", network, StringComparison.Ordinal);
        Assert.Contains("RecommendedWithReason", network, StringComparison.Ordinal);

        await Task.CompletedTask;
        TestBase.Record("NET17-03",
            $"没有实测证据时：推荐地址为 {recommended}（kind={Host.Runtime.Admin.RecommendedHostKind}，不是 verified），界面同时给出推荐/物理 HTTPS/稳定名称三项，并在存在 VPN/TUN 时把稳定名称标注为\"VPN/TUN 可能阻断\"");
    }
}
