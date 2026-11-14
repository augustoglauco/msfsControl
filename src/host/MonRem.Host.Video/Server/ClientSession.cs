using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using MonRem.Host.Video.Pipeline;
using MonRem.Host.Video.Wire;

namespace MonRem.Host.Video.Server;

public delegate IFrameSource FrameSourceFactory(int width, int height, int fps);

/// <summary>Uma conexão de visualizador: identificação, preâmbulo, quadros com janela de ACK.</summary>
internal sealed class ClientSession(
    TcpClient tcp,
    VideoServerOptions options,
    FrameSourceFactory sources,
    Func<IVideoEncoder> encoders,
    ILogger? logger)
{
    /// <summary>Mensagens do cliente são só cabeçalhos e a identificação (334 bytes).</summary>
    private const int MaxClientBody = 4096;
    private const int FallbackWidth = 1280, FallbackHeight = 720;

    public async Task RunAsync(CancellationToken ct)
    {
        var remote = tcp.Client.RemoteEndPoint;
        try
        {
            tcp.NoDelay = true;
            await using var stream = tcp.GetStream();

            var identification = await ReadIdentificationAsync(stream, ct).ConfigureAwait(false);
            if (identification is null)
            {
                logger?.LogInformation("{Remote}: identificação inválida ou ausente, conexão encerrada", remote);
                return;
            }

            var valid = identification.Width is > 0 && identification.Height is > 0
                && identification.Width <= options.MaxDimension && identification.Height <= options.MaxDimension;
            var (width, height) = valid ? (identification.Width, identification.Height) : (FallbackWidth, FallbackHeight);
            var fps = identification.Fps > 0 && identification.Fps <= 240 ? identification.Fps : options.Fps;
            logger?.LogInformation("{Remote}: {Name} {Width}x{Height}@{Fps}", remote, identification.DeviceName, width, height, fps);

            foreach (var message in Messages.Preamble())
                await WireCodec.WriteAsync(stream, message, ct).ConfigureAwait(false);

            await StreamAsync(stream, width, height, fps, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or InvalidDataException or EndOfStreamException or ObjectDisposedException)
        {
            logger?.LogDebug("{Remote}: sessão terminou ({Reason})", remote, ex.GetType().Name);
        }
        finally
        {
            tcp.Dispose();
        }
    }

    private async Task<Identification?> ReadIdentificationAsync(Stream stream, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.IdentifyTimeout);
        try
        {
            while (true)
            {
                var message = await WireCodec.ReadAsync(stream, MaxClientBody, timeout.Token).ConfigureAwait(false);
                if (message is null)
                    return null;
                if (message.Value.Type == MessageType.EvtDisplaySettings)
                    continue; // SPEC 15.6: o celular pode mandar o tipo 6 antes da identificação
                return Identification.TryParse(message.Value, out var id) ? id : null;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task StreamAsync(Stream stream, int width, int height, int fps, CancellationToken ct)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var credit = new SemaphoreSlim(options.AckWindow, options.AckWindow);
        await using var source = sources(width, height, fps);
        using var encoder = encoders();

        var reader = Task.Run(() => ReadLoopAsync(stream, credit, session), CancellationToken.None);
        try
        {
            while (!session.IsCancellationRequested)
            {
                await credit.WaitAsync(session.Token).ConfigureAwait(false);
                var frame = await source.WaitForFrameAsync(session.Token).ConfigureAwait(false);
                if (frame is null)
                    break;
                var accessUnit = encoder.Encode(frame.Value, forceKeyframe: true);
                await WireCodec.WriteAsync(stream, Messages.Framebuffer(width, height, fps, accessUnit), session.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            await session.CancelAsync().ConfigureAwait(false);
            await reader.ConfigureAwait(false);
        }
    }

    /// <summary>Lê o que o cliente manda. ACK devolve crédito; o resto é registrado e ignorado nesta etapa.</summary>
    private async Task ReadLoopAsync(Stream stream, SemaphoreSlim credit, CancellationTokenSource session)
    {
        try
        {
            while (true)
            {
                using var loss = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                loss.CancelAfter(options.ConnectionLossTimeout);
                var message = await WireCodec.ReadAsync(stream, MaxClientBody, loss.Token).ConfigureAwait(false);
                if (message is null)
                    break;

                if (message.Value.Type == MessageType.FlowControlAck)
                {
                    try { credit.Release(); }
                    catch (SemaphoreFullException) { /* ACK a mais: ignorar */ }
                }
                else
                {
                    logger?.LogTrace("Mensagem {Type} ignorada", message.Value.Type);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or InvalidDataException or EndOfStreamException or ObjectDisposedException)
        {
            logger?.LogDebug("Leitura terminou ({Reason})", ex.GetType().Name);
        }
        finally
        {
            await session.CancelAsync().ConfigureAwait(false);
        }
    }
}
