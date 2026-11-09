using System.Threading.Channels;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using MonRem.Host.Core.Sim;
using MonRem.Protocol.V1;

namespace MonRem.Host.Core.Devices;

/// <summary>Estado público de um painel conectado (para a interface e diagnóstico).</summary>
public sealed record DeviceInfo(
    string SessionId, string DeviceId, string Model, string FirmwareVersion, string Link,
    DeviceTransport Transport, string? ProfileName, bool DirectMode, IReadOnlyList<string> Warnings);

/// <summary>
/// Uma conexão com um painel ESP32. Fluxo: DeviceHello → DeviceWelcome → (descritor) → estado completo
/// nos dois sentidos → mudanças. Escrita só por uma tarefa (fila de saída), leitura em outra.
/// </summary>
public sealed class DeviceSession
{
    public const int OutputItemsPerFrame = 16;
    public const int InputItemsPerFrame = 48;
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FullOutputInterval = TimeSpan.FromSeconds(2);

    private readonly IDeviceLink _link;
    private readonly SimRouter _router;
    private readonly SubscriptionHub _hub;
    private readonly DeviceProfileStore _profiles;
    private readonly DescriptorCache _descriptors;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Channel<DeviceFrame> _outbox = Channel.CreateBounded<DeviceFrame>(
        new BoundedChannelOptions(256) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Channel<DeviceInputState> _inputs = Channel.CreateBounded<DeviceInputState>(
        new BoundedChannelOptions(256) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Lock _lock = new();
    private readonly string _sessionId = Guid.NewGuid().ToString("D");

    private DeviceHello _hello = new();
    private IReadOnlyList<Element>? _elements;
    private readonly List<Element> _pendingPages = [];
    private readonly List<byte> _pendingHashBytes = [];
    private uint _pagesSeen;
    private DeviceMapper? _mapper;
    private DeviceProfile? _profile;
    private Dictionary<uint, string> _handleToSource = [];
    private uint _outSeq;

    public DeviceSession(IDeviceLink link, SimRouter router, SubscriptionHub hub, DeviceProfileStore profiles,
        DescriptorCache descriptors, TimeProvider time, ILogger<DeviceSession> logger)
    {
        _link = link;
        _router = router;
        _hub = hub;
        _profiles = profiles;
        _descriptors = descriptors;
        _time = time;
        _logger = logger;
    }

    private string HubKey => "dev:" + _sessionId;

    public DeviceInfo Info
    {
        get
        {
            lock (_lock)
                return new DeviceInfo(_sessionId, _hello.DeviceId, _hello.Model, _hello.FwVersion, _link.Description,
                    _link.Transport, _profile?.Name, _profile?.Direct ?? false, _mapper?.Warnings ?? []);
        }
    }

    /// <summary>Chamado quando o painel se identifica (para o gerenciador registrar).</summary>
    public event Action<DeviceSession>? Identified;

    public async Task RunAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = cts.Token;

        // 1) Hello
        using (var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            helloTimeout.CancelAfter(HelloTimeout);
            DeviceFrame? first;
            try
            {
                first = await _link.ReadAsync(helloTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                _logger.LogDebug("{Link}: nenhum DeviceHello em {Timeout}", _link.Description, HelloTimeout);
                return;
            }
            if (first?.BodyCase != DeviceFrame.BodyOneofCase.Hello) return;
            if (first.Hello.ProtocolVersion != Protocol.ProtocolConstants.ProtocolVersion)
            {
                await _link.WriteAsync(new DeviceFrame { Error = new Error { Code = ErrorCode.ErrorVersionUnsupported, Message = "versão não suportada" } }, token).ConfigureAwait(false);
                return;
            }
            lock (_lock) _hello = first.Hello;
        }

        _logger.LogInformation("Painel {Model} {DeviceId} (fw {Fw}) em {Link}", _hello.Model, _hello.DeviceId, _hello.FwVersion, _link.Description);
        var known = _descriptors.TryGet(_hello.Model, _hello.DescriptorHash, out var cached);
        if (known) SetElements(cached);
        SelectProfile();

        var writer = WriterLoopAsync(token);
        // Falha de escrita (cabo solto, porta fechada) encerra a sessão.
        _ = writer.ContinueWith(_ => cts.Cancel(), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        var processor = InputLoopAsync(token);
        var periodic = PeriodicFullOutputAsync(token);
        _router.Connection.AircraftChanged += OnAircraftChanged;
        Identified?.Invoke(this);

        Send(new DeviceFrame
        {
            Welcome = new DeviceWelcome
            {
                SessionId = _sessionId,
                NeedDescriptor = !known,
                DirectMode = _profile?.Direct ?? false,
                OutputMaxHz = _profile?.OutputMaxHz ?? 30,
            },
        });
        SendFullOutputs();

        try
        {
            await ReadLoopAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
        finally
        {
            _router.Connection.AircraftChanged -= OnAircraftChanged;
            _hub.RemoveClient(HubKey);
            _inputs.Writer.TryComplete();
            await cts.CancelAsync().ConfigureAwait(false);
            await Ignore(writer).ConfigureAwait(false);
            await Ignore(processor).ConfigureAwait(false);
            await Ignore(periodic).ConfigureAwait(false);
            _logger.LogInformation("Painel {Model} {DeviceId} desconectado", _hello.Model, _hello.DeviceId);
        }
    }

    // ------------------------------------------------------------------ leitura

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            DeviceFrame? frame;
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                idle.CancelAfter(IdleTimeout);
                try
                {
                    frame = await _link.ReadAsync(idle.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    _logger.LogInformation("Painel {DeviceId}: 3 s sem dados, considerado offline", _hello.DeviceId);
                    return;
                }
            }
            if (frame is null) return;

            switch (frame.BodyCase)
            {
                case DeviceFrame.BodyOneofCase.IoDescriptor:
                    OnDescriptorPage(frame.IoDescriptor);
                    break;
                case DeviceFrame.BodyOneofCase.InputState:
                    await _inputs.Writer.WriteAsync(frame.InputState, ct).ConfigureAwait(false);
                    break;
                case DeviceFrame.BodyOneofCase.Ping:
                    Send(new DeviceFrame { Pong = new DevicePong { TDeviceMs = frame.Ping.TDeviceMs, THostUs = (ulong)(_time.GetTimestamp() * 1_000_000.0 / _time.TimestampFrequency) } });
                    break;
                case DeviceFrame.BodyOneofCase.Bye:
                    return;

                // Modo direto: a resposta ecoa o seq do pedido.
                case DeviceFrame.BodyOneofCase.SimSubscribe when _profile?.Direct == true:
                    var subscribed = _hub.Subscribe(HubKey, frame.SimSubscribe, OnDirectValues);
                    Send(new DeviceFrame { Seq = frame.Seq, SimSubscribed = subscribed });
                    break;
                case DeviceFrame.BodyOneofCase.SimUnsubscribe when _profile?.Direct == true:
                    _hub.Unsubscribe(HubKey, frame.SimUnsubscribe.SubId);
                    break;
                case DeviceFrame.BodyOneofCase.SimSetVar when _profile?.Direct == true:
                    Send(new DeviceFrame { Seq = frame.Seq, SimAck = await SimCommands.SetAsync(_router, frame.SimSetVar, ct).ConfigureAwait(false) });
                    break;
                case DeviceFrame.BodyOneofCase.SimFireEvent when _profile?.Direct == true:
                    Send(new DeviceFrame { Seq = frame.Seq, SimAck = await SimCommands.FireAsync(_router, frame.SimFireEvent, ct).ConfigureAwait(false) });
                    break;
            }
        }
    }

    private void OnDescriptorPage(DeviceDescriptor page)
    {
        if (page.Pages == 0 || page.Page >= page.Pages) return;
        if (page.Page == 0)
        {
            _pendingPages.Clear();
            _pendingHashBytes.Clear();
            _pagesSeen = 0;
        }
        if (page.Page != _pagesSeen) return; // fora de ordem: espera recomeçar da página 0

        _pendingPages.AddRange(page.Elements);
        _pendingHashBytes.AddRange(page.ToByteArray());
        _pagesSeen++;
        if (_pagesSeen < page.Pages) return;

        var hash = Crc32.Compute([.. _pendingHashBytes]);
        if (hash != _hello.DescriptorHash)
            _logger.LogWarning("Painel {DeviceId}: hash do descritor {Got:x8} ≠ anunciado {Expected:x8}", _hello.DeviceId, hash, _hello.DescriptorHash);

        var elements = _pendingPages.ToList();
        _descriptors.Put(_hello.Model, _hello.DescriptorHash, elements);
        SetElements(elements);
        _logger.LogInformation("Painel {DeviceId}: descritor com {Count} elementos", _hello.DeviceId, elements.Count);
        RebuildMapper();
    }

    // ------------------------------------------------------------------ entradas → sim

    private async Task InputLoopAsync(CancellationToken ct)
    {
        await foreach (var state in _inputs.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            DeviceMapper? mapper;
            lock (_lock) mapper = _mapper;
            if (mapper is null) continue;

            foreach (var action in mapper.OnInput(state.Items, state.TDeviceMs))
            {
                var ack = await SimCommands.ExecuteAsync(_router, action, ct).ConfigureAwait(false);
                if (!ack.Ok)
                    _logger.LogDebug("Painel {DeviceId}: {Ref} falhou: {Code} {Message}", _hello.DeviceId, action.Ref, ack.Error.Code, ack.Error.Message);
            }
        }
    }

    // ------------------------------------------------------------------ sim → saídas

    private void SetElements(IReadOnlyList<Element> elements)
    {
        lock (_lock) _elements = elements;
    }

    private void SelectProfile()
    {
        var profile = _profiles.Select(_hello.Model, _hello.DeviceId, _router.Connection.Aircraft);
        lock (_lock) _profile = profile;
        if (profile is null)
            _logger.LogWarning("Painel {Model} {DeviceId}: nenhum perfil encontrado", _hello.Model, _hello.DeviceId);
        RebuildMapper();
    }

    private void RebuildMapper()
    {
        DeviceProfile? profile;
        IReadOnlyList<Element>? elements;
        lock (_lock)
        {
            profile = _profile;
            elements = _elements;
        }

        _hub.RemoveClient(HubKey);
        if (profile is null || profile.Direct || elements is null)
        {
            lock (_lock)
            {
                _mapper = null;
                _handleToSource = [];
            }
            return;
        }

        var mapper = new DeviceMapper(profile, elements);
        foreach (var w in mapper.Warnings) _logger.LogWarning("Perfil {Profile}: {Warning}", profile.Name, w);

        var request = new SimSubscribe { SubId = 1, MaxHz = profile.OutputMaxHz };
        request.Vars.AddRange(mapper.OutputSources);
        Dictionary<uint, string> handles = [];
        lock (_lock)
        {
            _mapper = mapper;
            _handleToSource = handles;
        }

        if (request.Vars.Count > 0)
        {
            var subscribed = _hub.Subscribe(HubKey, request, OnMappedValues);
            for (var i = 0; i < subscribed.Handles.Count; i++)
            {
                var h = subscribed.Handles[i];
                if (h.Handle != 0) handles[h.Handle] = request.Vars[i].Ref;
                else _logger.LogWarning("Perfil {Profile}: {Ref}: {Message}", profile.Name, request.Vars[i].Ref, h.Error.Message);
            }
        }
        SendFullOutputs();
    }

    private ValueTask OnMappedValues(Envelope envelope)
    {
        List<OutputValue> changed = [];
        lock (_lock)
        {
            if (_mapper is null) return ValueTask.CompletedTask;
            foreach (var v in envelope.SimValues.Items)
            {
                if (!_handleToSource.TryGetValue(v.Handle, out var source)) continue;
                var datum = v.ValueCase == SimValue.ValueOneofCase.Text ? SimDatum.Of(v.Text) : SimDatum.Of(v.Number);
                changed.AddRange(_mapper.OnSourceValue(source, datum));
            }
        }
        SendOutputs(changed, full: false);
        return ValueTask.CompletedTask;
    }

    private ValueTask OnDirectValues(Envelope envelope)
    {
        Send(new DeviceFrame { SimValues = envelope.SimValues });
        return ValueTask.CompletedTask;
    }

    private void SendFullOutputs()
    {
        IReadOnlyList<OutputValue> all;
        lock (_lock)
        {
            if (_mapper is null) return;
            all = _mapper.FullOutputs();
        }
        SendOutputs(all, full: true);
    }

    private void SendOutputs(IReadOnlyList<OutputValue> outputs, bool full)
    {
        if (outputs.Count == 0) return;
        var parts = (outputs.Count + OutputItemsPerFrame - 1) / OutputItemsPerFrame;
        for (var p = 0; p < parts; p++)
        {
            var state = new DeviceOutputState { Full = full, Part = (uint)p, Parts = (uint)parts };
            state.Items.AddRange(outputs.Skip(p * OutputItemsPerFrame).Take(OutputItemsPerFrame));
            Send(new DeviceFrame { OutputState = state });
        }
    }

    private async Task PeriodicFullOutputAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(FullOutputInterval, _time, ct).ConfigureAwait(false);
            SendFullOutputs();
        }
    }

    /// <summary>Perfis recarregados do disco: escolhe de novo e reconstrói o mapeamento.</summary>
    public void ReloadProfile() => OnAircraftChanged(_router.Connection.Aircraft);

    private void OnAircraftChanged(AircraftInfo? aircraft)
    {
        var next = _profiles.Select(_hello.Model, _hello.DeviceId, aircraft);
        DeviceProfile? current;
        lock (_lock) current = _profile;
        if (ReferenceEquals(next, current)) return;

        _logger.LogInformation("Painel {DeviceId}: aeronave {Aircraft} → perfil {Profile}", _hello.DeviceId, aircraft?.Title ?? "?", next?.Name ?? "(nenhum)");
        lock (_lock) _profile = next;
        RebuildMapper();
    }

    // ------------------------------------------------------------------ escrita

    private void Send(DeviceFrame frame) => _outbox.Writer.TryWrite(frame);

    private async Task WriterLoopAsync(CancellationToken ct)
    {
        await foreach (var frame in _outbox.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (frame.Seq == 0) frame.Seq = ++_outSeq;
            await _link.WriteAsync(frame, ct).ConfigureAwait(false);
        }
    }

    private static async Task Ignore(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or ChannelClosedException)
        {
        }
    }
}
