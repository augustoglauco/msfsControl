using System.Buffers;
using System.Buffers.Binary;

namespace MonRem.Host.Video.Wire;

/// <summary>Enquadramento do SPEC 12.1: cabeçalho de 128 bytes (32 uint32 little-endian) + corpo.</summary>
public static class WireCodec
{
    public const int HeaderSize = 128;
    internal const int HeaderInts = HeaderSize / 4;

    public static async ValueTask WriteAsync(Stream stream, WireMessage message, CancellationToken ct)
    {
        var buffer = new byte[HeaderSize + message.Body.Length];
        for (var i = 0; i < HeaderInts; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(i * 4, 4), message.Ints[i]);
        message.Body.CopyTo(buffer, HeaderSize);
        await stream.WriteAsync(buffer, ct).ConfigureAwait(false);
    }

    /// <returns><c>null</c> se o fluxo termina limpo na fronteira de uma mensagem.</returns>
    /// <exception cref="InvalidDataException">Corpo maior que <paramref name="maxBody"/> (nada é alocado nem lido).</exception>
    /// <exception cref="EndOfStreamException">O fluxo termina no meio de uma mensagem.</exception>
    public static async ValueTask<WireMessage?> ReadAsync(Stream stream, int maxBody, CancellationToken ct)
    {
        var headerBytes = new byte[HeaderSize];
        var first = await ReadFullyAsync(stream, headerBytes, ct).ConfigureAwait(false);
        if (first == 0)
            return null;
        if (first < HeaderSize)
            throw new EndOfStreamException("Cabeçalho truncado.");

        var ints = new uint[HeaderInts];
        for (var i = 0; i < HeaderInts; i++)
            ints[i] = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes.AsSpan(i * 4, 4));

        if (ints[1] > (uint)maxBody)
            throw new InvalidDataException($"Corpo de {ints[1]} bytes excede o limite de {maxBody}.");

        var body = new byte[ints[1]];
        if (body.Length > 0 && await ReadFullyAsync(stream, body, ct).ConfigureAwait(false) < body.Length)
            throw new EndOfStreamException("Corpo truncado.");
        return new WireMessage(ints, body);
    }

    private static async ValueTask<int> ReadFullyAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (n == 0)
                break;
            total += n;
        }
        return total;
    }
}
