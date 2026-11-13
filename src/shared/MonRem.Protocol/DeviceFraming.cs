using System.Buffers.Binary;
using Google.Protobuf;
using MonRem.Protocol.V1;

namespace MonRem.Protocol;

/// <summary>
/// Painéis ESP32 por USB: quadro = COBS(DeviceFrame ‖ CRC-16/CCITT-FALSE little-endian) + 0x00.
/// DeviceFrame serializado ≤ 1024 bytes.
/// </summary>
public static class DeviceSerialFraming
{
    public const int MaxPayload = 1024;

    /// <summary>Pior caso do COBS: +1 byte a cada 254, mais o terminador.</summary>
    public const int MaxEncoded = MaxPayload + 2 + (MaxPayload + 2) / 254 + 2;

    public static byte[] Encode(DeviceFrame frame)
    {
        var size = frame.CalculateSize();
        if (size > MaxPayload) throw new ProtocolException($"DeviceFrame de {size} bytes excede {MaxPayload}");

        var raw = new byte[size + 2];
        frame.WriteTo(raw.AsSpan(0, size));
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(size), Crc16.Compute(raw.AsSpan(0, size)));

        var encoded = new byte[raw.Length + raw.Length / 254 + 2];
        var n = Cobs.Encode(raw, encoded);
        encoded[n] = 0;
        return encoded[..(n + 1)];
    }

    /// <summary>Decodifica um quadro SEM o terminador 0x00. Retorna null se o CRC ou o COBS estiverem errados.</summary>
    public static DeviceFrame? Decode(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length < 3 || encoded.Length > MaxEncoded) return null;
        var raw = new byte[encoded.Length];
        var n = Cobs.Decode(encoded, raw);
        if (n < 2) return null;

        var payload = raw.AsSpan(0, n - 2);
        if (BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(n - 2)) != Crc16.Compute(payload)) return null;
        try
        {
            return DeviceFrame.Parser.ParseFrom(payload);
        }
        catch (InvalidProtocolBufferException)
        {
            return null;
        }
    }
}

/// <summary>
/// Lê quadros seriais de um Stream (porta serial ou pipe). Quadros corrompidos são descartados e contados:
/// o protocolo dos painéis é de estado absoluto, então o próximo quadro corrige.
/// </summary>
public sealed class DeviceSerialReader(Stream stream)
{
    private readonly byte[] _buffer = new byte[4096];
    private readonly byte[] _frame = new byte[DeviceSerialFraming.MaxEncoded];
    private int _bufferLen;
    private int _bufferPos;
    private int _frameLen;
    private bool _overflow;

    public long DroppedFrames { get; private set; }

    /// <summary>Próximo quadro válido; null quando o stream termina.</summary>
    public async ValueTask<DeviceFrame?> ReadAsync(CancellationToken ct)
    {
        while (true)
        {
            if (_bufferPos == _bufferLen)
            {
                _bufferLen = await stream.ReadAsync(_buffer, ct).ConfigureAwait(false);
                _bufferPos = 0;
                if (_bufferLen == 0) return null;
            }

            while (_bufferPos < _bufferLen)
            {
                var b = _buffer[_bufferPos++];
                if (b != 0)
                {
                    if (_frameLen < _frame.Length) _frame[_frameLen++] = b;
                    else _overflow = true;
                    continue;
                }

                // Fim de quadro.
                var length = _frameLen;
                var overflow = _overflow;
                _frameLen = 0;
                _overflow = false;
                if (length == 0) continue; // 0x00 extra (sincronismo): ignora

                var frame = overflow ? null : DeviceSerialFraming.Decode(_frame.AsSpan(0, length));
                if (frame is not null) return frame;
                DroppedFrames++;
            }
        }
    }
}

/// <summary>Consistent Overhead Byte Stuffing (sem o terminador).</summary>
public static class Cobs
{
    public static int Encode(ReadOnlySpan<byte> input, Span<byte> output)
    {
        var codeIndex = 0;
        var write = 1;
        byte code = 1;
        for (var i = 0; i < input.Length; i++)
        {
            var b = input[i];
            if (b == 0)
            {
                output[codeIndex] = code;
                codeIndex = write++;
                code = 1;
                continue;
            }
            output[write++] = b;
            if (++code == 0xFF)
            {
                output[codeIndex] = code;
                if (i == input.Length - 1) return write; // bloco cheio no fim: sem código extra
                codeIndex = write++;
                code = 1;
            }
        }
        output[codeIndex] = code;
        return write;
    }

    /// <summary>Retorna o tamanho decodificado, ou -1 se a entrada for inválida.</summary>
    public static int Decode(ReadOnlySpan<byte> input, Span<byte> output)
    {
        var read = 0;
        var write = 0;
        while (read < input.Length)
        {
            var code = input[read++];
            if (code == 0) return -1;
            for (var i = 1; i < code; i++)
            {
                if (read >= input.Length) return -1;
                var b = input[read++];
                if (b == 0) return -1;
                output[write++] = b;
            }
            if (code != 0xFF && read < input.Length) output[write++] = 0;
        }
        return write;
    }
}

/// <summary>CRC-16/CCITT-FALSE: polinômio 0x1021, inicial 0xFFFF, sem reflexão, sem XOR final.</summary>
public static class Crc16
{
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= (ushort)(b << 8);
            for (var i = 0; i < 8; i++)
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }
        return crc;
    }
}

/// <summary>Painéis ESP32 por Wi-Fi: [u16 big-endian tamanho][DeviceFrame], até 4 KiB.</summary>
public static class DeviceTcpFraming
{
    public const int MaxPayload = 4096;

    public static byte[] Encode(DeviceFrame frame)
    {
        var size = frame.CalculateSize();
        if (size > MaxPayload) throw new ProtocolException($"DeviceFrame de {size} bytes excede {MaxPayload}");
        var buffer = new byte[2 + size];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, (ushort)size);
        frame.WriteTo(buffer.AsSpan(2));
        return buffer;
    }
}
