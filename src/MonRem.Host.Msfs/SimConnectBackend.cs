using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.FlightSimulator.SimConnect;
using MonRem.Host.Core.Sim;
using MonRem.Protocol.V1;

namespace MonRem.Host.Msfs;

/// <summary>
/// Backend SimConnect (MSFS 2024). Uma thread dedicada é dona do objeto SimConnect:
/// conecta (e reconecta a cada 2 s quando o sim fecha), recebe mensagens e executa os comandos enfileirados.
/// Cada variável assinada vira uma data definition com período VISUAL_FRAME + flag CHANGED.
/// </summary>
/// <remarks>
/// Pendências conhecidas (próxima iteração): eventos K: com mais de 1 parâmetro (TransmitClientEvent_EX1).
/// </remarks>
public sealed class SimConnectBackend : ISimConnection, IDisposable
{
    private const uint AircraftDefinition = 1;
    private const uint AircraftRequest = 1;
    private const uint FirstDynamicId = 100;

    private readonly ILogger<SimConnectBackend> _logger;
    private readonly Thread _thread;
    private readonly AutoResetEvent _messageSignal = new(false);
    private readonly AutoResetEvent _queueSignal = new(false);
    private readonly ConcurrentQueue<QueuedAction> _queue = new();
    private readonly Lock _lock = new();
    private readonly Dictionary<string, VarRegistration> _vars = [];
    private readonly Dictionary<uint, VarRegistration> _varsByDefinition = [];
    private readonly Dictionary<string, uint> _events = [];

    // Input Events (B:): resolvidos por nome -> hash via EnumerateInputEvents (próprios de cada aeronave).
    private readonly Dictionary<string, InputEventDesc> _inputEventsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, InputEventSub> _inputEventSubsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ulong, InputEventSub> _inputEventSubsByHash = [];
    private readonly List<TaskCompletionSource<IReadOnlyList<SimInputEvent>>> _pendingInputEventLists = [];
    private bool _inputEventsEnumerated;
    private bool _inputEventEnumerateInFlight;
    private uint? _inputEventsRequestId;

    private volatile bool _stopping;
    private uint _nextId = FirstDynamicId;
    private SimConnect? _sim;

    public SimConnectBackend(ILogger<SimConnectBackend> logger)
    {
        _logger = logger;
        _thread = new Thread(Run) { IsBackground = true, Name = "SimConnect" };
        _thread.Start();
    }

    public string Name => "SimConnect";
    public bool IsAvailable => Status.Connected;
    public SimStatusInfo Status { get; private set; } = new(false, false, false, 0, "");
    public AircraftInfo? Aircraft { get; private set; }

    public event Action<SimStatusInfo>? StatusChanged;
    public event Action<SimSystemEventType>? SystemEvent;
    public event Action<AircraftInfo>? AircraftChanged;

    public bool Supports(SimRef reference, SimOperation operation) => (reference.Kind, operation) switch
    {
        (SimRefKind.A or SimRefKind.L, SimOperation.Read or SimOperation.Set) => true,
        (SimRefKind.B, SimOperation.Read or SimOperation.Set or SimOperation.Fire) => true,
        (SimRefKind.K, SimOperation.Fire) => true,
        _ => false,
    };

    public IDisposable Subscribe(SimRef reference, Action<SimDatum> onValue)
    {
        if (reference.Kind == SimRefKind.B) return SubscribeInputEvent(reference, onValue);

        VarRegistration reg;
        bool firstListener;
        lock (_lock)
        {
            if (!_vars.TryGetValue(reference.Key, out reg!))
            {
                reg = new VarRegistration(reference, NextIdLocked());
                _vars[reference.Key] = reg;
                _varsByDefinition[reg.DefinitionId] = reg;
            }
            firstListener = reg.Listeners.Count == 0;
            reg.Listeners.Add(onValue);
        }

        // De 0 para 1 ouvinte: (re)registra pedindo dados, mesmo se a definição já existia só para escrita.
        if (firstListener) Enqueue(sc => Register(sc, reg, request: true));
        else if (reg.Last is { } last) onValue(last);

        return new Unsubscriber(() => Unsubscribe(reg, onValue));
    }

    public Task SetAsync(SimRef reference, SimDatum value, CancellationToken ct)
    {
        if (!IsAvailable) throw new SimException(ErrorCode.ErrorSimNotConnected, "MSFS não conectado");
        if (reference.Kind == SimRefKind.B) return SetInputEventAsync(reference, value, ct);

        VarRegistration reg;
        bool created;
        lock (_lock)
        {
            created = !_vars.TryGetValue(reference.Key, out reg!);
            if (created)
            {
                reg = new VarRegistration(reference, NextIdLocked());
                _vars[reference.Key] = reg;
                _varsByDefinition[reg.DefinitionId] = reg;
            }
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(sc =>
        {
            try
            {
                if (created) Register(sc, reg, request: reg.Listeners.Count > 0);
                object data = reg.IsString ? new String256 { Value = value.Text ?? "" } : value.Number ?? 0;
                sc.SetDataOnSimObject((DynamicId)reg.DefinitionId, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_DATA_SET_FLAG.DEFAULT, data);
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(new SimException(ErrorCode.ErrorInternal, $"SetDataOnSimObject: {ex.Message}"));
            }
        }, ex => tcs.TrySetException(ex));
        return WithTimeout(tcs.Task, ct);
    }

    public Task FireAsync(SimRef reference, IReadOnlyList<long> args, CancellationToken ct)
    {
        if (!IsAvailable) throw new SimException(ErrorCode.ErrorSimNotConnected, "MSFS não conectado");
        if (reference.Kind == SimRefKind.B)
        {
            // Input Event "disparado": equivale a escrever o valor (1 por padrão, como um botão momentâneo).
            var value = args.Count > 0 ? (double)args[0] : 1.0;
            return SetInputEventAsync(reference, SimDatum.Of(value), ct);
        }
        if (args.Count > 1)
            throw new SimException(ErrorCode.ErrorInvalidArgument, "K: com mais de 1 parâmetro ainda não suportado (TransmitClientEvent_EX1)");

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(sc =>
        {
            try
            {
                uint id;
                bool mapped;
                lock (_lock)
                {
                    mapped = _events.TryGetValue(reference.Name, out id);
                    if (!mapped) _events[reference.Name] = id = NextIdLocked();
                }
                if (!mapped) sc.MapClientEventToSimEvent((DynamicId)id, reference.Name);
                var data = args.Count == 1 ? unchecked((uint)args[0]) : 0u;
                sc.TransmitClientEvent(SimConnect.SIMCONNECT_OBJECT_ID_USER, (DynamicId)id, data,
                    (DynamicId)SimConnect.SIMCONNECT_GROUP_PRIORITY_HIGHEST, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(new SimException(ErrorCode.ErrorInternal, $"TransmitClientEvent: {ex.Message}"));
            }
        }, ex => tcs.TrySetException(ex));
        return WithTimeout(tcs.Task, ct);
    }

    public Task<IReadOnlyList<SimInputEvent>> ListInputEventsAsync(CancellationToken ct)
    {
        if (!IsAvailable) throw new SimException(ErrorCode.ErrorSimNotConnected, "MSFS não conectado");
        var tcs = new TaskCompletionSource<IReadOnlyList<SimInputEvent>>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock) _pendingInputEventLists.Add(tcs);
        Enqueue(sc => EnsureInputEventsEnumerated(sc, force: true), ex =>
        {
            lock (_lock) _pendingInputEventLists.Remove(tcs);
            tcs.TrySetException(ex);
        });
        return WithTimeout(tcs.Task, ct);
    }

    public void Dispose()
    {
        _stopping = true;
        _queueSignal.Set();
        // Só libera os handles se a thread terminou; senão ela ainda pode usá-los.
        if (!_thread.Join(TimeSpan.FromSeconds(3))) return;
        _messageSignal.Dispose();
        _queueSignal.Dispose();
    }

    private static async Task WithTimeout(Task task, CancellationToken ct)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new SimException(ErrorCode.ErrorSimNotConnected, "o MSFS não respondeu em 2 s");
        }
    }

    private static async Task<T> WithTimeout<T>(Task<T> task, CancellationToken ct)
    {
        try
        {
            return await task.WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new SimException(ErrorCode.ErrorSimNotConnected, "o MSFS não respondeu em 2 s");
        }
    }

    // ------------------------------------------------------------------ thread do SimConnect

    private void Run()
    {
        while (!_stopping)
        {
            try
            {
                _sim = new SimConnect("MonRem Host", IntPtr.Zero, 0, _messageSignal, 0);
            }
            catch (COMException)
            {
                Thread.Sleep(2000); // sim fechado: tenta de novo
                continue;
            }

            Wire(_sim);
            try
            {
                while (!_stopping && _sim is not null)
                {
                    var signaled = WaitHandle.WaitAny([_messageSignal, _queueSignal], 250);
                    if (signaled == 0) _sim.ReceiveMessage();
                    while (_sim is not null && _queue.TryDequeue(out var item))
                    {
                        try
                        {
                            item.Run(_sim);
                        }
                        catch (COMException)
                        {
                            throw; // conexão perdida: reconecta
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Erro ao executar comando no SimConnect");
                            item.Fail?.Invoke(new SimException(ErrorCode.ErrorInternal, ex.Message));
                        }
                    }
                }
            }
            catch (COMException ex)
            {
                _logger.LogWarning("SimConnect caiu: {Message}", ex.Message);
            }
            catch (Exception ex)
            {
                // Erro em callback não pode matar a thread (e derrubar o host): reconecta.
                _logger.LogError(ex, "Erro inesperado no laço do SimConnect");
            }
            finally
            {
                OnDisconnected();
            }
        }
    }

    private void Wire(SimConnect sc)
    {
        sc.OnRecvOpen += (s, data) =>
        {
            var version = $"{data.dwApplicationVersionMajor}.{data.dwApplicationVersionMinor}.{data.dwApplicationBuildMajor}.{data.dwApplicationBuildMinor}";
            _logger.LogInformation("Conectado ao {App} {Version}", data.szApplicationName, version);
            SetStatus(Status with { Connected = true, SimVersion = version });

            foreach (var (id, name) in new[]
            {
                (SystemEventId.SimStart, "SimStart"), (SystemEventId.SimStop, "SimStop"), (SystemEventId.PauseEx1, "Pause_EX1"),
                (SystemEventId.FlightLoaded, "FlightLoaded"), (SystemEventId.AircraftLoaded, "AircraftLoaded"), (SystemEventId.Crashed, "Crashed"),
            })
                s.SubscribeToSystemEvent(id, name);

            RegisterAircraftDefinition(s);
            RequestAircraft(s);

            // Input Events: um struct double e um de string, por tipo de evento (ver SIMCONNECT_INPUT_EVENT_TYPE).
            s.RegisterStruct<SIMCONNECT_RECV_GET_INPUT_EVENT, double>((Enum)SIMCONNECT_INPUT_EVENT_TYPE.DOUBLE);
            s.RegisterStruct<SIMCONNECT_RECV_GET_INPUT_EVENT, SimConnect.InputEventString>((Enum)SIMCONNECT_INPUT_EVENT_TYPE.STRING);
            s.RegisterStruct<SIMCONNECT_RECV_SUBSCRIBE_INPUT_EVENT, double>((Enum)SIMCONNECT_INPUT_EVENT_TYPE.DOUBLE);
            s.RegisterStruct<SIMCONNECT_RECV_SUBSCRIBE_INPUT_EVENT, SimConnect.InputEventString>((Enum)SIMCONNECT_INPUT_EVENT_TYPE.STRING);

            List<VarRegistration> all;
            lock (_lock)
            {
                _events.Clear();
                all = [.. _vars.Values];
            }
            // Re-registra todas as definições: com pedido de dados as que têm ouvintes, só a definição as de escrita.
            foreach (var reg in all) Register(s, reg, request: reg.Listeners.Count > 0);

            if (HasInputEventWork()) EnsureInputEventsEnumerated(s, force: true);
        };

        sc.OnRecvQuit += (_, _) =>
        {
            _logger.LogInformation("MSFS encerrado");
            _sim = null;
        };

        sc.OnRecvException += (_, data) =>
        {
            var exception = (SIMCONNECT_EXCEPTION)data.dwException;
            // No menu do sim não existe avião do jogador: pedidos de dados dele voltam UNRECOGNIZED_ID.
            // É esperado; os pedidos são refeitos quando o voo ou o avião carrega.
            if (exception == SIMCONNECT_EXCEPTION.UNRECOGNIZED_ID && !Status.InFlight)
            {
                _logger.LogDebug("SimConnect UNRECOGNIZED_ID fora de voo (send {SendId}); será refeito ao carregar o voo", data.dwSendID);
                return;
            }
            _logger.LogWarning("SimConnect exception {Exception} (send {SendId}, parâmetro {Index})", exception, data.dwSendID, data.dwIndex);
        };

        sc.OnRecvSimobjectData += (_, data) =>
        {
            if (data.dwRequestID == AircraftRequest && data.dwDefineID == AircraftDefinition)
            {
                var a = (AircraftStrings)data.dwData[0];
                var info = new AircraftInfo(a.Title, a.LiveryName, a.LiveryFolder, a.AtcModel, a.AtcType);
                Aircraft = info;
                AircraftChanged?.Invoke(info);
                return;
            }

            VarRegistration? reg;
            lock (_lock) _varsByDefinition.TryGetValue(data.dwDefineID, out reg);
            if (reg is null) return;

            var datum = reg.IsString ? SimDatum.Of(((String256)data.dwData[0]).Value) : SimDatum.Of((double)data.dwData[0]);
            Action<SimDatum>[] listeners;
            lock (_lock)
            {
                reg.Last = datum;
                listeners = [.. reg.Listeners];
            }
            foreach (var l in listeners) l(datum);
        };

        sc.OnRecvEnumerateInputEvents += (s, data) =>
        {
            List<TaskCompletionSource<IReadOnlyList<SimInputEvent>>> pendingLists;
            List<InputEventSub> toSubscribe = [];
            List<SimInputEvent> list = [];
            lock (_lock)
            {
                _inputEventsByName.Clear();
                foreach (var o in data.rgData)
                {
                    var d = (SIMCONNECT_INPUT_EVENT_DESCRIPTOR)o;
                    _inputEventsByName[d.Name] = new InputEventDesc(d.Hash, d.eType);
                    list.Add(new SimInputEvent { Name = d.Name, Type = d.eType == SIMCONNECT_INPUT_EVENT_TYPE.STRING ? "string" : "double" });
                }
                _inputEventsEnumerated = true;
                _inputEventEnumerateInFlight = false;

                foreach (var sub in _inputEventSubsByName.Values)
                {
                    if (sub.Hash is not null) continue;
                    if (!_inputEventsByName.TryGetValue(sub.Name, out var desc))
                    {
                        _logger.LogDebug("B:{Name} não existe na aeronave atual", sub.Name);
                        continue;
                    }
                    sub.Hash = desc.Hash;
                    sub.Type = desc.Type;
                    _inputEventSubsByHash[desc.Hash] = sub;
                    if (sub.Listeners.Count > 0) toSubscribe.Add(sub);
                }

                pendingLists = [.. _pendingInputEventLists];
                _pendingInputEventLists.Clear();
            }

            foreach (var sub in toSubscribe) s.SubscribeInputEvent(sub.Hash!.Value);
            foreach (var p in pendingLists) p.TrySetResult(list);
        };

        sc.OnRecvSubscribeInputEvent += (_, data) =>
        {
            InputEventSub? sub;
            lock (_lock) _inputEventSubsByHash.TryGetValue(data.Hash, out sub);
            if (sub is null) return;

            var datum = data.eType == SIMCONNECT_INPUT_EVENT_TYPE.STRING
                ? SimDatum.Of(((SimConnect.InputEventString)data.Value[0]).value)
                : SimDatum.Of((double)data.Value[0]);
            Action<SimDatum>[] listeners;
            lock (_lock)
            {
                sub.Last = datum;
                listeners = [.. sub.Listeners];
            }
            foreach (var l in listeners) l(datum);
        };

        sc.OnRecvEvent += (s, data) =>
        {
            switch ((SystemEventId)data.uEventID)
            {
                case SystemEventId.SimStart:
                    SetStatus(Status with { InFlight = true });
                    SystemEvent?.Invoke(SimSystemEventType.SimSystemEventSimStart);
                    RequestAircraft(s);
                    RequestAllData(s);
                    ReenumerateInputEvents(s);
                    break;
                case SystemEventId.SimStop:
                    SetStatus(Status with { InFlight = false });
                    SystemEvent?.Invoke(SimSystemEventType.SimSystemEventSimStop);
                    break;
                case SystemEventId.PauseEx1:
                    var paused = data.dwData != 0;
                    SetStatus(Status with { Paused = paused, PauseFlags = data.dwData });
                    SystemEvent?.Invoke(paused ? SimSystemEventType.SimSystemEventPause : SimSystemEventType.SimSystemEventUnpause);
                    break;
                case SystemEventId.Crashed:
                    SystemEvent?.Invoke(SimSystemEventType.SimSystemEventCrashed);
                    break;
            }
        };

        sc.OnRecvEventFilename += (s, data) =>
        {
            switch ((SystemEventId)data.uEventID)
            {
                case SystemEventId.FlightLoaded:
                    SetStatus(Status with { InFlight = true });
                    SystemEvent?.Invoke(SimSystemEventType.SimSystemEventFlightLoaded);
                    break;
                case SystemEventId.AircraftLoaded:
                    SystemEvent?.Invoke(SimSystemEventType.SimSystemEventAircraftLoaded);
                    break;
            }
            RequestAircraft(s);
            RequestAllData(s);
            ReenumerateInputEvents(s);
        };
    }

    /// <summary>Limpa o cache de Input Events (são por aeronave) e pede uma nova enumeração.</summary>
    private void ReenumerateInputEvents(SimConnect sc)
    {
        lock (_lock)
        {
            _inputEventsByName.Clear();
            _inputEventsEnumerated = false;
            foreach (var sub in _inputEventSubsByName.Values) sub.Hash = null;
            _inputEventSubsByHash.Clear();
        }
        if (HasInputEventWork()) EnsureInputEventsEnumerated(sc, force: true);
    }

    private bool HasInputEventWork()
    {
        lock (_lock) return _inputEventSubsByName.Values.Any(s => s.Listeners.Count > 0) || _pendingInputEventLists.Count > 0;
    }

    /// <summary>
    /// Refaz os pedidos de dados de todas as variáveis com ouvintes. Pedidos feitos no menu (sem avião)
    /// são recusados pelo sim e não voltam sozinhos; repetir o mesmo request id só substitui o anterior.
    /// </summary>
    private void RequestAllData(SimConnect sc)
    {
        List<VarRegistration> active;
        lock (_lock) active = [.. _vars.Values.Where(v => v.Listeners.Count > 0)];
        foreach (var reg in active)
        {
            var defId = (DynamicId)reg.DefinitionId;
            sc.RequestDataOnSimObject(defId, defId, SimConnect.SIMCONNECT_OBJECT_ID_USER,
                SIMCONNECT_PERIOD.VISUAL_FRAME, SIMCONNECT_DATA_REQUEST_FLAG.CHANGED, 0, 0, 0);
        }
    }

    private void Register(SimConnect sc, VarRegistration reg, bool request)
    {
        var r = reg.Reference;
        var name = r.Kind == SimRefKind.L ? $"L:{r.Name}" : r.SimConnectName;
        var defId = (DynamicId)reg.DefinitionId;

        sc.ClearDataDefinition(defId);
        if (reg.IsString)
        {
            sc.AddToDataDefinition(defId, name, null, SIMCONNECT_DATATYPE.STRING256, 0f, SimConnect.SIMCONNECT_UNUSED);
            sc.RegisterDataDefineStruct<String256>(defId);
        }
        else
        {
            sc.AddToDataDefinition(defId, name, r.Unit ?? "number", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
            sc.RegisterDataDefineStruct<double>(defId);
        }

        if (request)
        {
            sc.RequestDataOnSimObject(defId, defId, SimConnect.SIMCONNECT_OBJECT_ID_USER,
                SIMCONNECT_PERIOD.VISUAL_FRAME, SIMCONNECT_DATA_REQUEST_FLAG.CHANGED, 0, 0, 0);
        }
    }

    private void Unsubscribe(VarRegistration reg, Action<SimDatum> listener)
    {
        bool last;
        lock (_lock)
        {
            reg.Listeners.Remove(listener);
            last = reg.Listeners.Count == 0;
            if (last)
            {
                _vars.Remove(reg.Reference.Key);
                _varsByDefinition.Remove(reg.DefinitionId);
            }
        }
        if (!last) return;
        Enqueue(sc =>
        {
            var defId = (DynamicId)reg.DefinitionId;
            sc.RequestDataOnSimObject(defId, defId, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.NEVER,
                SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
            sc.ClearDataDefinition(defId);
        });
    }

    // ------------------------------------------------------------------ Input Events (B:)

    private IDisposable SubscribeInputEvent(SimRef reference, Action<SimDatum> onValue)
    {
        InputEventSub sub;
        bool firstListener;
        ulong? hash;
        lock (_lock)
        {
            if (!_inputEventSubsByName.TryGetValue(reference.Name, out sub!))
            {
                sub = new InputEventSub(reference.Name);
                _inputEventSubsByName[reference.Name] = sub;
                if (_inputEventsByName.TryGetValue(reference.Name, out var desc))
                {
                    sub.Hash = desc.Hash;
                    sub.Type = desc.Type;
                    _inputEventSubsByHash[desc.Hash] = sub;
                }
            }
            firstListener = sub.Listeners.Count == 0;
            sub.Listeners.Add(onValue);
            hash = sub.Hash;
        }

        if (firstListener)
        {
            if (hash is { } h) Enqueue(sc => sc.SubscribeInputEvent(h));
            else Enqueue(sc => EnsureInputEventsEnumerated(sc, force: false));
        }
        else if (sub.Last is { } last) onValue(last);

        return new Unsubscriber(() => UnsubscribeInputEvent(sub, onValue));
    }

    private void UnsubscribeInputEvent(InputEventSub sub, Action<SimDatum> listener)
    {
        bool last;
        ulong? hash;
        lock (_lock)
        {
            sub.Listeners.Remove(listener);
            last = sub.Listeners.Count == 0;
            hash = sub.Hash;
            if (last)
            {
                _inputEventSubsByName.Remove(sub.Name);
                if (hash is { } h) _inputEventSubsByHash.Remove(h);
            }
        }
        if (!last || hash is not { } unsubHash) return;
        Enqueue(sc => sc.UnsubscribeInputEvent(unsubHash));
    }

    private Task SetInputEventAsync(SimRef reference, SimDatum value, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(sc => SetInputEventQueued(sc, reference, value, tcs, retried: false), ex => tcs.TrySetException(ex));
        return WithTimeout(tcs.Task, ct);
    }

    private void SetInputEventQueued(SimConnect sc, SimRef reference, SimDatum value, TaskCompletionSource tcs, bool retried)
    {
        bool found;
        InputEventDesc desc;
        lock (_lock) found = _inputEventsByName.TryGetValue(reference.Name, out desc);
        if (!found)
        {
            if (retried)
            {
                tcs.TrySetException(new SimException(ErrorCode.ErrorInvalidArgument, $"B:{reference.Name} não existe na aeronave atual"));
                return;
            }
            // Ainda não enumerado (ou aeronave trocou): enumera e tenta de novo depois.
            EnsureInputEventsEnumerated(sc, force: false);
            Enqueue(s => SetInputEventQueued(s, reference, value, tcs, retried: true), ex => tcs.TrySetException(ex));
            return;
        }

        try
        {
            object boxed = desc.Type == SIMCONNECT_INPUT_EVENT_TYPE.STRING
                ? new SimConnect.InputEventString { value = value.Text ?? value.Number?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "" }
                : value.Number ?? 0;
            sc.SetInputEvent(desc.Hash, boxed);
            tcs.TrySetResult();
        }
        catch (Exception ex)
        {
            tcs.TrySetException(new SimException(ErrorCode.ErrorInternal, $"SetInputEvent: {ex.Message}"));
        }
    }

    /// <summary>
    /// Pede a lista de Input Events da aeronave atual (uma vez por aeronave, ou de novo se <paramref name="force"/>).
    /// A resposta chega em OnRecvEnumerateInputEvents e resolve nomes pendentes (assinaturas e Set/Fire enfileirados).
    /// </summary>
    private void EnsureInputEventsEnumerated(SimConnect sc, bool force)
    {
        lock (_lock)
        {
            if (_inputEventEnumerateInFlight) return;
            if (_inputEventsEnumerated && !force) return;
            _inputEventEnumerateInFlight = true;
            _inputEventsRequestId ??= NextIdLocked();
        }
        sc.EnumerateInputEvents((DynamicId)_inputEventsRequestId!.Value);
    }

    private static void RegisterAircraftDefinition(SimConnect sc)
    {
        var def = (DynamicId)AircraftDefinition;
        foreach (var field in new[] { "TITLE", "LIVERY NAME", "LIVERY FOLDER", "ATC MODEL", "ATC TYPE" })
            sc.AddToDataDefinition(def, field, null, SIMCONNECT_DATATYPE.STRING256, 0f, SimConnect.SIMCONNECT_UNUSED);
        sc.RegisterDataDefineStruct<AircraftStrings>(def);
    }

    private static void RequestAircraft(SimConnect sc) =>
        sc.RequestDataOnSimObject((DynamicId)AircraftRequest, (DynamicId)AircraftDefinition, SimConnect.SIMCONNECT_OBJECT_ID_USER,
            SIMCONNECT_PERIOD.ONCE, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);

    private void OnDisconnected()
    {
        try { _sim?.Dispose(); } catch (Exception) { }
        _sim = null;

        // Comandos enfileirados com o sim fora NÃO podem rodar na próxima conexão
        // (ex.: um evento de trem de pouso minutos depois). Falha todos agora.
        var notConnected = new SimException(ErrorCode.ErrorSimNotConnected, "MSFS desconectado");
        while (_queue.TryDequeue(out var item)) item.Fail?.Invoke(notConnected);

        List<TaskCompletionSource<IReadOnlyList<SimInputEvent>>> pendingLists;
        lock (_lock)
        {
            // Input Events são por aeronave: reenumera do zero na próxima conexão.
            _inputEventsByName.Clear();
            _inputEventsEnumerated = false;
            _inputEventEnumerateInFlight = false;
            foreach (var sub in _inputEventSubsByName.Values) sub.Hash = null;
            _inputEventSubsByHash.Clear();
            pendingLists = [.. _pendingInputEventLists];
            _pendingInputEventLists.Clear();
        }
        foreach (var p in pendingLists) p.TrySetException(notConnected);

        if (Status.Connected) SetStatus(new SimStatusInfo(false, false, false, 0, Status.SimVersion));
    }

    private void SetStatus(SimStatusInfo status)
    {
        Status = status;
        StatusChanged?.Invoke(status);
    }

    private void Enqueue(Action<SimConnect> action, Action<Exception>? fail = null)
    {
        _queue.Enqueue(new QueuedAction(action, fail));
        _queueSignal.Set();
    }

    private sealed record QueuedAction(Action<SimConnect> Run, Action<Exception>? Fail);

    private uint NextIdLocked() => _nextId++;

    private enum DynamicId : uint
    {
    }

    private enum SystemEventId : uint
    {
        SimStart = 1,
        SimStop,
        PauseEx1,
        FlightLoaded,
        AircraftLoaded,
        Crashed,
    }

    private sealed class VarRegistration(SimRef reference, uint definitionId)
    {
        public SimRef Reference { get; } = reference;
        public uint DefinitionId { get; } = definitionId;
        public bool IsString { get; } = string.Equals(reference.Unit, "string", StringComparison.OrdinalIgnoreCase);
        public List<Action<SimDatum>> Listeners { get; } = [];
        public SimDatum? Last { get; set; }
    }

    /// <summary>Um Input Event conhecido da aeronave atual (vindo de EnumerateInputEvents).</summary>
    private readonly record struct InputEventDesc(ulong Hash, SIMCONNECT_INPUT_EVENT_TYPE Type);

    /// <summary>Assinatura de um B: por nome; o hash só é conhecido depois da enumeração.</summary>
    private sealed class InputEventSub(string name)
    {
        public string Name { get; } = name;
        public ulong? Hash { get; set; }
        public SIMCONNECT_INPUT_EVENT_TYPE? Type { get; set; }
        public List<Action<SimDatum>> Listeners { get; } = [];
        public SimDatum? Last { get; set; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    private struct String256
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Value;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    private struct AircraftStrings
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Title;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string LiveryName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string LiveryFolder;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AtcModel;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AtcType;
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
