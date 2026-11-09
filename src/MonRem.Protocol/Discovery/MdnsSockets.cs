using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MonRem.Protocol.Discovery;

/// <summary>Utilidades de rede compartilhadas pelo responder (host) e pelo browser (cliente).</summary>
public static class MdnsSockets
{
    public static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");
    public const int Port = 5353;
    public static readonly IPEndPoint GroupEndpoint = new(Group, Port);

    /// <summary>Endereços IPv4 das interfaces ativas que suportam multicast (sem loopback).</summary>
    public static IReadOnlyList<IPAddress> LocalIPv4(bool includeLoopback = false)
    {
        List<IPAddress> result = [];
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            var loopback = nic.NetworkInterfaceType == NetworkInterfaceType.Loopback;
            if (loopback && !includeLoopback) continue;
            if (!loopback && !nic.SupportsMulticast) continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !ua.Address.Equals(IPAddress.Any))
                    result.Add(ua.Address);
            }
        }
        return result;
    }

    /// <summary>Socket UDP IPv4 com reuso de endereço (o Windows e o Bonjour também usam a 5353).</summary>
    public static Socket CreateUdp()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        if (OperatingSystem.IsWindows()) socket.ExclusiveAddressUse = false;
        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
        return socket;
    }

    /// <summary>Entra no grupo mDNS em cada interface; ignora as que recusarem.</summary>
    public static int JoinGroup(Socket socket, IEnumerable<IPAddress> interfaces)
    {
        var joined = 0;
        foreach (var local in interfaces)
        {
            try
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group, local));
                joined++;
            }
            catch (SocketException)
            {
                // Interface sem rota multicast ou já inscrita: segue nas outras.
            }
        }
        return joined;
    }

    /// <summary>Envia ao grupo por cada interface (IP_MULTICAST_IF), para alcançar todas as redes do PC.</summary>
    public static async Task SendToGroupAsync(Socket socket, byte[] packet, IEnumerable<IPAddress> interfaces, CancellationToken ct)
    {
        var sent = false;
        foreach (var local in interfaces)
        {
            try
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
                await socket.SendToAsync(packet, SocketFlags.None, GroupEndpoint, ct).ConfigureAwait(false);
                sent = true;
            }
            catch (SocketException)
            {
            }
        }
        if (!sent) await socket.SendToAsync(packet, SocketFlags.None, GroupEndpoint, ct).ConfigureAwait(false);
    }
}
