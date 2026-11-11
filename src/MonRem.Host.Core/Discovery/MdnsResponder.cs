using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using MonRem.Protocol.Discovery;

namespace MonRem.Host.Core.Discovery;

/// <summary>
/// Anuncia o host por mDNS/DNS-SD como <c>_monrem._tcp.local</c> e responde às perguntas.
/// Anuncia ao iniciar (2 vezes, 1 s de intervalo), quando o estado do sim muda (TXT "sim")
/// e se despede com TTL 0 ao parar.
/// </summary>
public sealed class MdnsResponder(MonRemService service, ILogger<MdnsResponder> logger)
{
    public MonRemService Service => service;

    public async Task RunAsync(CancellationToken ct)
    {
        using var socket = MdnsSockets.CreateUdp();
        try
        {
            socket.Bind(new IPEndPoint(IPAddress.Any, MdnsSockets.Port));
        }
        catch (SocketException ex)
        {
            logger.LogWarning("mDNS indisponível (porta 5353: {Message}). Os clientes ainda podem usar o IP manual.", ex.Message);
            return;
        }

        var interfaces = MdnsSockets.LocalIPv4();
        var joined = MdnsSockets.JoinGroup(socket, interfaces);
        logger.LogInformation("mDNS: anunciando \"{Instance}\" em {Count} interface(s)", service.InstanceName, joined);

        var announcer = AnnounceAsync(socket, ct);
        try
        {
            var buffer = new byte[9000];
            while (!ct.IsCancellationRequested)
            {
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                SocketReceiveFromResult result;
                try
                {
                    result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, remote, ct).ConfigureAwait(false);
                }
                catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize)
                {
                    continue; // ICMP de porta inalcançável (Windows) ou pacote gigante: ignora
                }
                await HandleAsync(socket, buffer.AsMemory(0, result.ReceivedBytes), (IPEndPoint)result.RemoteEndPoint, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            try { await announcer.ConfigureAwait(false); } catch (OperationCanceledException) { }
            await SendGoodbyeAsync(socket).ConfigureAwait(false);
        }
    }

    /// <summary>Pede um novo anúncio (ex.: o sim conectou e o TXT "sim" mudou).</summary>
    public void Reannounce()
    {
        try
        {
            _reannounce.Release();
        }
        catch (SemaphoreFullException)
        {
            // Já há um anúncio pendente.
        }
    }

    private readonly SemaphoreSlim _reannounce = new(0, 1);

    private async Task HandleAsync(Socket socket, ReadOnlyMemory<byte> packet, IPEndPoint remote, CancellationToken ct)
    {
        DnsMessage query;
        try
        {
            query = DnsMessage.Parse(packet.Span);
        }
        catch (FormatException)
        {
            return;
        }
        if (query.IsResponse || query.Questions.Count == 0) return;

        var legacy = remote.Port != MdnsSockets.Port;
        var response = service.Answer(query, MdnsSockets.LocalIPv4(), legacy);
        if (response is null) return;

        var bytes = response.Encode();
        try
        {
            if (legacy || query.Questions.Any(q => q.UnicastResponse))
                await socket.SendToAsync(bytes, SocketFlags.None, remote, ct).ConfigureAwait(false);
            else
                await MdnsSockets.SendToGroupAsync(socket, bytes, MdnsSockets.LocalIPv4(), ct).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            logger.LogDebug("mDNS: falha ao responder a {Remote}: {Message}", remote, ex.Message);
        }
    }

    private async Task AnnounceAsync(Socket socket, CancellationToken ct)
    {
        for (var i = 0; i < 2; i++)
        {
            await SendAnnouncementAsync(socket, goodbye: false, ct).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        }
        while (!ct.IsCancellationRequested)
        {
            await _reannounce.WaitAsync(ct).ConfigureAwait(false);
            await SendAnnouncementAsync(socket, goodbye: false, ct).ConfigureAwait(false);
        }
    }

    private async Task SendAnnouncementAsync(Socket socket, bool goodbye, CancellationToken ct)
    {
        var addresses = MdnsSockets.LocalIPv4();
        try
        {
            await MdnsSockets.SendToGroupAsync(socket, service.Announcement(addresses, goodbye).Encode(), addresses, ct).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            logger.LogDebug("mDNS: falha ao anunciar: {Message}", ex.Message);
        }
    }

    private async Task SendGoodbyeAsync(Socket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            await SendAnnouncementAsync(socket, goodbye: true, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
