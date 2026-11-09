using MonRem.Protocol.V1;

namespace MonRem.Host.Core.Sim;

/// <summary>
/// Escolhe o backend de cada referência: SimConnect para A/L/K/B, ponte WASM para H/RPN.
/// </summary>
public sealed class SimRouter(ISimConnection connection, IWasmBridge? wasm = null)
{
    public ISimConnection Connection { get; } = connection;
    public IWasmBridge? Wasm { get; } = wasm;

    public ISimBackend Resolve(SimRef reference, SimOperation operation)
    {
        var allowed = operation switch
        {
            SimOperation.Read => reference.IsReadable,
            SimOperation.Set => reference.IsSettable,
            SimOperation.Fire => reference.IsFireable,
            _ => false,
        };
        if (!allowed)
            throw new SimException(ErrorCode.ErrorInvalidArgument, $"{reference.Raw}: operação {operation} não se aplica a {reference.Kind}:");

        var needsWasm = reference.Kind is SimRefKind.H or SimRefKind.Rpn;
        if (needsWasm)
        {
            if (Wasm is null || !Wasm.IsAvailable)
                throw new SimException(ErrorCode.ErrorWasmBridgeMissing, $"{reference.Raw}: módulo WASM indisponível");
            if (!Wasm.Supports(reference, operation))
                throw new SimException(ErrorCode.ErrorInvalidArgument, $"{reference.Raw}: não suportado pela ponte WASM");
            return Wasm;
        }

        if (!Connection.Supports(reference, operation))
            throw new SimException(ErrorCode.ErrorInvalidArgument, $"{reference.Raw}: não suportado pelo SimConnect");
        return Connection;
    }
}
