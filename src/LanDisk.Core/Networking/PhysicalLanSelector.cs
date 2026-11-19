using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LanDisk.Core.Networking;

/// <summary>Why an adapter was accepted or rejected, in a form the UI can show verbatim.</summary>
public sealed record AdapterVerdict(
    string Name,
    string Description,
    string Address,
    int InterfaceIndex,
    bool IsPhysical,
    int Score,
    string Reason);

/// <summary>
/// One adapter as the classifier sees it. Kept as plain data so every rule below can be tested
/// against the shapes that actually break in the field — a Wintun VPN holding the default route, a
/// Hyper-V switch on 172.16/12, a Wi-Fi adapter sitting on APIPA — without needing those adapters to
/// be present on the build machine.
/// </summary>
public sealed record AdapterSnapshot(
    string Name,
    string Description,
    NetworkInterfaceType Type,
    OperationalStatus Status,
    string Address,
    string SubnetMask,
    int InterfaceIndex,
    bool HasGateway,
    bool IsDhcpAssigned,
    long Speed,
    string DnsSuffix);

/// <summary>
/// Picks the interface a phone can actually reach.
///
/// <para><b>Why this exists.</b> A machine with a global VPN/TUN is the normal case, not the
/// exception: the tunnel takes the default route, and it also hands out a private-looking address
/// (iKuuuVPN uses 198.18.0.1/16). Treating every "Up interface with an address" as a LAN address is
/// what put a Hyper-V switch address into the server certificate's SAN and left the product
/// advertising itself on interfaces a phone cannot use. The symptom the user reported — the stable
/// <c>.local</c> name failing exactly while the VPN was up — is the visible end of that.</para>
///
/// <para><b>How it decides.</b> Several weak signals are combined rather than trusting names: the
/// adapter type Windows reports, whether it is up, whether it has a default gateway, whether the
/// address is RFC1918 and not the benchmark range VPNs borrow, whether the address came from DHCP,
/// the reported link speed, and finally name/description markers. A name alone never decides on its
/// own, because "Ethernet" is what a tunnel driver often calls itself.</para>
/// </summary>
public static class PhysicalLanSelector
{
    /// <summary>
    /// 198.18.0.0/15 is reserved for benchmarking. Tunnel products use it as their fake client subnet,
    /// which is exactly why it must never be treated as "a private LAN address a phone could use".
    /// </summary>
    public static bool IsBenchmarkRange(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        byte[] b = address.GetAddressBytes();
        return b[0] == 198 && (b[1] == 18 || b[1] == 19);
    }

    public static bool IsApipa(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        byte[] b = address.GetAddressBytes();
        return b[0] == 169 && b[1] == 254;
    }

    private static readonly string[] VirtualMarkers =
    [
        "vethernet", "hyper-v", "wsl", "docker", "virtualbox", "vmware", "loopback",
        "tap-", "tun", "tap", "wireguard", "wintun", "openvpn", "zerotier", "tailscale",
        "hamachi", "ikuuu", "bluetooth", "npcap", "cisco anyconnect", "forticlient",
        "proton", "radmin", "default switch", "nat network", "host-only",
        "wan miniport", "wi-fi direct", "virtual adapter", "pseudo-interface",
        "kernel debug", "teredo", "6to4", "ip-https", "vswitch", "virtual switch",
        "loopback adapter", "microsoft km-test", "sangfor", "easyconnect", "clash", "v2ray"
    ];

    /// <summary>True when the adapter looks like something a phone on the LAN cannot reach.</summary>
    public static bool LooksVirtual(AdapterSnapshot adapter)
    {
        string haystack = (adapter.Name + " " + adapter.Description).ToLowerInvariant();
        if (VirtualMarkers.Any(marker => haystack.Contains(marker, StringComparison.Ordinal))) return true;

        return adapter.Type is NetworkInterfaceType.Loopback
            or NetworkInterfaceType.Tunnel
            or NetworkInterfaceType.Wwanpp
            or NetworkInterfaceType.Wwanpp2;
    }

    /// <summary>True when the adapter carries a link-local-only address (nothing was assigned to it).</summary>
    public static bool IsLinkLocalOnly(AdapterSnapshot adapter)
        => IPAddress.TryParse(adapter.Address, out IPAddress? ip) && IsApipa(ip);

    /// <summary>
    /// Scores one adapter. Higher is better; anything at or below <see cref="RejectionThreshold"/> is
    /// not usable, and the reason string says which rule produced that.
    /// </summary>
    public static AdapterVerdict Classify(AdapterSnapshot adapter)
    {
        var reasons = new List<string>();
        int score = 0;

        if (!IPAddress.TryParse(adapter.Address, out IPAddress? ip))
        {
            return new AdapterVerdict(adapter.Name, adapter.Description, adapter.Address, adapter.InterfaceIndex,
                false, int.MinValue, "地址无法解析，不予考虑");
        }

        if (adapter.Status != OperationalStatus.Up)
        {
            return new AdapterVerdict(adapter.Name, adapter.Description, adapter.Address, adapter.InterfaceIndex,
                false, int.MinValue, "网卡未连接（" + adapter.Status + "）");
        }

        if (IsLinkLocalOnly(adapter))
        {
            return new AdapterVerdict(adapter.Name, adapter.Description, adapter.Address, adapter.InterfaceIndex,
                false, int.MinValue, "只有 169.254.x 自动地址，说明没有拿到 DHCP 地址");
        }

        if (IsBenchmarkRange(ip))
        {
            return new AdapterVerdict(adapter.Name, adapter.Description, adapter.Address, adapter.InterfaceIndex,
                false, int.MinValue, "属于 198.18.0.0/15 基准测试网段，是 VPN/TUN 的虚拟地址，手机无法访问");
        }

        if (!LanInfo.IsPrivate(ip))
        {
            return new AdapterVerdict(adapter.Name, adapter.Description, adapter.Address, adapter.InterfaceIndex,
                false, int.MinValue, "不是私有网段地址，LanDisk 不在公网地址上提供服务");
        }

        if (LooksVirtual(adapter))
        {
            return new AdapterVerdict(adapter.Name, adapter.Description, adapter.Address, adapter.InterfaceIndex,
                false, int.MinValue, "虚拟或隧道网卡（" + adapter.Description + "），手机通常无法访问");
        }

        // From here the adapter is a candidate; the score decides which one wins when there are several.
        switch (adapter.Type)
        {
            case NetworkInterfaceType.Ethernet:
            case NetworkInterfaceType.GigabitEthernet:
            case NetworkInterfaceType.FastEthernetT:
            case NetworkInterfaceType.FastEthernetFx:
            case NetworkInterfaceType.Ethernet3Megabit:
                score += 100;
                reasons.Add("有线以太网");
                break;

            case NetworkInterfaceType.Wireless80211:
                score += 80;
                reasons.Add("Wi-Fi");
                break;

            case NetworkInterfaceType.Unknown:
                score += 10;
                reasons.Add("类型未知");
                break;

            default:
                score += 5;
                reasons.Add("类型 " + adapter.Type);
                break;
        }

        if (adapter.HasGateway)
        {
            score += 40;
            reasons.Add("有默认网关");
        }
        else
        {
            score -= 30;
            reasons.Add("没有默认网关");
        }

        if (adapter.IsDhcpAssigned)
        {
            score += 20;
            reasons.Add("地址由 DHCP 分配");
        }

        if (adapter.Speed >= 100_000_000)
        {
            score += 10;
            reasons.Add("链路速率 " + (adapter.Speed / 1_000_000) + " Mbps");
        }

        if (!string.IsNullOrWhiteSpace(adapter.DnsSuffix))
        {
            score += 5;
            reasons.Add("DNS 后缀 " + adapter.DnsSuffix);
        }

        return new AdapterVerdict(adapter.Name, adapter.Description, adapter.Address, adapter.InterfaceIndex,
            true, score, string.Join("；", reasons));
    }

    public const int RejectionThreshold = 0;

    /// <summary>Best adapter first, rejected ones included with their reason so the UI can explain.</summary>
    public static IReadOnlyList<AdapterVerdict> Rank(IEnumerable<AdapterSnapshot> adapters)
        => adapters.Select(Classify)
            .OrderByDescending(v => v.IsPhysical)
            .ThenByDescending(v => v.Score)
            .ThenBy(v => v.Address, StringComparer.Ordinal)
            .ToList();

    /// <summary>The addresses a phone may be told to use, best first.</summary>
    public static IReadOnlyList<AdapterVerdict> PhysicalCandidates(IEnumerable<AdapterSnapshot> adapters)
        => Rank(adapters).Where(v => v.IsPhysical).ToList();

    public static IReadOnlyList<AdapterVerdict> Rejected(IEnumerable<AdapterSnapshot> adapters)
        => Rank(adapters).Where(v => !v.IsPhysical).ToList();

    // ------------------------------------------------------------------ live system

    /// <summary>Reads the live adapters. One address per adapter; several addresses on one NIC is rare here.</summary>
    public static IReadOnlyList<AdapterSnapshot> Snapshot()
    {
        var result = new List<AdapterSnapshot>();

        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            IPInterfaceProperties props;
            try { props = nic.GetIPProperties(); }
            catch { continue; }

            bool hasGateway = props.GatewayAddresses.Any(g => g?.Address is not null
                                                              && !g.Address.Equals(IPAddress.Any)
                                                              && !g.Address.Equals(IPAddress.IPv6Any));

            int index = -1;
            try { index = props.GetIPv4Properties()?.Index ?? -1; } catch { }

            long speed = 0;
            try { speed = nic.Speed; } catch { }

            foreach (UnicastIPAddressInformation addr in props.UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                result.Add(new AdapterSnapshot(
                    nic.Name,
                    nic.Description,
                    nic.NetworkInterfaceType,
                    nic.OperationalStatus,
                    addr.Address.ToString(),
                    addr.IPv4Mask?.ToString() ?? "255.255.255.0",
                    index,
                    hasGateway,
                    addr.PrefixOrigin == PrefixOrigin.Dhcp,
                    speed,
                    props.DnsSuffix ?? string.Empty));
            }
        }

        return result;
    }

    /// <summary>The physical addresses a phone can use right now, best first. Empty when there are none.</summary>
    public static IReadOnlyList<AdapterVerdict> PhysicalAddresses() => PhysicalCandidates(Snapshot());

    /// <summary>The single best address, or null when the machine has no usable LAN interface.</summary>
    public static AdapterVerdict? Best() => PhysicalAddresses().FirstOrDefault();

    /// <summary>
    /// True when a tunnel or VPN adapter is present and up. Used only to explain the situation in the
    /// UI — LanDisk never changes routing, never disables a VPN and never touches a third party's
    /// configuration.
    /// </summary>
    public static bool VpnOrTunnelDetected(out string details)
    {
        var tunnels = Snapshot()
            .Where(a => a.Status == OperationalStatus.Up)
            .Where(a => a.Type != NetworkInterfaceType.Loopback)
            .Where(a => !(IPAddress.TryParse(a.Address, out IPAddress? ip) && IPAddress.IsLoopback(ip)))
            .Where(a => a.Type is NetworkInterfaceType.Tunnel
                        || IsBenchmarkRange(ParseOrNone(a.Address))
                        || (a.Name + " " + a.Description).ToLowerInvariant() is var h
                           && VirtualMarkers.Any(m => h.Contains(m, StringComparison.Ordinal)))
            .Select(a => $"{a.Name}（{a.Description}，{a.Address}）")
            .Distinct()
            .ToList();

        details = string.Join("、", tunnels);
        return tunnels.Count > 0;
    }

    private static IPAddress ParseOrNone(string text)
        => IPAddress.TryParse(text, out IPAddress? ip) ? ip : IPAddress.None;
}
