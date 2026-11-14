using MonRem.Host.Video.Wire;

namespace MonRem.Tests.Video;

public class MessagesTests
{
    [Fact]
    public void Framebuffer_header_matches_oracle()
    {
        var m = Messages.Framebuffer(1560, 720, 60, new byte[100]);

        uint[] expected = new uint[32];
        expected[0] = 2; expected[1] = 100; expected[2] = 1560; expected[3] = 720; expected[4] = 6240;
        expected[5] = 21; expected[8] = 1560; expected[9] = 720; expected[10] = 4; expected[11] = 2;
        expected[12] = 60; expected[13] = 4; expected[15] = 1; expected[17] = 100;
        Assert.Equal(expected, m.Ints);
        Assert.Equal(100, m.Body.Length);
    }

    [Fact]
    public void Preamble_order()
    {
        var p = Messages.Preamble();

        Assert.Equal(
            [MessageType.DisplaySettings, MessageType.Visibility, MessageType.Visibility, MessageType.Ping, MessageType.Ping],
            p.Select(m => m.Type));
        Assert.Equal((0u, 1u), (p[1].Ints[2], p[1].Ints[3]));
        Assert.Equal((1u, 1u), (p[2].Ints[2], p[2].Ints[3]));
        Assert.All(p.Where(m => m.Type is MessageType.DisplaySettings or MessageType.Ping),
            m => Assert.All(m.Ints.Skip(1), v => Assert.Equal(0u, v)));
    }

    [Fact]
    public void Ack_is_header_only()
    {
        var ack = Messages.Ack();
        Assert.Equal(MessageType.FlowControlAck, ack.Type);
        Assert.Equal(0u, ack.Ints[1]);
        Assert.Empty(ack.Body);
    }

    [Fact]
    public void Identification_roundtrip()
    {
        var m = Identification.Create(1560, 720, 60, "{AAAAAAAA-0000-0000-0000-000000000001}", "SM-S928B");

        Assert.Equal(334u, m.Ints[1]);
        Assert.True(Identification.TryParse(m, out var id));
        Assert.Equal(new Identification(1560, 720, 60, "{AAAAAAAA-0000-0000-0000-000000000001}", "SM-S928B"), id);
    }

    [Fact]
    public void TryParse_rejects_wrong_body_size()
    {
        Assert.False(Identification.TryParse(WireMessage.Create(MessageType.Identification, new byte[10]), out _));
    }

    [Fact]
    public void TryParse_rejects_wrong_type()
    {
        Assert.False(Identification.TryParse(WireMessage.Create(MessageType.Ping, new byte[334]), out _));
    }
}
