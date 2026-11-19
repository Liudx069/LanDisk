using System.Net;
using System.Net.Sockets;

namespace LanDisk.Core.Networking;

/// <summary>
/// How one datagram leaves one interface.
///
/// <para>Extracted so the interface choice can be observed: a reply must leave through the socket the
/// question arrived on, and proving that in a unit test otherwise needs two physical NICs. The
/// production implementation is <see cref="SocketMdnsTransport"/>; tests substitute a recorder.</para>
/// </summary>
public interface IMdnsTransport
{
    void Send(Socket socket, byte[] payload, IPEndPoint destination);
}

/// <summary>The only implementation the product ships: hand the datagram to that socket.</summary>
public sealed class SocketMdnsTransport : IMdnsTransport
{
    public static SocketMdnsTransport Instance { get; } = new();

    public void Send(Socket socket, byte[] payload, IPEndPoint destination) => socket.SendTo(payload, destination);
}
