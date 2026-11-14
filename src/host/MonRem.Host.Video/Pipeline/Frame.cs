namespace MonRem.Host.Video.Pipeline;

/// <summary>Um quadro BGRA (32 bits por pixel), linha a linha com <see cref="Stride"/> bytes.</summary>
public readonly record struct Frame(int Width, int Height, int Stride, ReadOnlyMemory<byte> Bgra);
