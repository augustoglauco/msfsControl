using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using MonRem.Host.Video.Discovery;
using MonRem.Host.Video.Pipeline;

namespace MonRem.Host.Video.Server;

/// <summary>Servidor TCP do visualizador (uma <see cref="ClientSession"/> por conexão) e respondedor de descoberta.</summary>
public sealed class VideoServer : IAsyncDisposable
{
    private readonly VideoServerOptions _options;
    private readonly FrameSourceFactory _sources;
    private readonly Func<IVideoEncoder> _encoders;
    private readonly ILogger? _logger;
    private readonly TcpListener _listener;
    private readonly DiscoveryResponder _discovery;
    private int _activeSessions;

    /// <summary>As portas são abertas aqui, para <see cref="LocalTcpPort"/> já valer quando a porta pedida é 0.</summary>
    public VideoServer(VideoServerOptions options, FrameSourceFactory sources, Func<IVideoEncoder> encoders, ILogger? logger = null)
    {
        _options = options;
        _sources = sources;
        _encoders = encoders;
        _logger = logger;
        _listener = new TcpListener(IPAddress.Any, options.TcpPort);
        _listener.Start();
        LocalTcpPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _discovery = new DiscoveryResponder(IPAddress.Any, options.UdpPort, options.HostName, LocalTcpPort, logger);
    }

    public int LocalTcpPort { get; }
    public int LocalUdpPort => _discovery.LocalPort;
    public int ActiveSessions => Volatile.Read(ref _activeSessions);

    /// <summary>Aceita conexões até o cancelamento, depois espera as sessões terminarem.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        _discovery.Start();
        _logger?.LogInformation("Vídeo ouvindo em TCP {Tcp} e UDP {Udp}", LocalTcpPort, LocalUdpPort);
        var sessions = new List<Task>();
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                Interlocked.Increment(ref _activeSessions);
                var session = new ClientSession(client, _options, _sources, _encoders, _logger);
                sessions.Add(Task.Run(async () =>
                {
                    try { await session.RunAsync(ct).ConfigureAwait(false); }
                    finally { Interlocked.Decrement(ref _activeSessions); }
                }, CancellationToken.None));
                sessions.RemoveAll(t => t.IsCompleted);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _listener.Stop();
            await Task.WhenAll(sessions).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
        await _discovery.DisposeAsync().ConfigureAwait(false);
    }
}
