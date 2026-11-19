using System.Net;
using System.Net.Sockets;
using LanDisk.Core.Networking;
using LanDisk.Core.Networking.Dns;
using LanDisk.Tests.Infrastructure;

namespace LanDisk.Tests;

/// <summary>
/// Task 09, fix 04: a unicast mDNS reply leaves through the interface the question arrived on.
///
/// <para><b>The defect.</b> The reply socket was chosen by comparing the <i>sender's</i> address with each
/// socket's own bound address (<c>s.LocalEndPoint.Address == remote.Address</c>). A phone's address is never
/// one of this machine's addresses, so the comparison could not match and every reply fell back to
/// <c>sockets[0]</c> — on a machine with two physical adapters the answer left through the wrong one
/// whenever the question had not arrived on the first. The receiving socket is already known, so it is now
/// used directly and no interface is guessed.</para>
///
/// <para>The two "interfaces" here are real UDP sockets (bound to two loopback addresses so their own
/// addresses are meaningful), and a recording transport replaces the send, so the assertion is about which
/// socket was chosen rather than about packets on a wire. That is the decision the bug was in.</para>
/// </summary>
public sealed class MdnsInterfaceSelectionTests
{
    /// <summary>Records which socket each datagram was handed to.</summary>
    private sealed class RecordingTransport : IMdnsTransport
    {
        public List<(Socket Socket, byte[] Payload, IPEndPoint Destination)> Sent { get; } = [];

        public void Send(Socket socket, byte[] payload, IPEndPoint destination)
            => Sent.Add((socket, payload, destination));
    }

    private static MdnsAdvertisement Advertisement(params string[] addresses) => new(
        "landisk-a1b2c3d4.local", 8892, "1.6.0", addresses.Length == 0 ? ["192.168.2.10"] : addresses);

    private static Socket Bind(string loopbackAddress, out bool bound)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.Bind(new IPEndPoint(IPAddress.Parse(loopbackAddress), 0));
            bound = true;
        }
        catch (SocketException)
        {
            // A locked-down host may refuse 127.0.0.0/8 aliases; the socket still works for the identity
            // comparison the responder performs, so the test continues without a local endpoint.
            bound = false;
        }
        return socket;
    }

    /// <summary>A question with the unicast-response bit set, which is the case that takes the reply path.</summary>
    private static byte[] UnicastQuestion()
    {
        var message = new DnsMessage { Id = 0x1234, IsResponse = false };
        message.Questions.Add(new DnsQuestion("landisk-a1b2c3d4.local", DnsRecordType.A, true));
        return message.ToBytes();
    }

    [Fact]
    public void A_unicast_question_is_answered_from_the_interface_that_received_it()
    {
        var transport = new RecordingTransport();
        using var first = Bind("127.0.0.1", out _);
        using var second = Bind("127.0.0.2", out _);
        var responder = new MdnsResponder(Advertisement("127.0.0.1", "127.0.0.2"), transport: transport);
        responder.UseSockets([first, second]);

        byte[] question = UnicastQuestion();
        responder.HandleDatagram(question, question.Length, new IPEndPoint(IPAddress.Parse("192.168.2.55"), 5353), first);

        Assert.Single(transport.Sent);
        Assert.Same(first, transport.Sent[0].Socket);
        Assert.Equal("192.168.2.55", transport.Sent[0].Destination.Address.ToString());
        responder.Dispose();

        TestBase.Record("FIX-09-04", "NIC-A 收到单播请求时，回复只从 A 的 socket 发出（同一台机器的另一个 socket 未被使用）");
    }

    [Fact]
    public void A_question_arriving_on_the_second_interface_is_answered_from_that_interface_not_the_first()
    {
        var transport = new RecordingTransport();
        using var first = Bind("127.0.0.1", out _);
        using var second = Bind("127.0.0.2", out _);
        var responder = new MdnsResponder(Advertisement("127.0.0.1", "127.0.0.2"), transport: transport);
        responder.UseSockets([first, second]);

        byte[] question = UnicastQuestion();
        responder.HandleDatagram(question, question.Length, new IPEndPoint(IPAddress.Parse("192.168.2.55"), 5353), second);

        // This is the regression: sockets[0] is `first`, and the old code would have used it.
        Assert.Single(transport.Sent);
        Assert.Same(second, transport.Sent[0].Socket);
        Assert.NotSame(first, transport.Sent[0].Socket);
        responder.Dispose();

        TestBase.Record("FIX-09-04b", "NIC-B 收到单播请求时回复只从 B 发出，不再总是走 sockets[0]");
    }

    [Fact]
    public void The_reply_does_not_depend_on_the_senders_address_matching_an_interface()
    {
        var transport = new RecordingTransport();
        using var first = Bind("127.0.0.1", out bool firstBound);
        using var second = Bind("127.0.0.2", out _);
        var responder = new MdnsResponder(Advertisement("127.0.0.1", "127.0.0.2"), transport: transport);
        responder.UseSockets([first, second]);

        // The exact shape the old code got wrong: the question's sender address happens to equal the
        // *first* interface's address while the question arrived on the second. Matching by address would
        // pick `first`; using the receiving socket picks `second`.
        var remote = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 5353);
        byte[] question = UnicastQuestion();
        responder.HandleDatagram(question, question.Length, remote, second);

        Assert.Single(transport.Sent);
        Assert.Same(second, transport.Sent[0].Socket);
        responder.Dispose();

        TestBase.Record("FIX-09-04c",
            $"回复不再靠“发送方地址 == 本机网卡地址”猜接口：发送方地址与第一块网卡相同（bind 成功={firstBound}），问题到达第二块，回复仍从第二块发出");
    }

    [Fact]
    public void With_several_interfaces_and_no_receiving_socket_nothing_is_sent()
    {
        var transport = new RecordingTransport();
        var log = new List<string>();
        using var first = Bind("127.0.0.1", out _);
        using var second = Bind("127.0.0.2", out _);
        var responder = new MdnsResponder(Advertisement("127.0.0.1", "127.0.0.2"), log.Add, transport);
        responder.UseSockets([first, second]);

        byte[] question = UnicastQuestion();
        responder.HandleDatagram(question, question.Length, new IPEndPoint(IPAddress.Parse("192.168.2.55"), 5353));

        // Not "reply from somewhere": with several interfaces and no idea which one carried the question,
        // answering from sockets[0] is exactly the bug, so nothing is sent and the reason is logged.
        Assert.Empty(transport.Sent);
        Assert.Contains(log, line => line.Contains("接口信息"));
        responder.Dispose();
    }

    [Fact]
    public void With_a_single_interface_the_reply_still_goes_out()
    {
        var transport = new RecordingTransport();
        using var only = Bind("127.0.0.1", out _);
        var responder = new MdnsResponder(Advertisement("127.0.0.1"), transport: transport);
        responder.UseSockets([only]);

        byte[] question = UnicastQuestion();
        responder.HandleDatagram(question, question.Length, new IPEndPoint(IPAddress.Parse("192.168.2.55"), 5353));

        Assert.Single(transport.Sent);
        Assert.Same(only, transport.Sent[0].Socket);
        responder.Dispose();
    }

    [Fact]
    public void A_multicast_question_is_still_answered_on_every_interface()
    {
        var transport = new RecordingTransport();
        using var first = Bind("127.0.0.1", out _);
        using var second = Bind("127.0.0.2", out _);
        var responder = new MdnsResponder(Advertisement("127.0.0.1", "127.0.0.2"), transport: transport);
        responder.UseSockets([first, second]);

        // No unicast bit: the answer is cached by everyone, so it must leave through every interface.
        var message = new DnsMessage { Id = 0, IsResponse = false };
        message.Questions.Add(new DnsQuestion("landisk-a1b2c3d4.local", DnsRecordType.A, false));
        byte[] question = message.ToBytes();
        responder.HandleDatagram(question, question.Length, new IPEndPoint(IPAddress.Parse("192.168.2.55"), 5353), first);

        Assert.Equal(2, transport.Sent.Count);
        Assert.Contains(transport.Sent, s => ReferenceEquals(s.Socket, first));
        Assert.Contains(transport.Sent, s => ReferenceEquals(s.Socket, second));
        Assert.All(transport.Sent, s => Assert.Equal(MdnsResponder.GroupAddressV4, s.Destination.Address.ToString()));
        Assert.All(transport.Sent, s => Assert.Equal(MdnsResponder.MdnsPort, s.Destination.Port));
        responder.Dispose();

        TestBase.Record("FIX-09-04d", "组播回答的回归：没有单播位的问题仍然从每一个物理接口各发一份到 224.0.0.251:5353");
    }

    [Fact]
    public async Task The_interface_list_is_built_from_the_addresses_the_classifier_accepted()
    {
        // The responder creates one socket per advertised address, so "no socket for the VPN" is the same
        // statement as "the VPN address is not in the advertisement". This is the wiring: the addresses
        // come from the physical-LAN classifier, not from "every interface Windows reports".
        IReadOnlyList<AdapterVerdict> accepted = PhysicalLanSelector.PhysicalAddresses();
        IReadOnlyList<AdapterVerdict> rejected = PhysicalLanSelector.Rejected(PhysicalLanSelector.Snapshot());

        await using TestHost host = await TestHost.CreateAsync(nameof(The_interface_list_is_built_from_the_addresses_the_classifier_accepted));
        IReadOnlyList<string> advertised = host.Runtime.LanAddresses();

        Assert.Equal(accepted.Select(a => a.Address).OrderBy(a => a, StringComparer.Ordinal),
                     advertised.OrderBy(a => a, StringComparer.Ordinal));

        foreach (AdapterVerdict bad in rejected)
            Assert.DoesNotContain(bad.Address, advertised);

        // Tunnels and the Hyper-V switch are the ones that used to end up in the certificate SAN and in
        // the mDNS answer; the classifier must have rejected at least one of them on this machine for the
        // claim to mean anything, and that is recorded rather than assumed.
        int virtualRejected = rejected.Count(r => r.Reason.Contains("虚拟", StringComparison.Ordinal)
                                                 || r.Reason.Contains("隧道", StringComparison.Ordinal)
                                                 || r.Reason.Contains("198.18", StringComparison.Ordinal));
        TestBase.Record("FIX-09-04e",
            $"mDNS 的接口列表来自物理网卡分类器：接受 {accepted.Count} 条、排除 {rejected.Count} 条（其中虚拟/隧道 {virtualRejected} 条），排除项一个都不在发布列表中");
    }
}
