namespace MonRem.Host.Video.Pipeline;

/// <summary>Fundo escuro com uma barra clara que cruza a tela em 4 s e um marcador de 16x16 no canto superior esquerdo.</summary>
public sealed class SyntheticFrameSource : IFrameSource
{
    private const int SweepSeconds = 4;
    private readonly int _fps;
    private readonly PeriodicTimer _timer;
    private long _index;

    public SyntheticFrameSource(int width, int height, int fps)
    {
        Width = width;
        Height = height;
        _fps = fps;
        _timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / fps));
    }

    public int Width { get; }
    public int Height { get; }

    public async ValueTask<Frame?> WaitForFrameAsync(CancellationToken ct)
    {
        await _timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
        return Render(_index++);
    }

    internal Frame Render(long index)
    {
        var stride = Width * 4;
        var buffer = new byte[stride * Height];
        var sweep = (long)SweepSeconds * _fps;
        var barX = (int)(index % sweep * Width / sweep);
        var barWidth = Math.Max(1, Width / 20);

        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var bright = (x >= barX && x < barX + barWidth) || (x < 16 && y < 16);
                var value = bright ? (byte)230 : (byte)(20 + y * 40 / Math.Max(1, Height));
                var o = y * stride + x * 4;
                buffer[o] = buffer[o + 1] = buffer[o + 2] = value;
                buffer[o + 3] = 255;
            }
        }
        return new Frame(Width, Height, stride, buffer);
    }

    public ValueTask DisposeAsync()
    {
        _timer.Dispose();
        return ValueTask.CompletedTask;
    }
}
