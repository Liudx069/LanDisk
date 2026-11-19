using System.Text.Json;
using LanDisk.Core.Configuration;
using LanDisk.Core.Model;
using LanDisk.Server.Services;
using LanDisk.Tests.Infrastructure;

namespace LanDisk.Tests;

/// <summary>
/// WiFi removal (W01–W10) and the retired-capability compatibility contract (C01–C06).
///
/// <para><b>Why the capability slot is kept.</b> Bit <c>1 &lt;&lt; 1</c> used to mean "this phone may read
/// WiFi state and change the machine's wireless connection". The feature is gone, so the bit grants nothing —
/// but deleting the enum member would renumber every flag after it, and an existing <c>devices.json</c> would
/// then be silently reinterpreted (FileWrite would become "install extensions", and so on). The slot therefore
/// stays, renamed to say what it is, and these tests pin both halves of that decision: old records still load,
/// and the bit can never be granted again.</para>
/// </summary>
public sealed class WifiRemovalTests : TestBase
{
    private static string Repo(string relative) => Path.Combine(TestPaths.RepoRoot, relative.Replace('/', '\\'));

    private static string Source(params string[] relative)
    {
        string path = Path.Combine([TestPaths.RepoRoot, .. relative]);
        Assert.True(File.Exists(path), "找不到 " + path);
        return File.ReadAllText(path);
    }

    /// <summary>Every product source file under src, as (path, text). Tests are excluded on purpose: they
    /// legitimately name the retired service (the Ethernet/LAN fixtures use a WLAN adapter to prove it is
    /// *not* selected as the physical LAN), so scanning them would only find this very file.</summary>
    private static IEnumerable<(string Path, string Text)> ProductSources()
    {
        string root = Path.Combine(TestPaths.RepoRoot, "src");
        foreach (string file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
        {
            if (file.Contains(@"\obj\") || file.Contains(@"\bin\") || file.Contains(@"\node_modules\")) continue;
            if (!file.EndsWith(".cs") && !file.EndsWith(".ts") && !file.EndsWith(".tsx") && !file.EndsWith(".mjs")) continue;
            yield return (file, File.ReadAllText(file));
        }
    }

    // ------------------------------------------------------------------ W01 / W02 / W09

    [Fact]
    public void W01_W02_W09_no_wifi_page_in_the_catalog_or_the_app()
    {
        string catalog = Source("src", "LanDisk.App", "PageCatalog.cs");

        // 目录里不得再有 WiFi 页；其它网络相关页必须仍在（移除 WiFi ≠ 移除网络）。
        Assert.DoesNotContain("WifiPage", catalog, StringComparison.Ordinal);
        Assert.DoesNotContain("WiFi", catalog, StringComparison.Ordinal);
        Assert.Contains("NetworkPage", catalog, StringComparison.Ordinal);
        Assert.Contains("HttpsPage", catalog, StringComparison.Ordinal);

        // 整个 App 工程不得再有 WiFi 页/面板的实现或引用。
        foreach ((string path, string text) in ProductSources().Where(s => s.Path.Contains(@"\LanDisk.App\")))
        {
            Assert.DoesNotContain("WifiPage", text, StringComparison.Ordinal);
            Assert.DoesNotContain("WifiPanel", text, StringComparison.Ordinal);
            Assert.DoesNotContain("--wlan-fixture", text, StringComparison.Ordinal);
        }

        Record("W01", "Windows 左侧导航无 WiFi 项：PageCatalog.cs 无 WifiPage/WiFi，且 NetworkPage 与 HttpsPage 仍在");
        Record("W02", "不存在可访问的独立 WiFi 页面：整个 LanDisk.App 无 WifiPage 实现或引用");
        Record("W09", "UI 目录不再包含 WiFi 页（页面总数由 PageCatalog 单一事实源决定，矩阵随之同步）");
    }

    // ------------------------------------------------------------------ W03

    [Fact]
    public async Task W03_no_wifi_endpoints_exist()
    {
        string[] paths =
        [
            "/api/network/wifi",
            "/api/network/wifi/scan",
            "/api/network/wifi/connect",
            "/api/network/wifi/disconnect",
            "/api/network/wifi/forget",
            "/api/network/wifi/profile/auto-connect"
        ];

        foreach (string path in paths)
        {
            ApiResult<JsonElement> asAdmin = await Admin.GetAsync<JsonElement>(path);
            Assert.Equal(System.Net.HttpStatusCode.NotFound, asAdmin.Status);

            LanDiskClient device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite, "WiFi 已移除手机");
            ApiResult<JsonElement> asDevice = await device.GetAsync<JsonElement>(path);
            Assert.Equal(System.Net.HttpStatusCode.NotFound, asDevice.Status);
        }

        Record("W03", "WLAN 扫描/连接/断开/忘记/自动连接 全部不存在：管理端与设备端请求 6 条路径均 404");
    }

    // ------------------------------------------------------------------ W04

    [Fact]
    public void W04_no_windows_wlan_api_path_remains()
    {
        // 1) 结构：源码里不得再出现 wlanapi 或 WLAN 服务类型。
        foreach ((string path, string text) in ProductSources())
        {
            Assert.DoesNotContain("wlanapi", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("IWlanService", text, StringComparison.Ordinal);
            Assert.DoesNotContain("NativeWlanService", text, StringComparison.Ordinal);
            Assert.DoesNotContain("FakeWlanService", text, StringComparison.Ordinal);
        }

        // 2) 运行期：已构建的产品程序集里不得存在任何 WLAN 类型（比源码扫描更强 —— 就算有生成代码也躲不过）。
        var assemblies = new[] { typeof(LanDiskRuntime).Assembly, typeof(DeviceDefinition).Assembly };
        var wlanTypes = assemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => t.Name.Contains("Wlan", StringComparison.OrdinalIgnoreCase))
            .Select(t => t.FullName!)
            .ToArray();
        Assert.Empty(wlanTypes);

        // 3) 源码树里不得再有 WiFi 专属文件。
        foreach (string gone in new[]
        {
            "src/LanDisk.App/WifiPage.cs",
            "src/LanDisk.Core/Networking/WlanContracts.cs",
            "src/LanDisk.Core/Networking/FakeWlanService.cs",
            "src/LanDisk.Server/Services/NativeWlanService.cs",
            "src/LanDisk.Server/Endpoints/WifiEndpoints.cs",
            "tests/LanDisk.Tests/WifiManagementTests.cs",
            "tests/LanDisk.Tests/WifiPresentationTests.cs"
        })
        {
            Assert.False(File.Exists(Repo(gone)), "仍存在 WiFi 专属文件：" + gone);
        }

        Record("W04", "产品运行时不存在 Windows WLAN API 入口：源码 0 处 wlanapi/IWlanService/NativeWlanService，"
            + "已构建程序集中 0 个 WLAN 类型，7 个 WiFi 专属文件均已删除");
    }

    // ------------------------------------------------------------------ W10

    [Fact]
    public void W10_release_contracts_no_longer_require_wifi_evidence()
    {
        string verdict = Source("tools", "EvidenceVerdict.ps1");
        string privacy = Source("tools", "Test-DeliveryPrivacy.ps1");
        string binding = Source("tools", "New-EvidenceBinding.ps1");
        string aggregator = Source("tools", "Test-EvidenceAggregator.ps1");

        // 证据判据不得再要求已删除的 WLAN12 断言（否则就是"测试删了、契约还在要"）。
        // 断言的必须是**判据键名**本身，而不是文件里出现过 "WLAN12" 这几个字 ——
        // 注释里说明"不再要求 WLAN12"是应该保留的信息，不该被当成违规。
        foreach (string tool in new[] { verdict, privacy, binding })
        {
            Assert.DoesNotContain("WLAN12-01_NATIVE_WLAN_CALLS", tool, StringComparison.Ordinal);
            Assert.DoesNotContain("WLAN12-02_FIXTURE_SENTINEL_SEEN", tool, StringComparison.Ordinal);
            Assert.DoesNotContain("UI_CAPTURE_MODE_WLAN_SOURCE", tool, StringComparison.Ordinal);
        }

        // 原 WLAN12 的位置改为真实 LAN 判据。
        Assert.Contains("R2_NATIVE_UI_RENDER", verdict, StringComparison.Ordinal);
        Assert.Contains("R2_VISIBLE_ADAPTER", verdict, StringComparison.Ordinal);
        Assert.Contains("R2_EMPTY_STATE_VISIBLE", verdict, StringComparison.Ordinal);

        // 负向能力必须继续存在（门禁不能变成恒绿）。
        Assert.Contains("R2_NATIVE_UI_RENDER`tFAIL", aggregator, StringComparison.Ordinal);
        Assert.Contains("R2_VISIBLE_ADAPTER`tfalse", aggregator, StringComparison.Ordinal);

        Record("W10", "ReleaseBlockingChain/EvidenceAggregator/VerifiedReports/Bundle/Privacy 均不再要求 WLAN/WiFi 证据；"
            + "原判据位置改用 R2 网络页夹具断言，且 N7/N8 负向注入仍在（门禁非恒绿）");
    }

    // ------------------------------------------------------------------ C04

    [Fact]
    public void C04_live_capability_bits_did_not_shift()
    {
        // 退役槽位改名不得让后面的位位移 —— 位移会让旧 devices.json 被静默重新解释。
        Assert.Equal(1 << 1, (int)DeviceCapability.RetiredNetworkManage);
        Assert.Equal(1 << 0, (int)DeviceCapability.FileWrite);
        Assert.Equal(1 << 2, (int)DeviceCapability.ExtensionInstall);
        Assert.Equal(1 << 3, (int)DeviceCapability.ExtensionExecute);
        Assert.Equal(1 << 4, (int)DeviceCapability.SystemView);

        Record("C04", "能力位未位移：FileWrite=1、retired=2、ExtensionInstall=4、ExtensionExecute=8、SystemView=16");
    }

    // ------------------------------------------------------------------ C01 / C05

    [Fact]
    public async Task C01_C05_old_device_record_loads_and_never_exposes_manage_network()
    {
        // 旧记录：capabilities = FileWrite(1) | retired NetworkManage(2) | SystemView(16) = 19。
        // 必须先配对（测试主机初始没有任何设备），再把它改写成"旧版本留下来的样子"。
        await PairDeviceAsync(DeviceAccessLevel.ReadOnly, "旧版本手机");
        DeviceDefinition device = Host.Runtime.Devices.All().Single();

        Host.Runtime.Devices.Update(device.Id, d => d.Capabilities =
            DeviceCapability.FileWrite | DeviceCapability.RetiredNetworkManage | DeviceCapability.SystemView);

        // 反序列化路径：旧文件里那个位仍然能读回来，且**其它位完全不变**。
        // 用**产品自己的读取 API**（JsonStore.Read，带 JsonStringEnumConverter）读回，
        // 而不是测试自己拼一套反序列化 —— 后者会在枚举以字符串落盘时直接抛异常，
        // 那验证的是测试的假设，不是产品的格式。
        List<DeviceDefinition> fromDiskAll = JsonStore.Read<List<DeviceDefinition>>(
            Path.Combine(Host.DataRoot, "devices.json"), () => []);
        DeviceDefinition fromDisk = fromDiskAll.Single(d => d.Id == device.Id);
        Assert.Equal(
            DeviceCapability.FileWrite | DeviceCapability.RetiredNetworkManage | DeviceCapability.SystemView,
            fromDisk.Capabilities);

        // 旧位不授予任何东西：权限计算里它不产生任何效果。
        DevicePermissions permissions = DevicePermissions.For(fromDisk);
        Assert.True(permissions.WriteFiles);      // 来自 FileWrite
        Assert.True(permissions.ViewSystem);      // 来自 SystemView
        Assert.False(permissions.InstallExtensions);
        Assert.False(permissions.ExecuteExtensions);

        // C05：/api/me 与心跳不得再暴露 manageNetwork。
        LanDiskClient phone = await PairDeviceAsync(DeviceAccessLevel.ReadOnly, "兼容手机");
        JsonElement me = await phone.MeAsync();
        Assert.False(me.GetProperty("permissions").TryGetProperty("manageNetwork", out _));
        JsonElement beat = (await phone.PostAsync<JsonElement>("/api/heartbeat", new { })).Body;
        Assert.False(beat.GetProperty("permissions").TryGetProperty("manageNetwork", out _));

        Record("C01", "旧 devices.json 含退役位 2 可反序列化，且 FileWrite/SystemView 等其它位完全不变");
        Record("C05", "/api/me 与 /api/heartbeat 的 permissions 均不再包含 manageNetwork 字段");
    }

    // ------------------------------------------------------------------ C02 / C03 / C06

    [Fact]
    public async Task C02_C03_C06_legacy_name_is_accepted_but_grants_nothing()
    {
        LanDiskClient phone = await PairDeviceAsync(DeviceAccessLevel.ReadOnly, "旧客户端手机");
        DeviceDefinition device = Host.Runtime.Devices.All().Single(d => d.Name == "旧客户端手机");

        // C02：旧名字不再导致 400（旧客户端/脚本不会因为一个已删除的能力而整批失败）……
        OperationOutcome<DeviceDefinition> result =
            Host.Runtime.Admin.SetDeviceCapabilities(device.Id, ["NetworkManage", "SystemView"]);
        Assert.True(result.Ok, result.Message);

        // ……但它什么也不授予：退役位必须是 0，只有真正存在的 SystemView 生效。
        Assert.Equal(DeviceCapability.SystemView, result.Value!.Capabilities);
        Assert.False(result.Value.Capabilities.HasFlag(DeviceCapability.RetiredNetworkManage));

        // C03：重新保存后，退役位不会被写回有效 capabilities。
        List<DeviceDefinition> fromDiskAll = JsonStore.Read<List<DeviceDefinition>>(
            Path.Combine(Host.DataRoot, "devices.json"), () => []);
        DeviceDefinition fromDisk = fromDiskAll.Single(d => d.Id == device.Id);
        Assert.Equal(DeviceCapability.SystemView, fromDisk.Capabilities);

        // C06：就算旧客户端反复声明 NetworkManage，也不存在任何可重新打开的 WiFi API。
        Host.Runtime.Admin.SetDeviceCapabilities(device.Id, ["NetworkManage"]);
        ApiResult<JsonElement> wifi = await phone.GetAsync<JsonElement>("/api/network/wifi");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, wifi.Status);

        Record("C02", "旧名 NetworkManage 经既有 API 传入不再返回 400，但退役位不授予任何能力");
        Record("C03", "旧记录重新保存后退役位不写回有效 capabilities");
        Record("C06", "旧客户端声明 NetworkManage 也无法重新打开任何 WiFi API（路径已不存在，404）");
    }
}
