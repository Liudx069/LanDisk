using System.Net;
using System.Net.Sockets;
using LanDisk.Core.Networking.Dns;

namespace LanDisk.Core.Networking;

public sealed class MdnsConflictEventArgs : EventArgs
{
    public required string HostName { get; init; }
    public required string OtherAddress { get; init; }
}

/// <summary>
/// A small RFC 6762 responder that publishes LanDisk's stable name and HTTPS service on the local
/// link.
///
/// <para><b>Why hand-rolled.</b> The alternative was a third-party mDNS library or requiring Bonjour;
/// both add a moving part to a product whose whole promise is "double click one file". The surface
/// actually needed here is narrow — answer questions about our own name and service, announce on
/// start, say goodbye on stop, and notice when somebody else already owns the name — so it is
/// implemented directly and covered by tests on the message layer, which is where the bugs live.</para>
///
/// <para>Failure is not fatal: if multicast cannot be joined (a locked-down network, or another
/// responder holding the port) <see cref="Start"/> returns false with a reason, and the product falls
/// back to showing the current address and its QR code on the computer's own screen. It never
/// silently degrades to plain HTTP.</para>
/// </summary>
public sealed class MdnsResponder : IDisposable
{
    public const string GroupAddressV4 = "224.0.0.251";
    public const int MdnsPort = 5353;

    private readonly Action<string>? _log;
    private readonly IMdnsTransport _transport;
    private readonly object _gate = new();

    private readonly List<Socket> _sockets = [];
    private CancellationTokenSource? _cts;
    private readonly List<Task> _loops = [];

    public MdnsResponder(MdnsAdvertisement advertisement, Action<string>? log = null, IMdnsTransport? transport = null)
    {
        Advertisement = advertisement;
        _log = log;
        _transport = transport ?? SocketMdnsTransport.Instance;
    }

    /// <summary>
    /// Installs an already-bound socket set instead of calling <see cref="Start"/>. Exists so a test can
    /// stand two interfaces up without two physical NICs; the reply path under test is the same code.
    /// </summary>
    internal void UseSockets(IReadOnlyList<Socket> sockets)
    {
        lock (_gate)
        {
            foreach (Socket socket in _sockets)
            {
                try { socket.Dispose(); } catch { }
            }
            _sockets.Clear();
            _sockets.AddRange(sockets);
        }
        IsRunning = _sockets.Count > 0;
    }

    /// <summary>The sockets currently in use, in interface order. Read by tests.</summary>
    internal IReadOnlyList<Socket> Interfaces
    {
        get { lock (_gate) return _sockets.ToArray(); }
    }

    public MdnsAdvertisement Advertisement { get; private set; }

    public bool IsRunning { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>Raised when another device on the link claims the same host name with a different address.</summary>
    public event EventHandler<MdnsConflictEventArgs>? ConflictDetected;

    /// <summary>Number of queries answered; used by the status endpoint and by acceptance tests.</summary>
    public long AnsweredQueries { get; private set; }

    public void Update(MdnsAdvertisement advertisement)
    {
        Advertisement = advertisement;
        if (IsRunning)
        {
            // Re-announce so the new address set replaces the old one immediately.
            SendMulticast(DnsMessageWithRecords(MdnsRecords.All(advertisement)));
        }
    }

    public bool Start()
    {
        if (IsRunning) return true;

        try
        {
            // One socket per physical interface, each bound to that interface's own address and joined
            // to the group on it. Binding the wildcard and hoping the routing table cooperates is what
            // breaks on a machine with a global tunnel: the tunnel owns the default route, so replies
            // and announcements would leave through the tunnel and never reach the phone.
            var sockets = new List<Socket>();
            foreach (string address in Advertisement.Ipv4)
            {
                if (!IPAddress.TryParse(address, out IPAddress? local)) continue;

                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
                {
                    ExclusiveAddressUse = false
                };
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                socket.Bind(new IPEndPoint(local, MdnsPort));
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                    new MulticastOption(IPAddress.Parse(GroupAddressV4), local));
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
                sockets.Add(socket);
            }

            if (sockets.Count == 0)
            {
                LastError = "没有可用的物理局域网地址，无法发布稳定名称。";
                _log?.Invoke(LastError);
                return false;
            }

            var cts = new CancellationTokenSource();
            lock (_gate)
            {
                _sockets.Clear();
                _sockets.AddRange(sockets);
                _cts = cts;
                _loops.Clear();
                foreach (Socket socket in sockets)
                {
                    _loops.Add(Task.Run(() => ReceiveLoopAsync(socket, cts.Token)));
                }
            }

            IsRunning = true;
            LastError = null;

            SendMulticast(DnsMessageWithRecords(MdnsRecords.All(Advertisement)));
            _log?.Invoke($"mDNS 已在物理网卡上发布 {Advertisement.HostName}（{string.Join(", ", Advertisement.Addresses)}），端口 {Advertisement.HttpsPort}，" +
                         $"socket 绑定 {sockets.Count} 个接口");
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _log?.Invoke("mDNS 启动失败：" + ex.Message);
            CleanupSockets();
            return false;
        }
    }

    /// <summary>Sends a second announcement; called shortly after start to satisfy the "announce twice" rule.</summary>
    public void AnnounceAgain()
    {
        if (!IsRunning) return;
        SendMulticast(DnsMessageWithRecords(MdnsRecords.All(Advertisement)));
    }

    public void Stop()
    {
        if (!IsRunning) return;

        try
        {
            // Goodbye: the same records with TTL 0, so browsers drop us promptly instead of waiting
            // out the TTL after a clean shutdown.
            SendMulticast(DnsMessageWithRecords(MdnsRecords.Goodbye(Advertisement)));
        }
        catch { }

        IsRunning = false;
        try { _cts?.Cancel(); } catch { }
        CleanupSockets();
    }

    private void CleanupSockets()
    {
        lock (_gate)
        {
            foreach (Socket socket in _sockets)
            {
                try { socket.Dispose(); } catch { }
            }
            _sockets.Clear();
            _loops.Clear();
            try { _cts?.Dispose(); } catch { }
            _cts = null;
        }
    }

    private async Task ReceiveLoopAsync(Socket socket, CancellationToken token)
    {
        var buffer = new byte[4096];
        var remote = new IPEndPoint(IPAddress.Any, 0);

        while (!token.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, remote, token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException ex)
            {
                if (token.IsCancellationRequested) return;
                _log?.Invoke("mDNS 接收失败：" + ex.Message);
                await Task.Delay(500, token).ConfigureAwait(false);
                continue;
            }

            try
            {
                HandleDatagram(buffer, result.ReceivedBytes, (IPEndPoint)result.RemoteEndPoint, socket);
            }
            catch (Exception ex)
            {
                _log?.Invoke("mDNS 报文处理失败：" + ex.Message);
            }
        }
    }

    /// <summary>Exposed for tests: handles one received datagram and answers it if it is a question.</summary>
    public void HandleDatagram(byte[] buffer, int length, IPEndPoint remote, Socket? socket = null)
    {
        DnsMessage message = DnsMessage.Parse(buffer, length);

        if (message.IsResponse)
        {
            CheckForConflict(message, remote);
            return;
        }

        if (message.Questions.Count == 0) return;

        var answers = new List<DnsResource>();
        bool unicastRequested = false;

        foreach (DnsQuestion question in message.Questions)
        {
            if (question.UnicastResponse) unicastRequested = true;
            answers.AddRange(MdnsRecords.ForQuestion(Advertisement, question));
        }

        if (answers.Count == 0) return;

        AnsweredQueries++;
        var response = new DnsMessage
        {
            Id = message.Id,
            IsResponse = true,
            Authoritative = true,
            ResponseCode = 0
        };
        response.Answers.AddRange(answers.Distinct());

        byte[] payload = response.ToBytes();

        // A question with the unicast-response bit set is answered directly; everything else goes to
        // the group so every listener can cache it. The unicast reply leaves through the *receiving*
        // socket: see SendUnicast for why choosing by address cannot work.
        if (unicastRequested && message.Id != 0)
        {
            SendUnicast(socket, payload, remote);
        }
        else
        {
            SendMulticast(payload);
        }
    }

    private void CheckForConflict(DnsMessage message, IPEndPoint remote)
    {
        string host = Advertisement.HostName.TrimEnd('.');

        foreach (DnsResource record in message.Answers)
        {
            if (record.Type is not (DnsRecordType.A or DnsRecordType.AAAA)) continue;
            if (!record.Name.TrimEnd('.').Equals(host, StringComparison.OrdinalIgnoreCase)) continue;

            string? address = record.AddressText;
            if (address is null) continue;
            if (Advertisement.Addresses.Contains(address)) continue;

            _log?.Invoke($"检测到名称冲突：{Advertisement.HostName} 已被 {remote.Address} 声明为 {address}");
            ConflictDetected?.Invoke(this, new MdnsConflictEventArgs
            {
                HostName = Advertisement.HostName,
                OtherAddress = address
            });
            return;
        }
    }

    private static byte[] DnsMessageWithRecords(IReadOnlyList<DnsResource> records)
    {
        var message = new DnsMessage { IsResponse = true, Authoritative = true };
        message.Answers.AddRange(records);
        return message.ToBytes();
    }

    public void SendMulticast(byte[] payload)
    {
        Socket[] sockets;
        lock (_gate) sockets = _sockets.ToArray();
        if (sockets.Length == 0) return;

        var group = new IPEndPoint(IPAddress.Parse(GroupAddressV4), MdnsPort);
        foreach (Socket socket in sockets)
        {
            try
            {
                // Each socket is already pinned to one physical interface, so the interface option only
                // has to be reconfirmed here; there is no path by which this packet can leave through
                // the tunnel.
                _transport.Send(socket, payload, group);
            }
            catch (Exception ex)
            {
                _log?.Invoke("mDNS 发送失败：" + ex.Message);
            }
        }
    }

    /// <summary>
    /// Answers one question on the interface the question arrived on.
    ///
    /// <para><b>What was wrong.</b> This used to pick the socket by comparing the *sender's* address
    /// with each socket's own bound address (<c>s.LocalEndPoint.Address == remote.Address</c>). A
    /// phone's address is never one of this machine's addresses, so the comparison could never match
    /// and every reply fell back to <c>sockets[0]</c> — on a machine with more than one physical
    /// adapter the answer left through the wrong one whenever the question had not arrived on the
    /// first. The receiving socket is already known (it is the one that completed the receive), so it
    /// is used directly and no interface is guessed.</para>
    /// </summary>
    internal void SendUnicast(Socket? receiving, byte[] payload, IPEndPoint remote)
    {
        Socket[] sockets;
        lock (_gate) sockets = _sockets.ToArray();

        if (receiving is not null && sockets.Contains(receiving))
        {
            try { _transport.Send(receiving, payload, remote); }
            catch (Exception ex) { _log?.Invoke("mDNS 单播回复失败：" + ex.Message); }
            return;
        }

        // No receiving socket was supplied. That only happens through the test/entry overload of
        // HandleDatagram, and with exactly one interface the answer is unambiguous. With several, the
        // old behaviour (blindly take sockets[0]) is precisely the bug, so nothing is sent and the
        // reason is logged instead of quietly answering from an interface nobody asked on.
        if (sockets.Length == 1)
        {
            try { _transport.Send(sockets[0], payload, remote); }
            catch (Exception ex) { _log?.Invoke("mDNS 单播回复失败：" + ex.Message); }
            return;
        }

        if (sockets.Length > 1)
            _log?.Invoke("mDNS 收到需要单播回复的问题，但没有收到它的接口信息；为避免从错误的网卡回复，本次不发送。");
    }

    public void Dispose() => Stop();
}
