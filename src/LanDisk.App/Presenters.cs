using LanDisk.Core.Model;
using LanDisk.Core.Networking;
using LanDisk.Server.Services;
using LanDisk.Update;

namespace LanDisk.App;

/// <summary>What the overview card shows.</summary>
internal sealed record OverviewModel(
    string Version,
    string BuildCommit,
    string DataRoot,
    string HostName,
    bool StableNamePublished,
    bool HttpsEnabled,
    bool LanEnabled,
    IReadOnlyList<string> PhysicalAddresses,
    string HttpsUrl,
    int ShareCount,
    int EnabledShareCount,
    int DeviceCount,
    int PendingPairings,
    int RecycleCount,
    bool VpnDetected,
    string VpnDetails,
    string ServerSummary,
    string IphoneStep);

internal sealed record ShareRow(string 名称, string 路径, string 卷, string 状态, string 权限, string 启用);
internal sealed record DeviceRow(string 设备, string 权限, string 状态, string 最近访问, string 最近地址);
internal sealed record RecycleRow(string 名称, string 原位置, string 大小, string 删除时间);
internal sealed record LogRow(string 时间, string 类别, string 操作, string 来源, string 详情, string 结果);

/// <summary>
/// Turns the running server's own objects into what the pages display.
///
/// <para>This is a read-only projection on purpose. Every action goes through
/// <see cref="AdminOperations"/> or the runtime service that owns it, so the native UI cannot develop
/// its own opinion about what is valid.</para>
/// </summary>
internal static class Presenters
{
    public static OverviewModel Overview(LanDiskRuntime runtime)
    {
        // The overview, the network page, self-check and the operations dashboard all read this same
        // reading, so they cannot end up describing two different machines.
        PhysicalLanSnapshot lan = runtime.Physical;
        IReadOnlyList<PhysicalLanAdapter> physical = lan.Accepted;
        bool vpn = lan.VpnOrTunnelDetected;
        string vpnDetails = lan.VpnDetails;

        List<ShareDefinition> shares = runtime.Shares.All().ToList();
        List<DeviceDefinition> devices = runtime.Devices.All().Where(d => !d.Revoked).ToList();

        string httpsUrl = physical.Count > 0
            ? $"https://{physical[0].Address}:{runtime.Settings.HttpsPort}/"
            : "（没有可用的物理局域网地址）";

        string iphoneStep = runtime.Onboarding.IsActive
            ? (runtime.Onboarding.Trusted
                ? "证书已受信任，可以扫描配对二维码"
                : "请用 iPhone 扫描首次连接二维码并完成证书信任")
            : (devices.Count > 0 ? $"已配对 {devices.Count} 台设备" : "尚未配对任何设备");

        return new OverviewModel(
            BuildInfo.Version,
            BuildInfo.Commit,
            runtime.Paths.DataRoot,
            runtime.Identity.HostName,
            runtime.Mdns is { IsRunning: true },
            runtime.Settings.HttpsEnabled,
            runtime.Settings.LanEnabled,
            physical.Select(p => p.Address).ToList(),
            httpsUrl,
            shares.Count,
            shares.Count(s => s.Enabled),
            devices.Count,
            runtime.Pairing.Active().Count,
            runtime.Recycle.List().Count(e => e.State == RecycleState.Committed),
            vpn,
            vpnDetails,
            runtime.BoundUrls.Count > 0 ? string.Join("、", runtime.BoundUrls) : "未监听",
            iphoneStep);
    }

    public static IReadOnlyList<ShareRow> Shares(LanDiskRuntime runtime)
        => runtime.Shares.All()
            .Select(s => new ShareRow(
                s.DisplayName,
                s.RootPath,
                string.IsNullOrEmpty(s.VolumeGuidPath) ? "—" : s.VolumeGuidPath,
                s.State switch
                {
                    ShareState.Online => "在线",
                    ShareState.Offline => "离线",
                    ShareState.IdentityMismatch => "卷身份不符",
                    ShareState.PermissionDenied => "无权限",
                    _ => "未知"
                },
                Capabilities(s.Capabilities),
                s.Enabled ? "是" : "否"))
            .ToList();

    public static string Capabilities(ShareCapabilities capabilities)
    {
        var parts = new List<string>();
        if (capabilities.HasFlag(ShareCapabilities.Read)) parts.Add("读取");
        if (capabilities.HasFlag(ShareCapabilities.Upload)) parts.Add("上传");
        if (capabilities.HasFlag(ShareCapabilities.Rename)) parts.Add("重命名");
        if (capabilities.HasFlag(ShareCapabilities.Delete)) parts.Add("删除");
        return parts.Count == 0 ? "无" : string.Join("+", parts);
    }

    public static IReadOnlyList<DeviceRow> Devices(LanDiskRuntime runtime)
        => runtime.Devices.All()
            .OrderByDescending(d => d.LastSeenAt)
            .Select(d => new DeviceRow(
                d.Name,
                d.Level == DeviceAccessLevel.ReadWrite ? "读写" : "只读",
                d.Revoked ? "已撤销" : "有效",
                d.LastSeenAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                d.LastSeenIp ?? "—"))
            .ToList();

    public static IReadOnlyList<AdapterRow> Adapters(LanDiskRuntime runtime)
        => runtime.Physical.Adapters
            .Select(a => new AdapterRow(
                a.Name,
                a.Address,
                a.IsPhysical ? "用于手机访问" : "已排除",
                a.Reason,
                a.IsPhysical))
            .ToList();

    public static IReadOnlyList<RecycleRow> Recycle(LanDiskRuntime runtime)
        => runtime.Recycle.List()
            .Where(e => e.State == RecycleState.Committed)
            .OrderByDescending(e => e.DeletedAt)
            .Select(e => new RecycleRow(
                e.DisplayName,
                e.OriginalRelativePath,
                FormatBytes(e.Length),
                e.DeletedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")))
            .ToList();

    public static IReadOnlyList<LogRow> Audit(LanDiskRuntime runtime, int limit = 200)
        => runtime.Audit.Tail(limit)
            .Select(e => new LogRow(
                e.At.ToLocalTime().ToString("MM-dd HH:mm:ss"),
                e.Category,
                e.Action,
                e.DeviceName ?? "—",
                Truncate(e.Detail ?? e.RelativePath ?? string.Empty, 80),
                e.Success ? "成功" : "失败"))
            .ToList();

    /// <summary>
    /// The self check the native UI shows, built from the same runtime objects the server uses. It is
    /// deliberately explicit about *why* something is not usable, because "does not work" with no
    /// explanation is what sends a user to a forum.
    /// </summary>
    public static IReadOnlyList<(string Item, string State, string Detail)> SelfCheck(LanDiskRuntime runtime)
    {
        var rows = new List<(string, string, string)>();

        rows.Add(("服务", runtime.BoundUrls.Count > 0 ? "通过" : "未运行",
            runtime.BoundUrls.Count > 0 ? string.Join("、", runtime.BoundUrls) : "服务尚未监听"));

        IReadOnlyList<PhysicalLanAdapter> physical = runtime.Physical.Accepted;
        rows.Add(("物理局域网", physical.Count > 0 ? "通过" : "失败",
            physical.Count > 0
                ? string.Join("、", physical.Select(p => $"{p.Address}（{p.Name}）"))
                : "没有找到物理 Ethernet/Wi-Fi 地址，手机无法连接"));

        rows.Add(("稳定名称 (mDNS)", runtime.Mdns is { IsRunning: true } ? "通过" : "不可用",
            runtime.Mdns is { IsRunning: true }
                ? runtime.Identity.HostName + "，已绑定物理网卡"
                : (runtime.Mdns?.LastError ?? "mDNS 未启动；可用 IP 地址访问，地址变化后可能需要重新配对")));

        bool vpn = runtime.VpnOrTunnelDetected(out string vpnDetails);
        rows.Add(("VPN / TUN", vpn ? "提示" : "通过",
            vpn
                ? $"{vpnDetails}。LanDisk 只使用物理网卡；若手机仍无法连接，请在 VPN 中开启“允许局域网 / 绕过本地网络”"
                : "未检测到处于连接状态的 VPN/TUN 网卡"));

        rows.Add(("HTTPS", runtime.Settings.HttpsEnabled ? "通过" : "未启用",
            runtime.Settings.HttpsEnabled
                ? $"端口 {runtime.Settings.HttpsPort}，证书由 LanDisk 本地 CA 签发"
                : "未启用 HTTPS 时手机侧无法安全配对"));

        CertificateInfo? certificate = runtime.Certificate;
        rows.Add(("证书", certificate is null ? "未知" : "通过",
            certificate is null
                ? "尚未生成证书"
                : $"根 CA {certificate.RootCaSubject}，有效期至 {certificate.RootCaNotAfter:yyyy-MM-dd}"));

        // The recommendation and the adapter list must agree, and this row is where that is checked rather
        // than assumed. If a physical origin is recommended, the same reading must contain that adapter.
        _ = runtime.Admin.RecommendedHttpsOrigin();
        string recommended = runtime.Admin.RecommendedPhysicalAdapter ?? string.Empty;
        bool physicalRecommended = runtime.Admin.RecommendedHostKind == "physical";
        rows.Add(("推荐地址与网卡一致",
            physicalRecommended ? (recommended.Length > 0 ? "通过" : "失败") : "通过",
            physicalRecommended
                ? (recommended.Length > 0
                    ? $"推荐的是物理地址，对应网卡：{recommended}"
                    : "推荐了物理地址，但同一份读数里找不到对应网卡（状态不一致）")
                : $"推荐的是 {runtime.Admin.RecommendedHostKind}，不涉及物理网卡"));

        // The firewall row is a measurement, not a reminder. It printed the same "需要放行 …" line
        // whether or not the rules existed, which is why a machine with no rules looked identical to a
        // healthy one and the Owner read that line as an unfixable error.
        FirewallReport firewallReport = FirewallStatus.Check(
            runtime.Settings.HttpsPort, Core.Networking.FirewallRules.DefaultBootstrapPort);
        foreach (var row in FirewallRows(firewallReport))
        {
            rows.Add(row);
        }

        rows.Add(("共享", runtime.Shares.All().Any(s => s.Enabled) ? "通过" : "未配置",
            runtime.Shares.All().Any(s => s.Enabled)
                ? $"{runtime.Shares.All().Count(s => s.Enabled)} 个共享已启用"
                : "尚未共享任何目录，手机端会看到空列表"));

        return rows;
    }

    /// <summary>
    /// The firewall rows, produced by the shared pure mapper in Core so the wording and the three-way state
    /// (通过 / 需修复 / 无法确认) are pinned by tests. This wrapper keeps the call site readable.
    /// </summary>
    public static IReadOnlyList<(string Name, string State, string Detail)> FirewallRows(FirewallReport report)
        => Core.Networking.FirewallRules.Rows(report);

    public static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..(max - 1)] + "…";

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{bytes} B" : $"{value:0.##} {units[unit]}";
    }
}
