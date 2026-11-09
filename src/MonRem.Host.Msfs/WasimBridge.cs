using MonRem.Host.Core.Sim;
#if WASIM
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using MonRem.Protocol.V1;
using WASimCommander.CLI.Client;
using WASimCommander.CLI.Enums;
using WASimCommander.CLI.Structs;
#endif

namespace MonRem.Host.Msfs;

#if WASIM
/// <summary>
/// Ponte WASM via WASimCommander (cliente .NET LGPL, módulo do sim GPL — ambos sem modificação).
/// H: e RPN para disparo; RPN para leitura com assinatura no servidor WASM.
/// Reconecta a cada 2 s quando o sim ou o módulo não estão disponíveis.
/// </summary>
public sealed class WasimBridge : IWasmBridge, IDisposable
{
    private const uint ClientId = 0x4D4F4E52; // "MONR"

    private readonly ILogger<WasimBridge> _logger;
    private readonly WASimClient _client = new(ClientId);
    private readonly ConcurrentDictionary<uint, Action<SimDatum>> _requests = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _connectLoop;
    private int _nextRequestId;

    public WasimBridge(ILogger<WasimBridge> logger)
    {
        _logger = logger;
        _client.OnDataReceived += OnData;
        _connectLoop = Task.Run(() => ConnectLoopAsync(_cts.Token));
    }

    public static bool IsInstalled() => true;

    public string Name => "WASimCommander";
    public bool IsAvailable => _client.isConnected();

    public bool Supports(SimRef reference, SimOperation operation) => (reference.Kind, operation) switch
    {
        (SimRefKind.H, SimOperation.Fire) => true,
        (SimRefKind.Rpn, SimOperation.Fire or SimOperation.Read) => true,
        _ => false,
    };

    public IDisposable Subscribe(SimRef reference, Action<SimDatum> onValue)
    {
        var id = (uint)Interlocked.Increment(ref _nextRequestId);
        _requests[id] = onValue;
        // Tick + epsilon 0: o servidor só envia quando o valor muda.
        var hr = _client.saveDataRequestAsync(new DataRequest(id, CalcResultType.Double, reference.Name, UpdatePeriod.Tick, 0, 0f));
        if (hr != HR.OK) _logger.LogWarning("saveDataRequest({Code}) = {Hr}", reference.Name, hr);
        return new Unsubscriber(() =>
        {
            _requests.TryRemove(id, out _);
            _client.removeDataRequest(id);
        });
    }

    public Task SetAsync(SimRef reference, SimDatum value, CancellationToken ct) =>
        throw new SimException(ErrorCode.ErrorInvalidArgument, "escrita via WASM: use RPN, ex. \"RPN:1 (>L:X)\"");

    public Task FireAsync(SimRef reference, IReadOnlyList<long> args, CancellationToken ct)
    {
        if (!IsAvailable) throw new SimException(ErrorCode.ErrorWasmBridgeMissing, "módulo WASim desconectado");
        var code = reference.Kind == SimRefKind.H ? $"(>H:{reference.Name})" : reference.Name;
        var hr = _client.executeCalculatorCode(code);
        if (hr != HR.OK) throw new SimException(ErrorCode.ErrorInternal, $"executeCalculatorCode: {hr}");
        return Task.CompletedTask;
    }

    private void OnData(DataRequestRecord record)
    {
        if (_requests.TryGetValue(record.requestId, out var listener))
            listener(SimDatum.Of((double)record));
    }

    private async Task ConnectLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!_client.isConnected())
            {
                if (_client.connectSimulator() == HR.OK && _client.connectServer() == HR.OK)
                {
                    _logger.LogInformation("WASimCommander conectado");
                    // Assinaturas feitas antes da conexão: o cliente guarda e reenvia ao conectar.
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _connectLoop.Wait(TimeSpan.FromSeconds(3)); } catch (AggregateException) { }
        _client.Dispose();
        _cts.Dispose();
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) dispose();
        }
    }
}
#else
/// <summary>Compilado sem o WASimCommander em lib/WASimCommander: sem ponte WASM.</summary>
public sealed class WasimBridge : IWasmBridge
{
    public static bool IsInstalled() => false;

    public string Name => "WASimCommander (ausente)";
    public bool IsAvailable => false;
    public bool Supports(SimRef reference, SimOperation operation) => false;
    public IDisposable Subscribe(SimRef reference, Action<SimDatum> onValue) => throw new NotSupportedException();
    public Task SetAsync(SimRef reference, SimDatum value, CancellationToken ct) => throw new NotSupportedException();
    public Task FireAsync(SimRef reference, IReadOnlyList<long> args, CancellationToken ct) => throw new NotSupportedException();
}
#endif
