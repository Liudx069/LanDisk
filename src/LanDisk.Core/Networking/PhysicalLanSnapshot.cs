using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LanDisk.Core.Networking;

/// <summary>One adapter as it appears in a <see cref="PhysicalLanSnapshot"/>.</summary>
public sealed record PhysicalLanAdapter(
    string Id,
    string Name,
    string Description,
    NetworkInterfaceType Type,
    OperationalStatus Status,
    string Address,
    string SubnetMask,
    string? Gateway,
    long Speed,
    bool Accepted,
    string Reason,
    int Score,
    int InterfaceIndex)
{
    /// <summary>True when this adapter is carrying the address the product is actually serving on.</summary>
    public bool IsPhysical => Accepted;
}

/// <summary>
/// One immutable reading of the machine's physical network, shared by everything that needs it.
///
/// <para><b>The bug this exists to kill.</b> Binding, the certificate SAN, the overview card, the network
/// page, self-check, the operations dashboard and the pairing origin each called
/// <c>PhysicalLanSelector.Snapshot()</c> themselves. Seven enumerations of a live system can disagree — the
/// Owner saw exactly that: the product serving <c>https://192.168.2.2:8892</c> while the network page said
/// there was no usable physical adapter. Each call was individually reasonable and the page as a whole was
/// lying. There is now one reading, with a revision, and every consumer reads that.</para>
///
/// <para><b>Why a revision.</b> A cached reading that never updates is the opposite failure — the user plugs
/// a cable in and the page keeps saying the adapter is down. The revision lets the UI say "this reading is
/// from N seconds ago" and lets a consumer decide whether to refresh, instead of silently mixing ages.</para>
/// </summary>
public sealed record PhysicalLanSnapshot(
    int Revision,
    DateTimeOffset CapturedAt,
    IReadOnlyList<PhysicalLanAdapter> Adapters,
    bool VpnOrTunnelDetected,
    string VpnDetails)
{
    public IReadOnlyList<PhysicalLanAdapter> Accepted { get; } = [.. Adapters.Where(a => a.Accepted)];
    public IReadOnlyList<PhysicalLanAdapter> Rejected { get; } = [.. Adapters.Where(a => !a.Accepted)];

    /// <summary>The best accepted adapter, or null when the machine has none right now.</summary>
    public PhysicalLanAdapter? Best => Accepted.FirstOrDefault();

    /// <summary>True when a phone could reach this machine over a physical LAN interface.</summary>
    public bool HasAccepted => Accepted.Count > 0;

    public static PhysicalLanSnapshot Empty { get; } = new(0, DateTimeOffset.MinValue, [], false, string.Empty);

    /// <summary>Finds the accepted adapter carrying a given address, so a recommended origin can be traced.</summary>
    public PhysicalLanAdapter? FindAccepted(string? address)
        => string.IsNullOrWhiteSpace(address) ? null : Accepted.FirstOrDefault(
            a => string.Equals(a.Address, address, StringComparison.Ordinal));

    /// <summary>
    /// Classifies raw adapter readings into a snapshot. Pure, so every rule can be pinned by a test against
    /// the shapes that actually break in the field. <paramref name="revision"/> is supplied by the provider.
    /// </summary>
    public static PhysicalLanSnapshot Build(int revision, IEnumerable<AdapterSnapshot> adapters, bool vpn, string vpnDetails)
    {
        List<AdapterSnapshot> list = [.. adapters];
        var verdicts = PhysicalLanSelector.Rank(list).ToList();

        // Gateway and speed are per adapter, not per verdict; keep the first reading for each address.
        var byAddress = list.GroupBy(a => a.Address, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        List<PhysicalLanAdapter> mapped = [];
        foreach (AdapterVerdict verdict in verdicts)
        {
            byAddress.TryGetValue(verdict.Address, out AdapterSnapshot? raw);
            mapped.Add(new PhysicalLanAdapter(
                Id: string.IsNullOrEmpty(raw?.Name) ? verdict.Address : raw.Name + "#" + verdict.InterfaceIndex,
                Name: verdict.Name,
                Description: verdict.Description,
                Type: raw?.Type ?? NetworkInterfaceType.Unknown,
                Status: raw?.Status ?? OperationalStatus.Unknown,
                Address: verdict.Address,
                SubnetMask: raw?.SubnetMask ?? string.Empty,
                Gateway: raw is { HasGateway: true } ? "有默认网关" : null,
                Speed: raw?.Speed ?? 0,
                Accepted: verdict.IsPhysical,
                Reason: verdict.Reason,
                Score: verdict.Score,
                InterfaceIndex: verdict.InterfaceIndex));
        }

        return new PhysicalLanSnapshot(revision, DateTimeOffset.Now, mapped, vpn, vpnDetails);
    }

    /// <summary>Reads the live machine once and classifies it.</summary>
    public static PhysicalLanSnapshot Capture(int revision)
    {
        bool vpn = PhysicalLanSelector.VpnOrTunnelDetected(out string details);
        IReadOnlyList<AdapterSnapshot> adapters = PhysicalLanSelector.Snapshot();
        return Build(revision, adapters, vpn, details);
    }

    /// <summary>True when two readings describe the same machine state, so the revision need not move.</summary>
    public bool SameAs(PhysicalLanSnapshot other)
    {
        if (VpnOrTunnelDetected != other.VpnOrTunnelDetected) return false;
        if (Adapters.Count != other.Adapters.Count) return false;

        for (int i = 0; i < Adapters.Count; i++)
        {
            PhysicalLanAdapter a = Adapters[i], b = other.Adapters[i];
            if (a.Address != b.Address || a.Accepted != b.Accepted || a.Status != b.Status || a.Name != b.Name) return false;
        }

        return true;
    }

    /// <summary>
    /// Every accepted adapter's address. Used by the certificate SAN and by the bind step so the certificate
    /// and the listener can only ever name addresses this same reading accepted.
    /// </summary>
    public IReadOnlyList<IPAddress> AcceptedAddresses()
    {
        List<IPAddress> result = [];
        foreach (PhysicalLanAdapter adapter in Accepted)
        {
            if (IPAddress.TryParse(adapter.Address, out IPAddress? ip) && ip.AddressFamily == AddressFamily.InterNetwork)
                result.Add(ip);
        }
        return result;
    }
}

/// <summary>
/// Holds the current <see cref="PhysicalLanSnapshot"/> and hands the same instance to every consumer.
///
/// <para>Refresh is explicit and rate limited: a page render asks for the current reading, and only a caller
/// that says "I want it fresh" pays for a re-enumeration. The revision moves only when the machine state
/// actually changed, so a UI can distinguish "nothing changed" from "I refreshed".</para>
/// </summary>
public sealed class PhysicalLanState
{
    private readonly object _gate = new();
    private readonly TimeSpan _minimumInterval;
    private readonly Func<PhysicalLanSnapshot, PhysicalLanSnapshot>? _observer;
    private readonly Func<int, PhysicalLanSnapshot> _source;
    private PhysicalLanSnapshot _current = PhysicalLanSnapshot.Empty;
    private DateTimeOffset _lastCapture = DateTimeOffset.MinValue;

    /// <param name="minimumInterval">How long a reading is reused before re-enumerating.</param>
    /// <param name="observer">Post-processing hook (used to raise conflict events on address changes).</param>
    /// <param name="source">
    /// Where a reading comes from. Defaults to enumerating the real machine. The UI capture passes a
    /// deterministic fixture so the network page can be verified against a known adapter set without
    /// reading (let alone changing) the machine's real network.
    /// </param>
    public PhysicalLanState(
        TimeSpan? minimumInterval = null,
        Func<PhysicalLanSnapshot, PhysicalLanSnapshot>? observer = null,
        Func<int, PhysicalLanSnapshot>? source = null)
    {
        _minimumInterval = minimumInterval ?? TimeSpan.FromSeconds(2);
        _observer = observer;
        _source = source ?? PhysicalLanSnapshot.Capture;
        _current = Book(_source(1));
        _lastCapture = DateTimeOffset.Now;
    }

    /// <summary>The current reading. Never null; <see cref="PhysicalLanSnapshot.HasAccepted"/> may be false.</summary>
    public PhysicalLanSnapshot Current
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>
    /// The current reading, re-enumerated when it is older than the minimum interval. Used where being a
    /// couple of seconds out of date is harmless (page render); a caller that needs a guaranteed-fresh
    /// reading calls <see cref="Refresh"/>.
    /// </summary>
    public PhysicalLanSnapshot Recent()
    {
        lock (_gate)
        {
            if (DateTimeOffset.Now - _lastCapture < _minimumInterval) return _current;
        }
        return Refresh();
    }

    /// <summary>Re-enumerates now. The revision moves only if the machine state actually changed.</summary>
    public PhysicalLanSnapshot Refresh()
    {
        PhysicalLanSnapshot fresh;
        lock (_gate)
        {
            int revision = _current.Revision;
            fresh = _source(revision + 1);
            fresh = fresh with { Revision = fresh.SameAs(_current) ? _current.Revision : revision + 1 };
            _current = Book(fresh);
            _lastCapture = DateTimeOffset.Now;
            return _current;
        }
    }

    private PhysicalLanSnapshot Book(PhysicalLanSnapshot snapshot) => _observer?.Invoke(snapshot) ?? snapshot;
}
