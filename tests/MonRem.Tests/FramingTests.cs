using System.Buffers.Binary;
using MonRem.Protocol;
using MonRem.Protocol.V1;

namespace MonRem.Tests;

public class FramingTests
{
    [Fact]
    public async Task Roundtrip_several_envelopes()
    {
        var stream = new MemoryStream();
        foreach (var i in Enumerable.Range(1, 3))
            await stream.WriteAsync(EnvelopeFraming.Encode(new Envelope { ReqId = (uint)i, Ping = new Ping { TClientUs = (ulong)i } }));
        stream.Position = 0;

        for (uint i = 1; i <= 3; i++)
        {
            var e = await EnvelopeFraming.ReadAsync(stream, CancellationToken.None);
            Assert.NotNull(e);
            Assert.Equal(i, e.ReqId);
            Assert.Equal(i, e.Ping.TClientUs);
        }
        Assert.Null(await EnvelopeFraming.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task Rejects_oversized_frame()
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, EnvelopeFraming.MaxFrameSize + 1);
        await Assert.ThrowsAsync<ProtocolException>(() => EnvelopeFraming.ReadAsync(new MemoryStream(header), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Rejects_truncated_frame()
    {
        var frame = EnvelopeFraming.Encode(new Envelope { Ping = new Ping { TClientUs = 42 } });
        await Assert.ThrowsAsync<ProtocolException>(() => EnvelopeFraming.ReadAsync(new MemoryStream(frame[..^1]), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Rejects_garbage_payload()
    {
        byte[] frame = [0, 0, 0, 3, 0xFF, 0xFF, 0xFF];
        await Assert.ThrowsAsync<ProtocolException>(() => EnvelopeFraming.ReadAsync(new MemoryStream(frame), CancellationToken.None).AsTask());
    }
}
