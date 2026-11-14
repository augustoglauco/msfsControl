using MonRem.Host.Video.Pipeline;

namespace MonRem.Tests.Video;

public class TestPatternEncoderTests
{
    private static List<byte[]> SplitNals(ReadOnlyMemory<byte> data)
    {
        var bytes = data.ToArray();
        var starts = new List<int>();
        for (var i = 0; i + 3 < bytes.Length; i++)
            if (bytes[i] == 0 && bytes[i + 1] == 0 && bytes[i + 2] == 0 && bytes[i + 3] == 1)
                starts.Add(i);
        var nals = new List<byte[]>();
        for (var i = 0; i < starts.Count; i++)
        {
            var from = starts[i] + 4;
            var to = i + 1 < starts.Count ? starts[i + 1] : bytes.Length;
            nals.Add(bytes[from..to]);
        }
        return nals;
    }

    private static ReadOnlyMemory<byte> EncodeFirstFrame(int width, int height)
    {
        using var encoder = new TestPatternEncoder();
        var frame = new SyntheticFrameSource(width, height, 50).Render(0);
        return encoder.Encode(in frame, forceKeyframe: true);
    }

    [Theory]
    [InlineData(1560, 720)]
    [InlineData(64, 64)]
    public void Encoder_emits_aud_sps_pps_idr_in_order(int width, int height)
    {
        var nals = SplitNals(EncodeFirstFrame(width, height));

        Assert.Equal([9, 7, 8, 5], nals.Select(n => n[0] & 0x1F));
    }

    [Fact]
    public void Sps_header_bytes_for_1560x720()
    {
        var sps = SplitNals(EncodeFirstFrame(1560, 720))[1];

        Assert.Equal(new byte[] { 0x67, 0x4D, 0x40, 0x2A }, sps[..4]);
    }

    [Fact]
    public void Encoder_output_is_small()
    {
        Assert.True(EncodeFirstFrame(1560, 720).Length < 64 * 1024);
    }

    [Fact]
    public void Output_is_byte_identical_to_the_python_oracle()
    {
        // Gerado por tools/video-oracle/ref_server/h264_pcm.py: idr_pattern(64, 48, lambda mx, my: 40 + mx*30 + (my % 2)*20, 0),
        // a mesma construção que o celular já decodificou (SPEC 14).
        const string golden = "00000001091000000001674d402ada11e40000000168ce3c8000000001658884064898a00c98b9317262e4d498b9317262e4dc9c9c9e";
        const int width = 64, height = 48, stride = width * 4;
        var bgra = new byte[stride * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var value = (byte)(40 + x / 16 * 30 + y / 16 % 2 * 20);
                var o = y * stride + x * 4;
                bgra[o] = bgra[o + 1] = bgra[o + 2] = value;
                bgra[o + 3] = 255;
            }
        var frame = new Frame(width, height, stride, bgra);
        using var encoder = new TestPatternEncoder();

        Assert.Equal(golden, Convert.ToHexString(encoder.Encode(in frame, true).Span).ToLowerInvariant());
    }

    [Fact]
    public void Synthetic_source_bar_moves()
    {
        var source = new SyntheticFrameSource(320, 240, 50);

        static int BarColumn(Frame f)
        {
            var row = f.Bgra.Span.Slice(100 * f.Stride, f.Width * 4);
            for (var x = 0; x < f.Width; x++)
                if (row[x * 4] > 200) return x;
            return -1;
        }

        var first = BarColumn(source.Render(0));
        var later = BarColumn(source.Render(50));
        Assert.True(first >= 0);
        Assert.True(later > first);
    }

    [Fact]
    public async Task Source_stops_on_cancellation()
    {
        await using var source = new SyntheticFrameSource(64, 64, 50);
        using var cts = new CancellationTokenSource();
        var first = await source.WaitForFrameAsync(cts.Token);
        Assert.NotNull(first);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.WaitForFrameAsync(cts.Token).AsTask());
    }
}
