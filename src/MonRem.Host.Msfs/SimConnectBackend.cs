using System.Collections.Concurrent;
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
/// Pendências conhecidas (próxima iteração): Input Events (B:) e eventos K: com mais de 1 parâmetro
/// (TransmitClientEvent_EX1). Ambos respondem "não suportado" por enquanto.
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
        (SimRefKind.K, SimOperation.Fire) => true,
        _ => false, // B: (Input Events) ainda não implementado
    };

    public IDisposable Subscribe(SimRef reference, Action<SimDatum> onValue)
    {
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

    public Task<IReadOnlyList<SimInputEvent>> ListInputEventsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SimInputEvent>>([]); // Input Events: próxima iteração

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

            List<VarRegistration> all;
            lock (_lock)
            {
                _events.Clear();
                all = [.. _vars.Values];
            }
            // Re-registra todas as definições: com pedido de dados as que têm ouvintes, só a definição as de escrita.
            foreach (var reg in all) Register(s, reg, request: reg.Listeners.Count > 0);
        };

        sc.OnRecvQuit += (_, _) =>
        {
            _logger.LogInformation("MSFS encerrado");
            _sim = null;
        };

        sc.OnRecvException += (_, data) =>
            _logger.LogWarning("SimConnect exception {Exception} (send {SendId}, parâmetro {Index})",
                (SIMCONNECT_EXCEPTION)data.dwException, data.dwSendID, data.dwIndex);

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

        sc.OnRecvEvent += (s, data) =>
        {
            switch ((SystemEventId)data.uEventID)
            {
                case SystemEventId.SimStart:
                    SetStatus(Status with { InFlight = true });
                    SystemEvent?.Invoke(SimSystemEventType.SimSystemEventSimStart);
                    RequestAircraft(s);
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
        };
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
