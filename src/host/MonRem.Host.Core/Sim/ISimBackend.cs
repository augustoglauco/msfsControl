using MonRem.Protocol.V1;

namespace MonRem.Host.Core.Sim;

/// <summary>Valor do sim: número ou texto.</summary>
public readonly record struct SimDatum(double? Number, string? Text)
{
    public static SimDatum Of(double value) => new(value, null);
    public static SimDatum Of(string value) => new(null, value);

    /// <summary>True se a mudança em relação a <paramref name="previous"/> passa do epsilon.</summary>
    public bool DiffersFrom(SimDatum? previous, double epsilon)
    {
        if (previous is not { } p) return true;
        if (Text is not null || p.Text is not null) return !string.Equals(Text, p.Text, StringComparison.Ordinal);
        if (Number is not { } a || p.Number is not { } b) return Number.HasValue != p.Number.HasValue;
        if (double.IsNaN(a) || double.IsNaN(b)) return double.IsNaN(a) != double.IsNaN(b);
        return Math.Abs(a - b) > epsilon || (epsilon == 0 && a != b);
    }
}

public enum SimOperation
{
    Read,
    Set,
    Fire,
}

public sealed record AircraftInfo(string Title, string LiveryName, string LiveryFolder, string AtcModel, string AtcType);

public sealed record SimStatusInfo(bool Connected, bool InFlight, bool Paused, uint PauseFlags, string SimVersion);

/// <summary>Erro ao rotear/executar uma referência, já com o código do protocolo.</summary>
public sealed class SimException(ErrorCode code, string message) : Exception(message)
{
    public ErrorCode Code { get; } = code;
}

/// <summary>
/// Uma fonte de dados do sim (SimConnect, módulo WASM, falso).
/// Assinaturas são por referência; o hub deduplica entre clientes antes de chegar aqui.
/// </summary>
public interface ISimBackend
{
    string Name { get; }

    /// <summary>Conectado e pronto para atender.</summary>
    bool IsAvailable { get; }

    bool Supports(SimRef reference, SimOperation operation);

    /// <summary>Passa a receber o valor; <paramref name="onValue"/> é chamado na mudança (pode vir de outra thread).</summary>
    IDisposable Subscribe(SimRef reference, Action<SimDatum> onValue);

    Task SetAsync(SimRef reference, SimDatum value, CancellationToken ct);

    Task FireAsync(SimRef reference, IReadOnlyList<long> args, CancellationToken ct);
}

/// <summary>Backend principal (SimConnect): também informa estado, eventos e aeronave.</summary>
public interface ISimConnection : ISimBackend
{
    SimStatusInfo Status { get; }
    AircraftInfo? Aircraft { get; }

    event Action<SimStatusInfo>? StatusChanged;
    event Action<SimSystemEventType>? SystemEvent;
    event Action<AircraftInfo>? AircraftChanged;

    /// <summary>Input Events (B:) da aeronave atual, para o editor de painéis.</summary>
    Task<IReadOnlyList<SimInputEvent>> ListInputEventsAsync(CancellationToken ct);
}

/// <summary>Ponte WASM (WASimCommander): H:, RPN e L:vars com escopo.</summary>
public interface IWasmBridge : ISimBackend;
