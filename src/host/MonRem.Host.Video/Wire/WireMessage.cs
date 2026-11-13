namespace MonRem.Host.Video.Wire;

/// <summary>Uma mensagem do protocolo: 32 inteiros de cabeçalho (int 0 = tipo, int 1 = bytes de corpo) e o corpo.</summary>
public readonly record struct WireMessage(uint[] Ints, byte[] Body)
{
    public MessageType Type => (MessageType)Ints[0];

    public static WireMessage Create(MessageType type, ReadOnlySpan<byte> body = default, params (int Index, uint Value)[] ints)
    {
        var header = new uint[WireCodec.HeaderInts];
        foreach (var (index, value) in ints)
            header[index] = value;
        header[0] = (uint)type;
        header[1] = (uint)body.Length;
        return new WireMessage(header, body.ToArray());
    }
}
