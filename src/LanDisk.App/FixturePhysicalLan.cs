using System.Net.NetworkInformation;
using LanDisk.Core.Networking;

namespace LanDisk.App;

/// <summary>
/// A deterministic adapter set for the UI capture, so the network page can be verified against a **known**
/// fixture instead of whatever the machine happens to have plugged in.
///
/// <para><b>Why this exists.</b> R2 asks whether the real WPF network page renders a usable physical adapter
/// (and never says "没有可用物理网卡" while the same page recommends that address). Checking that against the
/// operator's live machine would make the result depend on their cabling, and reading their real adapters is
/// not a fixture. This supplies the exact case from the field report:</para>
/// <list type="bullet">
/// <item>Ethernet 192.168.2.2/24 with gateway 192.168.2.1 — accepted;</item>

/// <item>a Hyper-V switch at 172.24.0.1 — present but rejected.</item>
/// </list>
///
/// <para>Capture mode only: <see cref="ServerController"/> refuses it for a normal launch, so a real run can
/// never be poisoned by a fixture.</para>
/// </summary>
internal static class FixturePhysicalLan
{
    public static PhysicalLanSnapshot Capture(int revision)
    {
        List<AdapterSnapshot> adapters =
        [
            new AdapterSnapshot(
                Name: "Ethernet",
                Description: "Intel(R) Ethernet Connection (1219-V) 夹具",
                Type: NetworkInterfaceType.Ethernet,
                Status: OperationalStatus.Up,
                Address: "192.168.2.2",
                SubnetMask: "255.255.255.0",
                InterfaceIndex: 12,
                HasGateway: true,
                IsDhcpAssigned: false,
                Speed: 1_000_000_000,
                DnsSuffix: string.Empty),

            // Hyper-V 交换机：存在、Up、有地址，但绝不可用于手机访问
            new AdapterSnapshot(
                Name: "vEthernet (Default Switch)",
                Description: "Hyper-V Virtual Ethernet Adapter",
                Type: NetworkInterfaceType.Ethernet,
                Status: OperationalStatus.Up,
                Address: "172.24.0.1",
                SubnetMask: "255.255.240.0",
                InterfaceIndex: 42,
                HasGateway: false,
                IsDhcpAssigned: false,
                Speed: 10_000_000_000,
                DnsSuffix: "mshome.net"),
        ];

        return PhysicalLanSnapshot.Build(revision, adapters, vpn: false, vpnDetails: string.Empty);
    }
}
