namespace MonRem.Tests;

/// <summary>Relógio controlado pelo teste (só GetUtcNow/GetTimestamp; não cria timers).</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public ManualTimeProvider() : this(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))
    {
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public override long GetTimestamp() => _now.UtcTicks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void Advance(TimeSpan by) => _now += by;
}
