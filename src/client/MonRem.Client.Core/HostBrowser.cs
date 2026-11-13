using System.Net;
using System.Net.Sockets;
using MonRem.Protocol.Discovery;

namespace MonRem.Client.Core;

/// <summary>
/// Procura hosts na rede com uma pergunta mDNS em unicast "legado" (porta de origem aleatória):
/// as respostas voltam direto para o socket, sem precisar receber multicast.
/// No Android isso dispensa o MulticastLock.
/// </summary>
public static class HostBrowser
{
    public static async Task<IReadOnlyList<DiscoveredHost>> BrowseAsync(TimeSpan duration, CancellationToken ct = default)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));

        var query = new DnsMessage { Id = (ushort)Random.Shared.Next(1, ushort.MaxValue) };
        query.Questions.Add(new DnsQuestion(MonRemService.ServiceType, DnsType.Ptr, UnicastResponse: true));
        var packet = query.Encode();

        var found = new Dictionary<string, DiscoveredHost>();
        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(duration);

        var sender = SendRepeatedlyAsync(socket, packet, window.Token);
        var buffer = new byte[9000];
        try
        {
            while (!window.IsCancellationRequested)
            {
                SocketReceiveFromResult result;
                try
                {
                    result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), window.Token).ConfigureAwait(false);
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
                {
                    continue;
                }

                DnsMessage response;
                try
                {
                    response = DnsMessage.Parse(buffer.AsSpan(0, result.ReceivedBytes));
                }
                catch (FormatException)
                {
                    continue;
                }
                if (!response.IsResponse) continue;
                foreach (var host in MonRemService.Interpret(response)) found[host.HostId] = host;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Fim da janela de busca.
        }
        try { await sender.ConfigureAwait(false); } catch (OperationCanceledException) { }
        return [.. found.Values.OrderBy(h => h.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    private static async Task SendRepeatedlyAsync(Socket socket, byte[] packet, CancellationToken ct)
    {
        // Três tentativas espaçadas: Wi-Fi perde multicast com frequência.
        for (var i = 0; i < 3 && !ct.IsCancellationRequested; i++)
        {
            await MdnsSockets.SendToGroupAsync(socket, packet, MdnsSockets.LocalIPv4(includeLoopback: true), ct).ConfigureAwait(false);
            // Host na mesma máquina: multicast em loopback nem sempre funciona, então pergunta direto também.
            try
            {
                await socket.SendToAsync(packet, SocketFlags.None, new IPEndPoint(IPAddress.Loopback, MdnsSockets.Port), ct).ConfigureAwait(false);
            }
            catch (SocketException)
            {
            }
            await Task.Delay(TimeSpan.FromMilliseconds(400), ct).ConfigureAwait(false);
        }
    }
}
