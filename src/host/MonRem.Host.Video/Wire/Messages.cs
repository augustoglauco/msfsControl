namespace MonRem.Host.Video.Wire;

/// <summary>Construtores das mensagens do servidor e do ACK (valores do SPEC 12 a 15).</summary>
public static class Messages
{
    public static WireMessage Settings() => WireMessage.Create(MessageType.DisplaySettings);

    public static WireMessage Visibility(uint int2, uint int3) =>
        WireMessage.Create(MessageType.Visibility, default, (2, int2), (3, int3));

    public static WireMessage Ping() => WireMessage.Create(MessageType.Ping);

    public static WireMessage Ack() => WireMessage.Create(MessageType.FlowControlAck);

    public static WireMessage Framebuffer(int width, int height, int fps, ReadOnlyMemory<byte> accessUnit) =>
        WireMessage.Create(MessageType.Framebuffer, accessUnit.Span,
            (2, (uint)width), (3, (uint)height), (4, (uint)width * 4), (5, 21),
            (8, (uint)width), (9, (uint)height), (10, 4), (11, 2), (12, (uint)fps), (13, 4), (15, 1),
            (17, (uint)accessUnit.Length));

    /// <summary>SPEC 13.2: settings, visibility (0,1), visibility (1,1), ping, ping.</summary>
    public static IReadOnlyList<WireMessage> Preamble() =>
        [Settings(), Visibility(0, 1), Visibility(1, 1), Ping(), Ping()];
}
