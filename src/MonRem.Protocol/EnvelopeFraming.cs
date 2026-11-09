using System.Buffers.Binary;
using Google.Protobuf;
using MonRem.Protocol.V1;

namespace MonRem.Protocol;

/// <summary>Erro de protocolo: quadro inválido, tamanho excedido, mensagem malformada.</summary>
public sealed class ProtocolException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Canal de controle: [u32 tamanho big-endian][Envelope], tamanho máximo 1 MiB.
/// </summary>
public static class EnvelopeFraming
{
    public const int MaxFrameSize = 1 << 20;

    /// <summary>Lê um Envelope. Retorna null se o stream terminou limpo antes do cabeçalho.</summary>
    public static async ValueTask<Envelope?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        if (!await ReadExactAsync(stream, header, allowCleanEof: true, ct).ConfigureAwait(false))
            return null;

        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length > MaxFrameSize)
            throw new ProtocolException($"quadro de {length} bytes excede o máximo de {MaxFrameSize}");

        var payload = new byte[length];
        await ReadExactAsync(stream, payload, allowCleanEof: false, ct).ConfigureAwait(false);
        try
        {
            return Envelope.Parser.ParseFrom(payload);
        }
        catch (InvalidProtocolBufferException ex)
        {
            throw new ProtocolException("Envelope malformado", ex);
        }
    }

    /// <summary>Serializa em um único buffer (cabeçalho + corpo) para uma única escrita.</summary>
    public static byte[] Encode(Envelope envelope)
    {
        var size = envelope.CalculateSize();
        if (size > MaxFrameSize)
            throw new ProtocolException($"Envelope de {size} bytes excede o máximo de {MaxFrameSize}");

        var buffer = new byte[4 + size];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)size);
        envelope.WriteTo(buffer.AsSpan(4));
        return buffer;
    }

    private static async ValueTask<bool> ReadExactAsync(Stream stream, byte[] buffer, bool allowCleanEof, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
            {
                if (read == 0 && allowCleanEof) return false;
                throw new ProtocolException("conexão encerrada no meio de um quadro");
            }
            read += n;
        }
        return true;
    }
}

/// <summary>Escrita serializada de Envelopes num stream compartilhado por várias tarefas.</summary>
public sealed class EnvelopeWriter(Stream stream) : IAsyncDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async ValueTask WriteAsync(Envelope envelope, CancellationToken ct)
    {
        var frame = EnvelopeFraming.Encode(envelope);
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(frame, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _lock.Dispose();
        return ValueTask.CompletedTask;
    }
}
