using System.Collections.Concurrent;
using MonRem.Protocol.V1;

namespace MonRem.Host.Core.Sim;

/// <summary>
/// Simulador falso para desenvolvimento e testes: guarda valores em memória,
/// registra eventos disparados e permite simular troca de aeronave.
/// </summary>
public sealed class FakeSimConnection : ISimConnection
{
    private readonly ConcurrentDictionary<string, SimDatum> _values = new();
    private readonly ConcurrentDictionary<string, List<Action<SimDatum>>> _listeners = new();
    private readonly Lock _lock = new();

    public string Name => "fake";
    public bool IsAvailable => true;
    public SimStatusInfo Status { get; private set; } = new(true, true, false, 0, "fake");
    public AircraftInfo? Aircraft { get; private set; } = new("Fake Aircraft", "", "", "FAKE", "FAKE");
    public ConcurrentQueue<(string Ref, long[] Args)> Fired { get; } = new();

    public event Action<SimStatusInfo>? StatusChanged;
    public event Action<SimSystemEventType>? SystemEvent;
    public event Action<AircraftInfo>? AircraftChanged;

    public bool Supports(SimRef reference, SimOperation operation) =>
        reference.Kind is SimRefKind.A or SimRefKind.L or SimRefKind.B or SimRefKind.K;

    public IDisposable Subscribe(SimRef reference, Action<SimDatum> onValue)
    {
        lock (_lock) _listeners.GetOrAdd(reference.Key, _ => []).Add(onValue);
        if (_values.TryGetValue(reference.Key, out var current)) onValue(current);
        return new Unsubscriber(() =>
        {
            lock (_lock)
            {
                if (_listeners.TryGetValue(reference.Key, out var list)) list.Remove(onValue);
            }
        });
    }

    public Task SetAsync(SimRef reference, SimDatum value, CancellationToken ct)
    {
        Push(reference, value);
        return Task.CompletedTask;
    }

    public Task FireAsync(SimRef reference, IReadOnlyList<long> args, CancellationToken ct)
    {
        Fired.Enqueue((reference.Raw, args.ToArray()));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SimInputEvent>> ListInputEventsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SimInputEvent>>([new SimInputEvent { Name = "FAKE_Battery_1", Type = "double" }]);

    /// <summary>Muda um valor como se viesse do sim.</summary>
    public void Push(string reference, SimDatum value) => Push(SimRef.Parse(reference), value);

    public void Push(SimRef reference, SimDatum value)
    {
        _values[reference.Key] = value;
        Action<SimDatum>[] targets;
        lock (_lock)
            targets = _listeners.TryGetValue(reference.Key, out var list) ? [.. list] : [];
        foreach (var t in targets) t(value);
    }

    public void ChangeAircraft(AircraftInfo aircraft)
    {
        Aircraft = aircraft;
        AircraftChanged?.Invoke(aircraft);
        SystemEvent?.Invoke(SimSystemEventType.SimSystemEventAircraftLoaded);
    }

    public void SetStatus(SimStatusInfo status)
    {
        Status = status;
        StatusChanged?.Invoke(status);
    }

    public int ListenerCount(string reference)
    {
        lock (_lock)
            return _listeners.TryGetValue(SimRef.Parse(reference).Key, out var l) ? l.Count : 0;
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) dispose();
        }
    }
}
