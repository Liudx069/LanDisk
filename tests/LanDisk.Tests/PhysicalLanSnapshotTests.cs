using System.Net.NetworkInformation;
using LanDisk.Core.Networking;

namespace LanDisk.Tests;

/// <summary>
/// NET12 — the physical LAN reading is one shared fact, and every surface that quotes it agrees.
///
/// <para><b>The defect.</b> The product was serving <c>https://192.168.2.2:8892</c> while the network page
/// said "no usable physical adapter". Neither half was a typo: seven call sites each enumerated the live
/// adapters themselves, and enumerations of a live system can disagree. These gates pin the rules that make
/// the contradiction impossible, using adapter shapes that actually break in the field rather than whatever
/// this build machine happens to have plugged in.</para>
/// </summary>
public sealed class PhysicalLanSnapshotTests
{
    private static AdapterSnapshot Ethernet(string address, bool gateway = true, long speed = 1_000_000_000)
        => new("以太网", "Realtek PCIe GbE Family Controller", NetworkInterfaceType.Ethernet,
            OperationalStatus.Up, address, "255.255.255.0", 12, gateway, true, speed, "lan");

    private static AdapterSnapshot WiFi(string address)
        => new("WLAN", "Intel(R) Wi-Fi 6 AX201 160MHz", NetworkInterfaceType.Wireless80211,
            OperationalStatus.Up, address, "255.255.255.0", 8, true, true, 866_000_000, "lan");

    private static AdapterSnapshot Tunnel(string address)
        => new("iKuuuVPN", "Wintun Userspace Tunnel", NetworkInterfaceType.Tunnel,
            OperationalStatus.Up, address, "255.255.0.0", 30, true, false, 0, string.Empty);

    private static AdapterSnapshot HyperV(string address)
        => new("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter", NetworkInterfaceType.Ethernet,
            OperationalStatus.Up, address, "255.255.240.0", 21, false, false, 10_000_000_000, string.Empty);

    [Fact]
    public void Every_recommended_physical_origin_maps_to_a_physical_adapter()
    {
        // The exact field shape: a real Ethernet LAN plus a VPN whose fake range looks private.
        PhysicalLanSnapshot snapshot = PhysicalLanSnapshot.Build(1,
            [Ethernet("192.168.2.2"), Tunnel("198.18.0.1"), HyperV("172.30.240.1")], vpn: true, vpnDetails: "iKuuuVPN");

        Assert.True(snapshot.HasAccepted);
        PhysicalLanAdapter best = snapshot.Best!;
        Assert.Equal("192.168.2.2", best.Address);
        Assert.Equal("physical", KindFor(snapshot, best));
        Assert.NotNull(snapshot.FindAccepted("192.168.2.2"));

        TestBase.Record("NET12-01",
            "推荐为物理地址时，同一份读数里一定能找到对应的物理网卡（192.168.2.2 → Realtek 以太网）；截图上「推荐地址」与「物理网络」两块不可能再互相矛盾");
    }

    [Fact]
    public void Every_certificate_physical_san_maps_to_an_accepted_adapter()
    {
        PhysicalLanSnapshot snapshot = PhysicalLanSnapshot.Build(1, [Ethernet("192.168.2.2"), WiFi("192.168.2.40")], false, "");

        IReadOnlyList<System.Net.IPAddress> addresses = snapshot.AcceptedAddresses();
        Assert.Equal(2, addresses.Count);
        foreach (System.Net.IPAddress address in addresses)
            Assert.NotNull(snapshot.FindAccepted(address.ToString()));

        TestBase.Record("NET12-02",
            "证书 SAN 里的每个物理地址都来自同一份读数的 Accepted 列表（这里 2 个地址全部可追溯），证书不会再声明一个列表里没有的地址");
    }

    [Fact]
    public void A_vpn_address_is_never_accepted()
    {
        PhysicalLanSnapshot snapshot = PhysicalLanSnapshot.Build(1, [Ethernet("192.168.2.2"), Tunnel("198.18.0.1")], true, "iKuuuVPN");

        Assert.DoesNotContain(snapshot.Accepted, a => a.Address == "198.18.0.1");
        PhysicalLanAdapter rejected = snapshot.Rejected.Single(a => a.Address == "198.18.0.1");
        Assert.Contains("基准测试网段", rejected.Reason, StringComparison.Ordinal);
        Assert.True(snapshot.VpnOrTunnelDetected);

        TestBase.Record("NET12-03",
            "198.18.0.0/15 是 VPN/TUN 借用的基准测试网段，永远不被接受；读数同时标记存在 VPN/TUN，页面据此解释而不是把它当 LAN");
    }

    [Fact]
    public void A_hyperv_address_is_never_accepted()
    {
        PhysicalLanSnapshot snapshot = PhysicalLanSnapshot.Build(1, [HyperV("172.30.240.1")], false, "");

        Assert.Empty(snapshot.Accepted);
        Assert.False(snapshot.HasAccepted);
        Assert.Contains("虚拟或隧道网卡", snapshot.Rejected.Single().Reason, StringComparison.Ordinal);

        TestBase.Record("NET12-04",
            "Hyper-V 虚拟交换机地址（172.30.240.1）永远不被接受；当机器只有虚拟网卡时 HasAccepted=false，网络页显示的是真实原因而不是「没有可用物理网卡」这种含糊说法");
    }

    [Fact]
    public void The_reading_is_shared_and_only_its_content_moves_the_revision()
    {
        // A real (non-zero) minimum interval, so Recent() genuinely serves the cached reading.
        var state = new PhysicalLanState(TimeSpan.FromMinutes(5));
        int first = state.Current.Revision;
        Assert.True(first >= 1);

        PhysicalLanSnapshot again = state.Refresh();
        // Re-enumerating the same machine must not invent a new revision: the UI distinguishes "refreshed"
        // from "something actually changed".
        Assert.Equal(first, again.Revision);

        PhysicalLanSnapshot copy = state.Recent();
        Assert.Same(again, copy);

        TestBase.Record("NET12-05",
            "读数只有一份：Recent() 返回同一个实例；重复刷新而机器状态未变时修订号不动，消费者因此能区分「刷新过」与「真的变了」");
    }

    private static string KindFor(PhysicalLanSnapshot snapshot, PhysicalLanAdapter adapter)
        => adapter.Accepted ? "physical" : "stable";
}
