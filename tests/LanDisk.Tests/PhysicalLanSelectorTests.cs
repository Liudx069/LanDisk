using System.Net.NetworkInformation;
using LanDisk.Core.Networking;
using LanDisk.Tests.Infrastructure;

namespace LanDisk.Tests;

/// <summary>
/// Task 08: which interface a phone is allowed to be told about.
///
/// The shapes below are not invented. They are the four adapters that were actually present on the
/// machine this was written on, plus the two that the user's bug report implies: a global TUN holding
/// the default route and answering on 198.18.0.1, and a Hyper-V switch on 172.16/12 — both of which
/// the previous "any up interface with an address" rule happily advertised.
/// </summary>
public sealed class PhysicalLanSelectorTests
{
    private static AdapterSnapshot Ethernet(string address = "192.168.2.2", bool gateway = true, bool dhcp = true)
        => new("以太网", "Realtek PCIe GBE Family Controller", NetworkInterfaceType.Ethernet,
            OperationalStatus.Up, address, "255.255.255.0", 12, gateway, dhcp, 1_000_000_000, "lan");

    private static AdapterSnapshot Wifi(string address = "192.168.1.50")
        => new("WLAN", "Realtek RTL8192EE Wireless LAN 802.11n PCI-E NIC", NetworkInterfaceType.Wireless80211,
            OperationalStatus.Up, address, "255.255.255.0", 15, true, true, 300_000_000, "lan");

    private static AdapterSnapshot IKuuuVpn()
        => new("iKuuuVPN", "iKuuuVPN Tunnel", NetworkInterfaceType.Ethernet,
            OperationalStatus.Up, "198.18.0.1", "255.255.0.0", 6, true, false, 100_000_000, "");

    private static AdapterSnapshot HyperV()
        => new("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter", NetworkInterfaceType.Ethernet,
            OperationalStatus.Up, "172.26.224.1", "255.255.240.0", 22, false, false, 10_000_000_000, "");

    private static AdapterSnapshot WireGuard()
        => new("wg0", "WireGuard Tunnel", NetworkInterfaceType.Tunnel,
            OperationalStatus.Up, "10.7.0.2", "255.255.255.0", 40, true, false, 0, "");

    private static AdapterSnapshot Apipa()
        => new("本地连接* 2", "Microsoft Wi-Fi Direct Virtual Adapter", NetworkInterfaceType.Ethernet,
            OperationalStatus.Up, "169.254.0.107", "255.255.0.0", 17, false, false, 0, "");

    // ------------------------------------------------------------------ exclusions

    [Fact]
    public void A_tunnel_on_the_benchmark_range_is_rejected_even_when_it_claims_to_be_ethernet()
    {
        AdapterVerdict verdict = PhysicalLanSelector.Classify(IKuuuVpn());

        Assert.False(verdict.IsPhysical);
        Assert.Contains("198.18.0.0/15", verdict.Reason, StringComparison.Ordinal);
        Assert.True(PhysicalLanSelector.IsBenchmarkRange(System.Net.IPAddress.Parse("198.18.0.1")));
        Assert.True(PhysicalLanSelector.IsBenchmarkRange(System.Net.IPAddress.Parse("198.19.255.254")));
        Assert.False(PhysicalLanSelector.IsBenchmarkRange(System.Net.IPAddress.Parse("198.20.0.1")));
    }

    [Fact]
    public void Virtual_switches_and_tunnel_drivers_are_rejected()
    {
        Assert.False(PhysicalLanSelector.Classify(HyperV()).IsPhysical);
        Assert.False(PhysicalLanSelector.Classify(WireGuard()).IsPhysical);
        Assert.False(PhysicalLanSelector.Classify(Apipa()).IsPhysical);

        Assert.Contains("虚拟或隧道", PhysicalLanSelector.Classify(HyperV()).Reason, StringComparison.Ordinal);
        Assert.Contains("169.254", PhysicalLanSelector.Classify(Apipa()).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_down_adapter_and_a_public_address_are_rejected()
    {
        var down = Ethernet() with { Status = OperationalStatus.Down };
        Assert.False(PhysicalLanSelector.Classify(down).IsPhysical);
        Assert.Contains("网卡未连接", PhysicalLanSelector.Classify(down).Reason, StringComparison.Ordinal);

        var publicAddress = Ethernet(address: "203.0.113.5");
        Assert.False(PhysicalLanSelector.Classify(publicAddress).IsPhysical);
        Assert.Contains("不是私有网段", PhysicalLanSelector.Classify(publicAddress).Reason, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ selection

    [Fact]
    public void Wired_ethernet_wins_over_wifi_and_the_virtual_adapters_are_never_chosen()
    {
        IReadOnlyList<AdapterVerdict> ranked = PhysicalLanSelector.Rank(
            [IKuuuVpn(), HyperV(), Apipa(), Wifi(), Ethernet(), WireGuard()]);

        AdapterVerdict best = ranked[0];

        Assert.True(best.IsPhysical);
        Assert.Equal("192.168.2.2", best.Address);
        Assert.Equal("以太网", best.Name);

        IReadOnlyList<AdapterVerdict> physical = PhysicalLanSelector.PhysicalCandidates(
            [IKuuuVpn(), HyperV(), Apipa(), Wifi(), Ethernet(), WireGuard()]);

        Assert.Equal(2, physical.Count);
        Assert.Equal(["192.168.2.2", "192.168.1.50"], physical.Select(v => v.Address).ToArray());

        // The rejected ones keep their explanation, because the network page shows it.
        IReadOnlyList<AdapterVerdict> rejected = PhysicalLanSelector.Rejected(
            [IKuuuVpn(), HyperV(), Apipa(), Wifi(), Ethernet(), WireGuard()]);
        Assert.Equal(4, rejected.Count);
        Assert.All(rejected, v => Assert.False(string.IsNullOrWhiteSpace(v.Reason)));
    }

    [Fact]
    public void A_wifi_only_machine_still_gets_a_usable_answer()
    {
        IReadOnlyList<AdapterVerdict> candidates = PhysicalLanSelector.PhysicalCandidates([HyperV(), Apipa(), Wifi()]);

        Assert.Single(candidates);
        Assert.Equal("192.168.1.50", candidates[0].Address);
    }

    [Fact]
    public void An_adapter_without_a_gateway_scores_lower_but_is_still_usable()
    {
        AdapterVerdict withGateway = PhysicalLanSelector.Classify(Ethernet());
        AdapterVerdict withoutGateway = PhysicalLanSelector.Classify(Ethernet(address: "192.168.9.2", gateway: false));

        Assert.True(withoutGateway.IsPhysical);
        Assert.True(withGateway.Score > withoutGateway.Score);
    }

    [Fact]
    public void Addresses_are_not_ordered_by_accident()
    {
        // Two identical wired adapters: the ordering has to be deterministic, not enumeration order.
        var first = Ethernet(address: "192.168.5.10") with { Name = "以太网 2" };
        var second = Ethernet(address: "192.168.5.11") with { Name = "以太网 3" };

        Assert.Equal(["192.168.5.10", "192.168.5.11"],
            PhysicalLanSelector.PhysicalCandidates([second, first]).Select(v => v.Address).ToArray());
        Assert.Equal(["192.168.5.10", "192.168.5.11"],
            PhysicalLanSelector.PhysicalCandidates([first, second]).Select(v => v.Address).ToArray());
    }

    // ------------------------------------------------------------------ against this machine

    [Fact]
    public void The_live_machine_never_offers_a_virtual_or_benchmark_address()
    {
        IReadOnlyList<AdapterVerdict> physical = PhysicalLanSelector.PhysicalAddresses();

        foreach (AdapterVerdict candidate in physical)
        {
            Assert.True(candidate.IsPhysical);
            Assert.False(PhysicalLanSelector.IsBenchmarkRange(System.Net.IPAddress.Parse(candidate.Address)),
                "不得把 198.18/15 的 VPN 地址当作局域网地址：" + candidate.Address);
            Assert.False(PhysicalLanSelector.IsApipa(System.Net.IPAddress.Parse(candidate.Address)),
                "不得把 APIPA 地址当作局域网地址：" + candidate.Address);
            Assert.True(LanInfo.IsPrivate(System.Net.IPAddress.Parse(candidate.Address)));
        }

        // Whatever the machine looks like, the answer must be stable and explainable.
        AdapterVerdict? best = PhysicalLanSelector.Best();
        if (best is not null) Assert.False(string.IsNullOrWhiteSpace(best.Reason));
    }

    [Fact]
    public void A_present_vpn_is_reported_but_never_modified()
    {
        bool detected = PhysicalLanSelector.VpnOrTunnelDetected(out string details);

        // The point of this test is not the boolean — it depends on the machine — but that the
        // detector answers with an explanation instead of throwing, and that nothing here touches the
        // adapter. LanDisk only ever reports; routing and VPN configuration are the user's.
        Assert.NotNull(details);
        if (detected) Assert.False(string.IsNullOrWhiteSpace(details), "检测到 VPN/TUN 时必须能说明是哪一个");

        TestBase.Record("UX08-01", detected
            ? "检测到 VPN/TUN：" + details
            : "本机当前没有处于连接状态的 VPN/TUN 网卡");
    }
}
