namespace MonRem.Host.Video.Pipeline;

/// <summary>
/// Codificador de teste: cada quadro vira um IDR (AUD, SPS, PPS, IDR) com macroblocos I16x16 só com
/// coeficiente DC (perfil Main, nível 4.2, QP 51, deblocking desligado). Luma de cada macroblock = média do
/// quadro de origem; croma neutro. Não é um codificador de verdade: serve para o celular mostrar a imagem
/// enquanto não há encoder H.264 (porte de tools/video-oracle/ref_server/h264_pcm.py, função idr_pattern).
/// </summary>
public sealed class TestPatternEncoder : IVideoEncoder
{
    private const int Qp = 51;
    private const int Step = 14;
    private const int MaxLevel = 8;
    private static readonly byte[] Aud = [0, 0, 0, 1, 0x09, 0x10];
    private int _idrId;

    public ReadOnlyMemory<byte> Encode(in Frame frame, bool forceKeyframe)
    {
        var mbw = (frame.Width + 15) / 16;
        var mbh = (frame.Height + 15) / 16;

        var bits = new BitWriter();
        bits.Ue(0); bits.Ue(7); bits.Ue(0); bits.U(4, 0); bits.Ue((uint)(_idrId++ & 0xFF));
        bits.U(1, 0); bits.U(1, 0);
        bits.Se(Qp - 26); bits.Ue(1); // slice_qp_delta, disable_deblocking_filter_idc = 1

        var reconstructed = new int[mbh, mbw];
        for (var my = 0; my < mbh; my++)
        {
            for (var mx = 0; mx < mbw; mx++)
            {
                var pred = mx > 0 && my > 0 ? (reconstructed[my, mx - 1] + reconstructed[my - 1, mx] + 1) >> 1
                    : my > 0 ? reconstructed[my - 1, mx]
                    : mx > 0 ? reconstructed[my, mx - 1]
                    : 128;
                var level = Math.Clamp((int)Math.Round((AverageLuma(frame, mx, my) - pred) / (double)Step, MidpointRounding.ToEven), -MaxLevel, MaxLevel);
                reconstructed[my, mx] = Math.Clamp(pred + Step * level, 0, 255);
                bits.Ue(3); bits.Ue(0); bits.Se(0);
                WriteDcLevel(bits, level);
            }
        }
        bits.Trail();

        using var output = new MemoryStream();
        output.Write(Aud);
        output.Write(Nal(3, 7, Sps(frame.Width, frame.Height)));
        output.Write(Nal(3, 8, Pps()));
        output.Write(Nal(3, 5, bits.ToArray()));
        return output.ToArray();
    }

    public void Dispose()
    {
    }

    private static double AverageLuma(in Frame frame, int mx, int my)
    {
        var span = frame.Bgra.Span;
        long sum = 0;
        var count = 0;
        var yEnd = Math.Min(frame.Height, my * 16 + 16);
        var xEnd = Math.Min(frame.Width, mx * 16 + 16);
        for (var y = my * 16; y < yEnd; y++)
        {
            for (var x = mx * 16; x < xEnd; x++)
            {
                var o = y * frame.Stride + x * 4;
                sum += (29 * span[o] + 150 * span[o + 1] + 77 * span[o + 2]) >> 8;
                count++;
            }
        }
        return count == 0 ? 128 : (double)sum / count;
    }

    /// <summary>Bloco Intra16x16DCLevel com um único nível DC (CAVLC, nC 0 a 1).</summary>
    private static void WriteDcLevel(BitWriter b, int level)
    {
        if (level == 0) { b.U(1, 1); return; }
        if (Math.Abs(level) == 1)
        {
            b.U(2, 1); b.U(1, level < 0 ? 1u : 0u); b.U(1, 1);
            return;
        }
        b.U(6, 5);
        var code = (level > 0 ? 2 * level - 2 : -2 * level - 1) - 2;
        b.U(code + 1, 1); b.U(1, 1);
    }

    private static byte[] Sps(int width, int height)
    {
        var mbw = (width + 15) / 16;
        var mbh = (height + 15) / 16;
        var b = new BitWriter();
        b.U(8, 77); b.U(8, 0x40); b.U(8, 42);
        b.Ue(0); b.Ue(0); b.Ue(2); b.Ue(1); b.U(1, 0);
        b.Ue((uint)(mbw - 1)); b.Ue((uint)(mbh - 1)); b.U(1, 1); b.U(1, 1);
        var cropRight = (mbw * 16 - width) / 2;
        var cropBottom = (mbh * 16 - height) / 2;
        if (cropRight != 0 || cropBottom != 0)
        {
            b.U(1, 1); b.Ue(0); b.Ue((uint)cropRight); b.Ue(0); b.Ue((uint)cropBottom);
        }
        else
        {
            b.U(1, 0);
        }
        b.U(1, 0);
        b.Trail();
        return b.ToArray();
    }

    private static byte[] Pps()
    {
        var b = new BitWriter();
        b.Ue(0); b.Ue(0); b.U(1, 0); b.U(1, 0); b.Ue(0); b.Ue(0); b.Ue(0); b.U(1, 0); b.U(2, 0);
        b.Se(0); b.Se(0); b.Se(0); b.U(1, 1); b.U(1, 0); b.U(1, 0);
        b.Trail();
        return b.ToArray();
    }

    /// <summary>Start code de 4 bytes, cabeçalho do NAL e RBSP com emulation prevention.</summary>
    private static byte[] Nal(int refIdc, int type, byte[] rbsp)
    {
        var result = new List<byte> { 0, 0, 0, 1, (byte)((refIdc << 5) | type) };
        var zeros = 0;
        foreach (var value in rbsp)
        {
            if (zeros == 2 && value <= 3)
            {
                result.Add(3);
                zeros = 0;
            }
            result.Add(value);
            zeros = value == 0 ? zeros + 1 : 0;
        }
        return [.. result];
    }
}
