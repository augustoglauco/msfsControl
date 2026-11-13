using MonRem.Host.Video.Wire;

namespace MonRem.Tests.Video;

public class WireCodecTests
{
    private static byte[] Header(uint type, uint bodySize)
    {
        var h = new byte[WireCodec.HeaderSize];
        BitConverter.TryWriteBytes(h.AsSpan(0, 4), type);
        BitConverter.TryWriteBytes(h.AsSpan(4, 4), bodySize);
        return h;
    }

    [Fact]
    public async Task Roundtrip_preserves_ints_and_body()
    {
        var body = new byte[] { 1, 2, 3, 4, 5 };
        var message = WireMessage.Create(MessageType.Identification, body, (13, 1560u), (22, 720u));
        using var stream = new MemoryStream();

        await WireCodec.WriteAsync(stream, message, CancellationToken.None);
        stream.Position = 0;
        var read = await WireCodec.ReadAsync(stream, 4096, CancellationToken.None);

        Assert.NotNull(read);
        Assert.Equal(MessageType.Identification, read.Value.Type);
        Assert.Equal(5u, read.Value.Ints[1]);
        Assert.Equal(1560u, read.Value.Ints[13]);
        Assert.Equal(720u, read.Value.Ints[22]);
        Assert.Equal(body, read.Value.Body);
    }

    [Fact]
    public async Task Header_is_128_bytes_little_endian()
    {
        using var stream = new MemoryStream();
        await WireCodec.WriteAsync(stream, WireMessage.Create(MessageType.Ping), CancellationToken.None);

        Assert.Equal(128, stream.Length);
        Assert.Equal(new byte[] { 1, 0, 0, 0 }, stream.ToArray()[..4]);
    }

    [Fact]
    public async Task Read_returns_null_on_clean_eof()
    {
        using var stream = new MemoryStream();
        Assert.Null(await WireCodec.ReadAsync(stream, 4096, CancellationToken.None));
    }

    [Fact]
    public async Task Read_rejects_body_larger_than_max()
    {
        // Só o cabeçalho: se o leitor tentasse ler o corpo, falharia com EndOfStream, não InvalidData.
        using var stream = new MemoryStream(Header(10, 0xFFFFFFFF));
        await Assert.ThrowsAsync<InvalidDataException>(
            () => WireCodec.ReadAsync(stream, 4096, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Read_throws_on_truncated_body()
    {
        var bytes = Header(2, 10).Concat(new byte[3]).ToArray();
        using var stream = new MemoryStream(bytes);
        await Assert.ThrowsAsync<EndOfStreamException>(
            () => WireCodec.ReadAsync(stream, 4096, CancellationToken.None).AsTask());
    }
}
