using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace LanDisk.Core.Networking.Dns;

/// <summary>
/// Everything LanDisk is willing to say about itself on the local link.
///
/// <para>Note what is <i>not</i> here: no token, no device identifier, no share name, no Windows user
/// name. mDNS is unauthenticated multicast, so anything placed in a TXT record is effectively public
/// to the whole link.</para>
/// </summary>
public sealed record MdnsAdvertisement(
    string HostName,
    int HttpsPort,
    string Version,
    IReadOnlyList<string> Addresses)
{
    public const string HttpsService = "_https._tcp.local";
    public const string ProductService = "_landisk._tcp.local";
    public const uint DefaultTtl = 120;
    public const uint GoodbyeTtl = 0;

    public string HostFqdn => HostName.EndsWith('.') ? HostName : HostName + ".";

    public IReadOnlyList<string> Ipv4 => Addresses.Where(a => IPAddress.TryParse(a, out IPAddress? ip)
        && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).ToList();
}

/// <summary>
/// Builds the DNS records behind the advertisement. Kept as pure functions so the wire content can be
/// asserted in tests without opening a socket.
/// </summary>
public static class MdnsRecords
{
    public static byte[] ARecord(IPAddress address) => address.GetAddressBytes();

    public static byte[] PtrRecord(string target)
    {
        using var stream = new MemoryStream();
        DnsMessage.WriteName(stream, target);
        return stream.ToArray();
    }

    public static byte[] SrvRecord(ushort priority, ushort weight, ushort port, string target)
    {
        using var stream = new MemoryStream();
        Span<byte> head = stackalloc byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(head[..2], priority);
        BinaryPrimitives.WriteUInt16BigEndian(head[2..4], weight);
        BinaryPrimitives.WriteUInt16BigEndian(head[4..6], port);
        stream.Write(head);
        DnsMessage.WriteName(stream, target);
        return stream.ToArray();
    }

    /// <summary>TXT strings, each at most 255 bytes, in the &lt;length, bytes&gt; encoding.</summary>
    public static byte[] TxtRecord(IEnumerable<string> values)
    {
        using var stream = new MemoryStream();
        foreach (string value in values)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            if (bytes.Length > 255) bytes = bytes[..255];
            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes);
        }
        if (stream.Length == 0) stream.WriteByte(0);
        return stream.ToArray();
    }

    /// <summary>Non-sensitive service metadata. Reviewed by test to contain no secret-shaped values.</summary>
    public static IReadOnlyList<string> TxtValues(MdnsAdvertisement advertisement) =>
    [
        "product=LanDisk",
        "version=" + advertisement.Version,
        "port=" + advertisement.HttpsPort,
        "scheme=https"
    ];

    /// <summary>Every record LanDisk is authoritative for, in the order it announces them.</summary>
    public static IReadOnlyList<DnsResource> All(MdnsAdvertisement ad, uint ttl = MdnsAdvertisement.DefaultTtl)
    {
        var records = new List<DnsResource>();

        foreach (string address in ad.Addresses)
        {
            if (!IPAddress.TryParse(address, out IPAddress? ip)) continue;
            records.Add(new DnsResource
            {
                Name = ad.HostName,
                Type = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                    ? DnsRecordType.A
                    : DnsRecordType.AAAA,
                Ttl = ttl,
                RData = ARecord(ip)
            });
        }

        records.Add(new DnsResource
        {
            Name = MdnsAdvertisement.HttpsService,
            Type = DnsRecordType.PTR,
            Ttl = ttl,
            RData = PtrRecord(ad.HostFqdn),
            Target = ad.HostFqdn
        });

        records.Add(new DnsResource
        {
            Name = MdnsAdvertisement.ProductService,
            Type = DnsRecordType.PTR,
            Ttl = ttl,
            RData = PtrRecord(ad.HostFqdn),
            Target = ad.HostFqdn
        });

        records.Add(new DnsResource
        {
            Name = ad.HostFqdn,
            Type = DnsRecordType.SRV,
            Ttl = ttl,
            RData = SrvRecord(0, 0, (ushort)ad.HttpsPort, ad.HostFqdn),
            Target = ad.HostFqdn
        });

        records.Add(new DnsResource
        {
            Name = ad.HostFqdn,
            Type = DnsRecordType.TXT,
            Ttl = ttl,
            RData = TxtRecord(TxtValues(ad))
        });

        return records;
    }

    /// <summary>
    /// The records that answer one question. ANY and the two service types expand to the whole
    /// service set, because a browsing client needs the SRV/TXT pair to be useful.
    /// </summary>
    public static IReadOnlyList<DnsResource> ForQuestion(
        MdnsAdvertisement ad,
        DnsQuestion question,
        uint ttl = MdnsAdvertisement.DefaultTtl)
    {
        string asked = question.Name.TrimEnd('.').ToLowerInvariant();
        string host = ad.HostName.TrimEnd('.').ToLowerInvariant();

        var all = All(ad, ttl);
        var matches = new List<DnsResource>();

        bool any = question.Type == DnsRecordType.ANY;

        if (any || asked == host)
        {
            matches.AddRange(all.Where(r =>
                r.Name.TrimEnd('.').Equals(host, StringComparison.OrdinalIgnoreCase)
                && (any || r.Type == question.Type)));
        }

        if (any || asked == MdnsAdvertisement.HttpsService || asked == MdnsAdvertisement.ProductService)
        {
            matches.AddRange(all.Where(r =>
                r.Name.TrimEnd('.').Equals(asked, StringComparison.OrdinalIgnoreCase)
                && r.Type == DnsRecordType.PTR));
        }

        // A PTR for the service implies the SRV/TXT pair; include them as additionals-into-answers so
        // a single response is enough for a browser to show a usable entry.
        if (matches.Any(m => m.Type == DnsRecordType.PTR))
        {
            matches.AddRange(all.Where(r =>
                r.Name.TrimEnd('.').Equals(host, StringComparison.OrdinalIgnoreCase)
                && r.Type is DnsRecordType.SRV or DnsRecordType.TXT));
        }

        return matches.Distinct().ToList();
    }

    /// <summary>Goodbye records: the same names with a zero TTL.</summary>
    public static IReadOnlyList<DnsResource> Goodbye(MdnsAdvertisement ad)
        => All(ad, MdnsAdvertisement.GoodbyeTtl);
}
