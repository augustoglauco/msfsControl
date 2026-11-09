namespace MonRem.Host.Core.Security;

/// <summary>
/// Limita tentativas de pareamento (global, não por cliente: um atacante troca de client_id à vontade).
/// 5 falhas → bloqueio de 60 s, dobrando a cada novo bloqueio, até 1 h. Sucesso zera tudo.
/// </summary>
public sealed class PairingGuard(TimeProvider time)
{
    public const int MaxFailures = 5;
    private static readonly TimeSpan BaseLock = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxLock = TimeSpan.FromHours(1);

    private readonly Lock _lock = new();
    private int _failures;
    private int _lockCount;
    private DateTimeOffset _lockedUntil = DateTimeOffset.MinValue;

    /// <summary>Tempo restante de bloqueio; zero se liberado.</summary>
    public TimeSpan RetryAfter
    {
        get
        {
            lock (_lock)
            {
                var left = _lockedUntil - time.GetUtcNow();
                return left > TimeSpan.Zero ? left : TimeSpan.Zero;
            }
        }
    }

    public void RecordFailure()
    {
        lock (_lock)
        {
            if (++_failures < MaxFailures) return;
            _failures = 0;
            _lockCount++;
            var duration = TimeSpan.FromTicks(Math.Min(MaxLock.Ticks, BaseLock.Ticks << Math.Min(_lockCount - 1, 16)));
            _lockedUntil = time.GetUtcNow() + duration;
        }
    }

    public void RecordSuccess()
    {
        lock (_lock)
        {
            _failures = 0;
            _lockCount = 0;
            _lockedUntil = DateTimeOffset.MinValue;
        }
    }
}
