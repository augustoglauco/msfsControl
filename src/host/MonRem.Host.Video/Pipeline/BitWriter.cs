namespace MonRem.Host.Video.Pipeline;

/// <summary>Escritor de bits (big-endian) com Exp-Golomb, para montar NALs H.264.</summary>
internal sealed class BitWriter
{
    private readonly List<byte> _bytes = [];
    private int _current;
    private int _bits;

    public void U(int count, uint value)
    {
        for (var i = count - 1; i >= 0; i--)
        {
            _current = (_current << 1) | (int)((value >> i) & 1);
            if (++_bits == 8)
            {
                _bytes.Add((byte)_current);
                _current = 0;
                _bits = 0;
            }
        }
    }

    public void Ue(uint value)
    {
        value++;
        var length = 32 - System.Numerics.BitOperations.LeadingZeroCount(value);
        U(length - 1, 0);
        U(length, value);
    }

    public void Se(int value) => Ue(value > 0 ? (uint)(2 * value - 1) : (uint)(-2 * value));

    /// <summary>Bit de parada 1 e zeros até o byte.</summary>
    public void Trail()
    {
        U(1, 1);
        while (_bits != 0) U(1, 0);
    }

    public byte[] ToArray() => [.. _bytes];
}
