using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace MonRem.Protocol.Discovery;

/// <summary>Tipos de registro DNS usados pelo mDNS/DNS-SD.</summary>
public enum DnsType : ushort
{
    A = 1,
    Ptr = 12,
    Txt = 16,
    Aaaa = 28,
    Srv = 33,
    Any = 255,
}

public sealed record DnsQuestion(string Name, DnsType Type, bool UnicastResponse);

/// <summary>Registro de recurso. <see cref="Data"/> já decodificado conforme o tipo.</summary>
public sealed record DnsRecord(string Name, DnsType Type, uint Ttl, bool CacheFlush, object Data);

public sealed record SrvData(ushort Priority, ushort Weight, ushort Port, string Target);

/// <summary>
/// Codec DNS mínimo para mDNS (RFC 6762) e DNS-SD (RFC 6763): perguntas, PTR, SRV, TXT, A.
/// Lê nomes comprimidos; escreve nomes sem compressão (permitido, só gasta alguns bytes).
/// </summary>
public sealed class DnsMessage
{
    public const ushort ClassIn = 1;
    private const ushort CacheFlushBit = 0x8000;
    private const ushort UnicastResponseBit = 0x8000;
    private const int MaxPointerJumps = 32;

    public ushort Id { get; init; }
    public bool IsResponse { get; init; }
    public List<DnsQuestion> Questions { get; } = [];
    public List<DnsRecord> Answers { get; } = [];
    public List<DnsRecord> Additionals { get; } = [];

    public static DnsMessage Parse(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 12) throw new FormatException("pacote DNS menor que o cabeçalho");
        var flags = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
        var message = new DnsMessage
        {
            Id = BinaryPrimitives.ReadUInt16BigEndian(packet),
            IsResponse = (flags & 0x8000) != 0,
        };
        var qd = BinaryPrimitives.ReadUInt16BigEndian(packet[4..]);
        var an = BinaryPrimitives.ReadUInt16BigEndian(packet[6..]);
        var ns = BinaryPrimitives.ReadUInt16BigEndian(packet[8..]);
        var ar = BinaryPrimitives.ReadUInt16BigEndian(packet[10..]);

        var offset = 12;
        for (var i = 0; i < qd; i++)
        {
            var name = ReadName(packet, ref offset);
            Need(packet, offset, 4);
            var type = (DnsType)BinaryPrimitives.ReadUInt16BigEndian(packet[offset..]);
            var cls = BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 2)..]);
            offset += 4;
            message.Questions.Add(new DnsQuestion(name, type, (cls & UnicastResponseBit) != 0));
        }

        for (var i = 0; i < an + ns + ar; i++)
        {
            var record = ReadRecord(packet, ref offset);
            if (record is null) continue;
            if (i < an) message.Answers.Add(record);
            else if (i >= an + ns) message.Additionals.Add(record);
        }
        return message;
    }

    public byte[] Encode()
    {
        using var ms = new MemoryStream(512);
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header, Id);
        // Resposta mDNS: QR=1, AA=1.
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], IsResponse ? (ushort)0x8400 : (ushort)0);
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], (ushort)Questions.Count);
        BinaryPrimitives.WriteUInt16BigEndian(header[6..], (ushort)Answers.Count);
        BinaryPrimitives.WriteUInt16BigEndian(header[8..], 0);
        BinaryPrimitives.WriteUInt16BigEndian(header[10..], (ushort)Additionals.Count);
        ms.Write(header);

        foreach (var q in Questions)
        {
            WriteName(ms, q.Name);
            WriteU16(ms, (ushort)q.Type);
            WriteU16(ms, (ushort)(ClassIn | (q.UnicastResponse ? UnicastResponseBit : 0)));
        }
        foreach (var r in Answers) WriteRecord(ms, r);
        foreach (var r in Additionals) WriteRecord(ms, r);
        return ms.ToArray();
    }

    /// <summary>Compara nomes DNS sem diferenciar maiúsculas (ASCII) e ignorando o ponto final.</summary>
    public static bool NameEquals(string a, string b) =>
        string.Equals(a.TrimEnd('.'), b.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

    private static DnsRecord? ReadRecord(ReadOnlySpan<byte> packet, ref int offset)
    {
        var name = ReadName(packet, ref offset);
        Need(packet, offset, 10);
        var type = (DnsType)BinaryPrimitives.ReadUInt16BigEndian(packet[offset..]);
        var cls = BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 2)..]);
        var ttl = BinaryPrimitives.ReadUInt32BigEndian(packet[(offset + 4)..]);
        var length = BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 8)..]);
        offset += 10;
        Need(packet, offset, length);
        var dataStart = offset;
        offset += length;
        var cacheFlush = (cls & CacheFlushBit) != 0;

        object? data = type switch
        {
            DnsType.A when length == 4 => new IPAddress(packet.Slice(dataStart, 4)),
            DnsType.Aaaa when length == 16 => new IPAddress(packet.Slice(dataStart, 16)),
            DnsType.Ptr => ReadNameAt(packet, dataStart),
            DnsType.Srv when length >= 7 => new SrvData(
                BinaryPrimitives.ReadUInt16BigEndian(packet[dataStart..]),
                BinaryPrimitives.ReadUInt16BigEndian(packet[(dataStart + 2)..]),
                BinaryPrimitives.ReadUInt16BigEndian(packet[(dataStart + 4)..]),
                ReadNameAt(packet, dataStart + 6)),
            DnsType.Txt => ReadTxt(packet.Slice(dataStart, length)),
            _ => null,
        };
        return data is null ? null : new DnsRecord(name, type, ttl, cacheFlush, data);
    }

    private static Dictionary<string, string> ReadTxt(ReadOnlySpan<byte> data)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        while (i < data.Length)
        {
            var len = data[i++];
            if (i + len > data.Length) break;
            var entry = Encoding.UTF8.GetString(data.Slice(i, len));
            i += len;
            var eq = entry.IndexOf('=');
            if (eq > 0) result.TryAdd(entry[..eq], entry[(eq + 1)..]);
            else if (entry.Length > 0) result.TryAdd(entry, "");
        }
        return result;
    }

    private static string ReadNameAt(ReadOnlySpan<byte> packet, int offset) => ReadName(packet, ref offset);

    private static string ReadName(ReadOnlySpan<byte> packet, ref int offset)
    {
        var labels = new List<string>();
        var position = offset;
        var jumped = false;
        var jumps = 0;
        while (true)
        {
            Need(packet, position, 1);
            var len = packet[position];
            if (len == 0)
            {
                position++;
                break;
            }
            if ((len & 0xC0) == 0xC0)
            {
                Need(packet, position, 2);
                if (++jumps > MaxPointerJumps) throw new FormatException("laço de ponteiros no nome DNS");
                var pointer = BinaryPrimitives.ReadUInt16BigEndian(packet[position..]) & 0x3FFF;
                if (!jumped) offset = position + 2;
                jumped = true;
                position = pointer;
                continue;
            }
            if ((len & 0xC0) != 0) throw new FormatException("rótulo DNS com tipo reservado");
            Need(packet, position + 1, len);
            labels.Add(Encoding.UTF8.GetString(packet.Slice(position + 1, len)));
            position += 1 + len;
        }
        if (!jumped) offset = position;
        return string.Join('.', labels);
    }

    private static void WriteRecord(Stream s, DnsRecord r)
    {
        WriteName(s, r.Name);
        WriteU16(s, (ushort)r.Type);
        WriteU16(s, (ushort)(ClassIn | (r.CacheFlush ? CacheFlushBit : 0)));
        WriteU32(s, r.Ttl);

        using var data = new MemoryStream();
        switch (r.Data)
        {
            case IPAddress ip:
                data.Write(ip.GetAddressBytes());
                break;
            case string target:
                WriteName(data, target);
                break;
            case SrvData srv:
                WriteU16(data, srv.Priority);
                WriteU16(data, srv.Weight);
                WriteU16(data, srv.Port);
                WriteName(data, srv.Target);
                break;
            case IReadOnlyDictionary<string, string> txt:
                if (txt.Count == 0) data.WriteByte(0);
                foreach (var (k, v) in txt)
                {
                    var bytes = Encoding.UTF8.GetBytes(v.Length == 0 ? k : $"{k}={v}");
                    if (bytes.Length > 255) throw new ArgumentException($"entrada TXT longa demais: {k}");
                    data.WriteByte((byte)bytes.Length);
                    data.Write(bytes);
                }
                break;
            default:
                throw new ArgumentException($"dado não suportado para {r.Type}");
        }
        WriteU16(s, (ushort)data.Length);
        data.Position = 0;
        data.CopyTo(s);
    }

    private static void WriteName(Stream s, string name)
    {
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            if (bytes.Length is 0 or > 63) throw new ArgumentException($"rótulo DNS inválido em \"{name}\"");
            s.WriteByte((byte)bytes.Length);
            s.Write(bytes);
        }
        s.WriteByte(0);
    }

    private static void WriteU16(Stream s, ushort v)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, v);
        s.Write(b);
    }

    private static void WriteU32(Stream s, uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        s.Write(b);
    }

    private static void Need(ReadOnlySpan<byte> packet, int offset, int count)
    {
        if (offset < 0 || offset + count > packet.Length) throw new FormatException("pacote DNS truncado");
    }
}
