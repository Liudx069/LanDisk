using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LanDisk.Core.Networking;

public sealed record NetworkInterfaceInfo(
    string Name,
    string Description,
    string Address,
    string SubnetMask,
    bool IsPrivate,
    string Status,
    string InterfaceType);

/// <summary>
/// One address a phone on the same LAN could reach this machine at, with the reasoning behind its
/// position in the list made explicit rather than implied by enumeration order.
/// </summary>
public sealed record LanAddressCandidate(
    string Address,
    string InterfaceName,
    string Description,
    bool IsPrivate,
    bool IsVirtual,
    int Rank,
    string Reason)
{
    public string Url(int port) => $"http://{Address}:{port}/";

    public string HttpsUrl(int port) => $"https://{Address}:{port}/";
}

public static class LanInfo
{
    /// <summary>
    /// Substrings that identify adapters which exist but cannot carry phone traffic: WSL, Hyper-V,
    /// Docker, VPN tunnels, Bluetooth PAN and the like. They are not excluded from the list — a user
    /// may legitimately want to see them — but they must never be presented first, because a phone
    /// cannot reach a vEthernet address on the host.
    /// </summary>
    private static readonly string[] VirtualMarkers =
    [
        "vethernet", "hyper-v", "wsl", "docker", "virtualbox", "vmware", "loopback",
        "tap-", "tun", "wireguard", "openvpn", "zerotier", "tailscale", "hamachi",
        "bluetooth", "npcap", "cisco anyconnect", "forticlient", "proton", "radmin",
        "default switch", "nat network", "host-only"
    ];

    public static IReadOnlyList<NetworkInterfaceInfo> ListIPv4()
    {
        var result = new List<NetworkInterfaceInfo>();

        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            IPInterfaceProperties props;
            try { props = nic.GetIPProperties(); }
            catch { continue; }

            foreach (UnicastIPAddressInformation addr in props.UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(addr.Address)) continue;

                string mask = addr.IPv4Mask?.ToString() ?? "255.255.255.0";
                result.Add(new NetworkInterfaceInfo(
                    nic.Name,
                    nic.Description,
                    addr.Address.ToString(),
                    mask,
                    IsPrivate(addr.Address),
                    nic.OperationalStatus.ToString(),
                    nic.NetworkInterfaceType.ToString()));
            }
        }

        return result.OrderBy(a => a.Address, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Addresses a phone could actually use, best first.
    ///
    /// The ordering rule is deliberately conservative: loopback and unspecified addresses are removed
    /// entirely (they are unreachable from the phone and would only confuse), private addresses on
    /// physical adapters come first, and known virtual adapters are pushed to the end with a stated
    /// reason. Nothing here changes the server's bind configuration — it only decides what to show.
    /// </summary>
    public static IReadOnlyList<LanAddressCandidate> RankForDevices(
        IReadOnlyCollection<string>? onlyAddresses = null)
    {
        var candidates = new List<LanAddressCandidate>();

        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            IPInterfaceProperties props;
            try { props = nic.GetIPProperties(); }
            catch { continue; }

            bool isVirtual = IsVirtualAdapter(nic.Name, nic.Description);

            foreach (UnicastIPAddressInformation addr in props.UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(addr.Address)) continue;
                if (addr.Address.Equals(IPAddress.Any) || addr.Address.Equals(IPAddress.None)) continue;

                string text = addr.Address.ToString();
                if (onlyAddresses is not null && !onlyAddresses.Contains(text)) continue;

                bool isPrivate = IsPrivate(addr.Address);

                // Rank is a small integer so the intent is readable in the UI: 0 is best.
                int rank = 0;
                string reason;

                if (isVirtual)
                {
                    rank += 100;
                    reason = "虚拟或隧道网卡，手机通常无法访问";
                }
                else
                {
                    reason = "物理网卡";
                }

                if (!isPrivate)
                {
                    rank += 10;
                    reason += "；不是私有网段";
                }
                else
                {
                    reason += "；私有网段";
                }

                if (text.StartsWith("169.254.", StringComparison.Ordinal))
                {
                    rank += 50;
                    reason += "；APIPA 自动地址（通常表示没有拿到 DHCP 地址）";
                }

                candidates.Add(new LanAddressCandidate(
                    text,
                    nic.Name,
                    nic.Description,
                    isPrivate,
                    isVirtual,
                    rank,
                    reason));
            }
        }

        return candidates
            .OrderBy(c => c.Rank)
            .ThenBy(c => c.IsVirtual)
            .ThenBy(c => c.Address, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The single best address to show a user, or null when there is no usable candidate.</summary>
    public static LanAddressCandidate? BestForDevices(IReadOnlyCollection<string>? onlyAddresses = null)
        => RankForDevices(onlyAddresses).FirstOrDefault(c => !c.IsVirtual);

    public static bool IsVirtualAdapter(string name, string description)
    {
        string haystack = (name + " " + description).ToLowerInvariant();
        return VirtualMarkers.Any(marker => haystack.Contains(marker, StringComparison.Ordinal));
    }

    /// <summary>RFC1918 plus link-local: the only ranges LanDisk will serve on.</summary>
    public static bool IsPrivate(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        byte[] b = address.GetAddressBytes();

        if (b[0] == 10) return true;
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
        if (b[0] == 192 && b[1] == 168) return true;
        if (b[0] == 169 && b[1] == 254) return true;
        if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true; // CGNAT
        return false;
    }

    public static bool IsLoopback(IPAddress? address)
    {
        if (address is null) return false;
        if (IPAddress.IsLoopback(address)) return true;
        if (address.IsIPv4MappedToIPv6) return IPAddress.IsLoopback(address.MapToIPv4());
        return false;
    }

    /// <summary>Parses "192.168.1.0/24" into network + prefix length.</summary>
    public static bool TryParseCidr(string cidr, out IPAddress network, out int prefixLength)
    {
        network = IPAddress.None;
        prefixLength = 0;
        if (string.IsNullOrWhiteSpace(cidr)) return false;

        string[] parts = cidr.Split('/');
        if (parts.Length != 2) return false;
        if (!IPAddress.TryParse(parts[0], out IPAddress? parsed) || parsed.AddressFamily != AddressFamily.InterNetwork) return false;
        if (!int.TryParse(parts[1], out prefixLength) || prefixLength < 0 || prefixLength > 32) return false;

        network = parsed;
        return true;
    }

    public static bool IsInCidr(IPAddress address, IPAddress network, int prefixLength)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        byte[] a = address.GetAddressBytes();
        byte[] n = network.GetAddressBytes();

        int fullBytes = prefixLength / 8;
        int remainder = prefixLength % 8;

        for (int i = 0; i < fullBytes; i++)
        {
            if (a[i] != n[i]) return false;
        }

        if (remainder > 0)
        {
            int mask = 0xFF << (8 - remainder) & 0xFF;
            if ((a[fullBytes] & mask) != (n[fullBytes] & mask)) return false;
        }

        return true;
    }
}
