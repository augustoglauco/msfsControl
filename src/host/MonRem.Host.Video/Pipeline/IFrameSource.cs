namespace MonRem.Host.Video.Pipeline;

/// <summary>Origem dos quadros de um monitor (sintética, captura real ou driver; DESIGN 5.4).</summary>
public interface IFrameSource : IAsyncDisposable
{
    int Width { get; }
    int Height { get; }

    /// <summary>Próximo quadro; <c>null</c> se a origem terminou. Lança <see cref="OperationCanceledException"/> ao cancelar.</summary>
    ValueTask<Frame?> WaitForFrameAsync(CancellationToken ct);
}
