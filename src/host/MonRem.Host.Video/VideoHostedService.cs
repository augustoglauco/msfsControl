using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MonRem.Host.Video.Pipeline;
using MonRem.Host.Video.Server;

namespace MonRem.Host.Video;

/// <summary>Roda o <see cref="VideoServer"/> com a fonte e o codificador sintéticos. Falhar aqui nunca derruba o host.</summary>
internal sealed class VideoHostedService(IOptions<VideoOptions> options, ILogger<VideoHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        var serverOptions = new VideoServerOptions
        {
            HostName = o.HostName ?? Environment.MachineName,
            TcpPort = o.TcpPort,
            UdpPort = o.UdpPort,
            Fps = o.Fps,
        };

        try
        {
            await using var server = new VideoServer(
                serverOptions, (w, h, fps) => new SyntheticFrameSource(w, h, fps), () => new TestPatternEncoder(), logger);
            await server.RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            logger.LogError(ex, "Vídeo não iniciou: porta {Tcp} (TCP) ou {Udp} (UDP) em uso. Pare o serviço do spacedesk ou mude MonRem:Video:TcpPort/UdpPort", o.TcpPort, o.UdpPort);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
