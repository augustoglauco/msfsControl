using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace MonRem.Host.Video.Discovery;

/// <summary>Responde unicast a pedidos de descoberta que começam com o prefixo (SPEC 13.1).</summary>
public sealed class DiscoveryResponder : IAsyncDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    private readonly IPAddress _bindAddress;
    private readonly int _udpPort;
    private readonly byte[] _reply;
    private readonly ILogger? _logger;
    private readonly CancellationTokenSource _cts = new();
    private UdpClient _socket;
    private Task? _loop;

    public DiscoveryResponder(IPAddress bindAddress, int udpPort, string hostName, int tcpPort, ILogger? logger = null)
    {
        _bindAddress = bindAddress;
        _udpPort = udpPort;
        _reply = DiscoveryReply.Build(hostName, tcpPort);
        _logger = logger;
        _socket = Bind();
        LocalPort = ((IPEndPoint)_socket.Client.LocalEndPoint!).Port;
    }

    /// <summary>Porta efetiva (útil quando <c>udpPort</c> = 0).</summary>
    public int LocalPort { get; private set; }

    public void Start() => _loop ??= Task.Run(() => RunAsync(_cts.Token));

    private UdpClient Bind()
    {
        var socket = new UdpClient(new IPEndPoint(_bindAddress, LocalPort != 0 ? LocalPort : _udpPort));
        return socket;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var received = await _socket.ReceiveAsync(ct).ConfigureAwait(false);
                if (received.Buffer.AsSpan().StartsWith(DiscoveryReply.RequestPrefix))
                    await _socket.SendAsync(_reply, received.RemoteEndPoint, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                _logger?.LogWarning(ex, "Descoberta UDP falhou; recriando o socket em {Delay}s", RetryDelay.TotalSeconds);
                try { await Task.Delay(RetryDelay, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                _socket.Dispose();
                _socket = Bind();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _socket.Dispose();
        if (_loop is not null)
            await _loop.ConfigureAwait(false);
        _cts.Dispose();
    }
}
