using System.Buffers.Binary;
using System.Text;

namespace MonRem.Host.Video.Discovery;

/// <summary>Resposta de descoberta UDP do SPEC 13.1 e 15.1.</summary>
public static class DiscoveryReply
{
    public const int Size = 308;
    private const int MaxNameBytes = 258;

    /// <summary><c>SPACEDESK-NET-CLIENT\0</c> (21 bytes). O pedido só precisa <b>começar</b> assim.</summary>
    public static readonly byte[] RequestPrefix = "SPACEDESK-NET-CLIENT\0"u8.ToArray();

    /// <summary>Sem TLS: byte 280 = 43, sem o bit 0x10.</summary>
    public static byte[] Build(string hostName, int tcpPort)
    {
        var reply = new byte[Size];
        var name = Encoding.Unicode.GetBytes(hostName);
        Array.Copy(name, reply, Math.Min(name.Length, MaxNameBytes));
        BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(260), (uint)tcpPort);
        BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(264), 10);
        BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(272), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(276), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(280), 43);
        return reply;
    }
}
