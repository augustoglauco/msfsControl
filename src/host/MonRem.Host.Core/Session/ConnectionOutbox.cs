using System.Threading.Channels;
using MonRem.Protocol;
using MonRem.Protocol.V1;

namespace MonRem.Host.Core.Session;

/// <summary>
/// Fila de saída de uma conexão, drenada por uma única tarefa de escrita.
/// Quem produz nunca espera o socket: um cliente lento não trava o hub nem os outros clientes.
/// - Respostas e notificações: fila limitada; se lotar, o cliente não está lendo e a conexão cai.
/// - SimValues: coalescidos por sub_id (último valor de cada handle vence), sem crescer.
/// </summary>
internal sealed class ConnectionOutbox
{
    public const int MaxQueuedMessages = 1024;
    private static readonly TimeSpan StalledWriteLimit = TimeSpan.FromSeconds(10);

    private readonly Channel<Envelope> _control = Channel.CreateBounded<Envelope>(
        new BoundedChannelOptions(MaxQueuedMessages) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Lock _lock = new();
    private readonly Dictionary<uint, SimValues> _pendingValues = [];
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private readonly Action<string> _onFatal;
    private int _closed;

    public ConnectionOutbox(Action<string> onFatal) => _onFatal = onFatal;

    /// <summary>Resposta ou notificação. Retorna false (e derruba a conexão) se a fila estiver cheia.</summary>
    public bool Enqueue(Envelope envelope)
    {
        if (Volatile.Read(ref _closed) == 1) return false;
        if (!_control.Writer.TryWrite(envelope))
        {
            Fail($"fila de saída cheia ({MaxQueuedMessages}): cliente não está lendo");
            return false;
        }
        _signal.Release();
        return true;
    }

    /// <summary>Valores de assinatura: junta com o que ainda não foi enviado.</summary>
    public void OfferSimValues(SimValues values)
    {
        if (Volatile.Read(ref _closed) == 1) return;
        lock (_lock)
        {
            if (_pendingValues.TryGetValue(values.SubId, out var pending))
            {
                pending.FullState |= values.FullState;
                foreach (var item in values.Items)
                {
                    var existing = pending.Items.FirstOrDefault(i => i.Handle == item.Handle);
                    if (existing is not null) pending.Items.Remove(existing);
                    pending.Items.Add(item);
                }
                return; // já sinalizado
            }
            _pendingValues[values.SubId] = values.Clone();
        }
        _signal.Release();
    }

    /// <summary>Escreve tudo até o cancelamento. Uma escrita parada por mais de 10 s derruba a conexão.</summary>
    public async Task RunWriterAsync(Stream stream, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _signal.WaitAsync(ct).ConfigureAwait(false);

                while (_control.Reader.TryRead(out var envelope))
                    await WriteAsync(stream, envelope, ct).ConfigureAwait(false);

                List<SimValues> values;
                lock (_lock)
                {
                    values = [.. _pendingValues.Values];
                    _pendingValues.Clear();
                }
                foreach (var v in values)
                    await WriteAsync(stream, new Envelope { SimValues = v }, ct).ConfigureAwait(false);

                if (Volatile.Read(ref _closed) == 1 && !_control.Reader.TryPeek(out _)) return;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or ProtocolException or TimeoutException)
        {
            Fail($"falha de escrita: {ex.Message}");
        }
    }

    /// <summary>Não aceita mais nada; o escritor termina depois de esvaziar a fila.</summary>
    public void Close()
    {
        Interlocked.Exchange(ref _closed, 1);
        _control.Writer.TryComplete();
        _signal.Release();
    }

    private static async Task WriteAsync(Stream stream, Envelope envelope, CancellationToken ct)
    {
        var frame = EnvelopeFraming.Encode(envelope);
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StalledWriteLimit);
        try
        {
            await stream.WriteAsync(frame, stall.Token).ConfigureAwait(false);
            await stream.FlushAsync(stall.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("escrita parada há mais de 10 s");
        }
    }

    private void Fail(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0) _onFatal(reason);
    }
}
