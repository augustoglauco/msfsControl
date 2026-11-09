using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using MonRem.Host.Core.Security;
using MonRem.Host.Core.Sim;
using MonRem.Protocol;
using MonRem.Protocol.V1;
using MonRem.Security;

namespace MonRem.Host.Core.Session;

/// <summary>
/// Uma conexão de controle. Estados: Unpaired → (pareamento) → AwaitHello → AwaitCapabilities → Ready.
/// </summary>
internal sealed class ClientConnection
{
    private static readonly TimeSpan PairingIdleLimit = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan HelloIdleLimit = TimeSpan.FromSeconds(10);

    private readonly HostContext _ctx;
    private readonly SslStream _ssl;
    private readonly X509Certificate2 _clientCert;
    private readonly string _remote;
    private readonly ILogger _logger;
    private readonly EnvelopeWriter _writer;
    private readonly CancellationTokenSource _cts = new();

    private State _state;
    private PairedClientRecord? _client;
    private PairingAttempt? _pairing;
    private string? _sessionId;
    private byte[]? _sessionSecret;
    private Capabilities? _capabilities;
    private long _lastRxTimestamp;

    public ClientConnection(HostContext ctx, SslStream ssl, X509Certificate2 clientCert, string remote)
    {
        _ctx = ctx;
        _ssl = ssl;
        _clientCert = clientCert;
        _remote = remote;
        _logger = ctx.LoggerFactory.CreateLogger<ClientConnection>();
        _writer = new EnvelopeWriter(ssl);
        _client = ctx.PairedClients.FindByFingerprint(Certificates.Fingerprint(clientCert));
        _state = _client is null ? State.Unpaired : State.AwaitHello;
    }

    /// <summary>Segredo da sessão (base das chaves UDP); null antes do Hello.</summary>
    internal byte[]? SessionSecret => _sessionSecret;

    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var token = linked.Token;
        _lastRxTimestamp = _ctx.Time.GetTimestamp();
        var watchdog = WatchdogAsync(token);

        try
        {
            while (!token.IsCancellationRequested)
            {
                var envelope = await EnvelopeFraming.ReadAsync(_ssl, token).ConfigureAwait(false);
                if (envelope is null) break;
                _lastRxTimestamp = _ctx.Time.GetTimestamp();
                await DispatchAsync(envelope, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // Encerrada por nós (timeout, Bye, erro de pareamento).
        }
        finally
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            try { await watchdog.ConfigureAwait(false); } catch (OperationCanceledException) { }
            Cleanup();
            await _writer.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task DispatchAsync(Envelope e, CancellationToken ct)
    {
        switch (_state)
        {
            case State.Unpaired:
                await HandlePairingAsync(e, ct).ConfigureAwait(false);
                return;
            case State.AwaitHello when e.BodyCase == Envelope.BodyOneofCase.Hello:
                await HandleHelloAsync(e, ct).ConfigureAwait(false);
                return;
            case State.AwaitCapabilities when e.BodyCase == Envelope.BodyOneofCase.Capabilities:
                await HandleCapabilitiesAsync(e, ct).ConfigureAwait(false);
                return;
            case State.Ready:
                await HandleReadyAsync(e, ct).ConfigureAwait(false);
                return;
        }

        if (e.BodyCase == Envelope.BodyOneofCase.Ping)
        {
            await SendAsync(Pong(e), ct).ConfigureAwait(false);
            return;
        }
        await SendErrorAsync(e.ReqId, ErrorCode.ErrorInvalidArgument, $"{e.BodyCase} não esperado no estado {_state}", ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ pareamento

    private async Task HandlePairingAsync(Envelope e, CancellationToken ct)
    {
        switch (e.BodyCase)
        {
            case Envelope.BodyOneofCase.PairStart when _pairing is null:
                await OnPairStartAsync(e, ct).ConfigureAwait(false);
                break;
            case Envelope.BodyOneofCase.PairSpake when _pairing is { Spake: null }:
                await OnPairSpakeAsync(e, ct).ConfigureAwait(false);
                break;
            case Envelope.BodyOneofCase.PairConfirm when _pairing is { Result: not null }:
                await OnPairConfirmAsync(e, ct).ConfigureAwait(false);
                break;
            default:
                await SendErrorAsync(e.ReqId, ErrorCode.ErrorAuthUnpaired, "cliente não pareado", ct).ConfigureAwait(false);
                await CloseAsync().ConfigureAwait(false);
                break;
        }
    }

    private async Task OnPairStartAsync(Envelope e, CancellationToken ct)
    {
        var start = e.PairStart;
        var retry = _ctx.PairingGuard.RetryAfter;
        if (retry > TimeSpan.Zero)
        {
            await SendAsync(Reply(e, new Envelope { PairResult = new PairResult { Ok = false, RetryAfterS = (uint)Math.Ceiling(retry.TotalSeconds) } }), ct).ConfigureAwait(false);
            await CloseAsync().ConfigureAwait(false);
            return;
        }
        if (string.IsNullOrWhiteSpace(start.ClientId) || start.ClientId.Length > 64)
        {
            await SendErrorAsync(e.ReqId, ErrorCode.ErrorInvalidArgument, "client_id inválido", ct).ConfigureAwait(false);
            await CloseAsync().ConfigureAwait(false);
            return;
        }

        var pin = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        _pairing = new PairingAttempt(start.ClientId, start.DeviceName, start.Platform, pin);
        _ctx.PinDisplay.Show(string.IsNullOrWhiteSpace(start.DeviceName) ? start.ClientId : start.DeviceName, pin);
        _logger.LogInformation("Pareamento iniciado por {Client} ({Remote})", start.ClientId, _remote);

        await SendAsync(Reply(e, new Envelope
        {
            PairChallenge = new PairChallenge { HostId = _ctx.HostId, PinDigits = ProtocolConstants.PinDigits },
        }), ct).ConfigureAwait(false);
    }

    private async Task OnPairSpakeAsync(Envelope e, CancellationToken ct)
    {
        var attempt = _pairing!;
        try
        {
            var w = Spake2.DeriveW(attempt.Pin, attempt.ClientId, _ctx.HostId, _ctx.Options.PinKdfIterations);
            var spake = new Spake2(Spake2Role.B, attempt.ClientId, _ctx.HostId, w);
            var aad = Certificates.PairingAad(_ctx.HostCertificate, _clientCert);
            attempt.Result = spake.Finish(e.PairSpake.Share.Span, aad);
            attempt.Spake = spake;
            await SendAsync(Reply(e, new Envelope { PairSpake = new PairSpake { Share = Google.Protobuf.ByteString.CopyFrom(spake.OwnShare) } }), ct).ConfigureAwait(false);
        }
        catch (CryptographicException ex)
        {
            await FailPairingAsync(e, $"SPAKE2 inválido: {ex.Message}", ct).ConfigureAwait(false);
        }
    }

    private async Task OnPairConfirmAsync(Envelope e, CancellationToken ct)
    {
        var attempt = _pairing!;
        if (!attempt.Result!.VerifyPeer(e.PairConfirm.Mac.Span))
        {
            await FailPairingAsync(e, "PIN incorreto", ct).ConfigureAwait(false);
            return;
        }

        // Resposta ao PairConfirm do cliente = a confirmação do host; o PairResult vem em seguida, como notificação.
        await SendAsync(Reply(e, new Envelope { PairConfirm = new PairConfirm { Mac = Google.Protobuf.ByteString.CopyFrom(attempt.Result.OwnConfirmation) } }), ct).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        _client = new PairedClientRecord(attempt.ClientId, attempt.DeviceName, attempt.Platform,
            Certificates.Fingerprint(_clientCert), now, now);
        _ctx.PairedClients.Upsert(_client);
        _ctx.PairingGuard.RecordSuccess();
        _ctx.PinDisplay.Clear();
        _pairing = null;
        _state = State.AwaitHello;
        _lastRxTimestamp = _ctx.Time.GetTimestamp();
        _logger.LogInformation("Cliente {Client} pareado", attempt.ClientId);

        await SendAsync(new Envelope { PairResult = new PairResult { Ok = true, HostId = _ctx.HostId } }, ct).ConfigureAwait(false);
    }

    private async Task FailPairingAsync(Envelope e, string reason, CancellationToken ct)
    {
        _ctx.PairingGuard.RecordFailure();
        _ctx.PinDisplay.Clear();
        _logger.LogWarning("Pareamento falhou ({Remote}): {Reason}", _remote, reason);
        _pairing = null;
        await SendAsync(Reply(e, new Envelope
        {
            PairResult = new PairResult { Ok = false, RetryAfterS = (uint)Math.Ceiling(_ctx.PairingGuard.RetryAfter.TotalSeconds) },
        }), ct).ConfigureAwait(false);
        await CloseAsync().ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ sessão

    private async Task HandleHelloAsync(Envelope e, CancellationToken ct)
    {
        var hello = e.Hello;
        if (hello.ProtocolVersion != ProtocolConstants.ProtocolVersion)
        {
            await SendErrorAsync(e.ReqId, ErrorCode.ErrorVersionUnsupported, $"versão {hello.ProtocolVersion} não suportada (host: {ProtocolConstants.ProtocolVersion})", ct).ConfigureAwait(false);
            await CloseAsync().ConfigureAwait(false);
            return;
        }
        if (hello.ClientId != _client!.ClientId)
        {
            await SendErrorAsync(e.ReqId, ErrorCode.ErrorAuthUnpaired, "client_id não corresponde ao certificado", ct).ConfigureAwait(false);
            await CloseAsync().ConfigureAwait(false);
            return;
        }

        var resumed = _ctx.Sessions.TryResume(hello.ClientId, hello.ResumeToken.Span);
        var (sessionId, resumeToken) = _ctx.Sessions.Open(hello.ClientId);

        using var ecdh = SessionKeys.CreateEphemeral();
        try
        {
            _sessionSecret = SessionKeys.DeriveSessionSecret(ecdh, hello.KeyShare.Span, sessionId);
        }
        catch (CryptographicException ex)
        {
            await SendErrorAsync(e.ReqId, ErrorCode.ErrorInvalidArgument, $"key_share inválido: {ex.Message}", ct).ConfigureAwait(false);
            await CloseAsync().ConfigureAwait(false);
            return;
        }

        _sessionId = sessionId;
        _state = State.AwaitCapabilities;
        _ctx.PairedClients.Touch(hello.ClientId);
        _logger.LogInformation("Sessão {Session} aberta para {Client} (resume: {Resumed})", sessionId, hello.ClientId, resumed);

        await SendAsync(Reply(e, new Envelope
        {
            HelloAck = new HelloAck
            {
                ProtocolVersion = ProtocolConstants.ProtocolVersion,
                HostId = _ctx.HostId,
                SessionId = sessionId,
                ServerTimeUs = NowUs(),
                Resumed = resumed,
                ResumeToken = Google.Protobuf.ByteString.CopyFrom(resumeToken),
                KeyShare = Google.Protobuf.ByteString.CopyFrom(SessionKeys.ExportPublic(ecdh)),
            },
        }), ct).ConfigureAwait(false);
    }

    private async Task HandleCapabilitiesAsync(Envelope e, CancellationToken ct)
    {
        _capabilities = e.Capabilities;
        _state = State.Ready;
        _ctx.Sim.Connection.StatusChanged += OnSimStatus;
        _ctx.Sim.Connection.AircraftChanged += OnAircraft;
        _ctx.Sim.Connection.SystemEvent += OnSystemEvent;

        await SendAsync(Reply(e, new Envelope
        {
            SessionReady = new SessionReady
            {
                UdpPorts = new UdpPorts { Media = (uint)_ctx.Options.MediaPort, Input = (uint)_ctx.Options.InputPort },
                ActiveLayoutId = "",
            },
        }), ct).ConfigureAwait(false);
        await SendAsync(new Envelope { SimStatus = SimStatusMessage() }, ct).ConfigureAwait(false);
        if (_ctx.Sim.Connection.Aircraft is { } aircraft)
            await SendAsync(new Envelope { SimAircraftChanged = ToProto(aircraft) }, ct).ConfigureAwait(false);
    }

    private async Task HandleReadyAsync(Envelope e, CancellationToken ct)
    {
        switch (e.BodyCase)
        {
            case Envelope.BodyOneofCase.Ping:
                await SendAsync(Pong(e), ct).ConfigureAwait(false);
                break;

            case Envelope.BodyOneofCase.Bye:
                _logger.LogInformation("Cliente {Client} saiu: {Reason}", _client?.ClientId, e.Bye.Reason);
                await CloseAsync().ConfigureAwait(false);
                break;

            case Envelope.BodyOneofCase.ListPairedClients:
                var list = new PairedClients();
                list.Items.AddRange(_ctx.PairedClients.List().Select(r => new PairedClient
                {
                    ClientId = r.ClientId,
                    DeviceName = r.DeviceName,
                    Platform = r.Platform,
                    PairedAtUnix = (ulong)r.PairedAt.ToUnixTimeSeconds(),
                    LastSeenUnix = (ulong)r.LastSeen.ToUnixTimeSeconds(),
                }));
                await SendAsync(Reply(e, new Envelope { PairedClients = list }), ct).ConfigureAwait(false);
                break;

            case Envelope.BodyOneofCase.Unpair:
                var target = string.IsNullOrEmpty(e.Unpair.ClientId) ? _client!.ClientId : e.Unpair.ClientId;
                var removed = _ctx.PairedClients.Remove(target);
                if (!removed)
                {
                    await SendErrorAsync(e.ReqId, ErrorCode.ErrorNotFound, $"cliente {target} não está pareado", ct).ConfigureAwait(false);
                    break;
                }
                await SendAsync(Reply(e, new Envelope { SimAck = new SimAck { Ok = true } }), ct).ConfigureAwait(false);
                if (target == _client!.ClientId)
                {
                    await SendAsync(new Envelope { Bye = new Bye { Reason = ByeReason.Unpaired } }, ct).ConfigureAwait(false);
                    await CloseAsync().ConfigureAwait(false);
                }
                break;

            case Envelope.BodyOneofCase.GetSimStatus:
                await SendAsync(Reply(e, new Envelope { SimStatus = SimStatusMessage() }), ct).ConfigureAwait(false);
                break;

            case Envelope.BodyOneofCase.SimSubscribe:
                var subscribed = _ctx.Subscriptions.Subscribe(_sessionId!, e.SimSubscribe, env => SendAsync(env, _cts.Token));
                await SendAsync(Reply(e, new Envelope { SimSubscribed = subscribed }), ct).ConfigureAwait(false);
                break;

            case Envelope.BodyOneofCase.SimUnsubscribe:
                var ok = _ctx.Subscriptions.Unsubscribe(_sessionId!, e.SimUnsubscribe.SubId);
                if (e.ReqId != 0)
                    await SendAsync(Reply(e, new Envelope { SimAck = ok ? new SimAck { Ok = true } : Nack(ErrorCode.ErrorNotFound, "assinatura inexistente") }), ct).ConfigureAwait(false);
                break;

            case Envelope.BodyOneofCase.SimSetVar:
                await SendAsync(Reply(e, new Envelope { SimAck = await SetVarAsync(e.SimSetVar, ct).ConfigureAwait(false) }), ct).ConfigureAwait(false);
                break;

            case Envelope.BodyOneofCase.SimFireEvent:
                await SendAsync(Reply(e, new Envelope { SimAck = await FireAsync(e.SimFireEvent, ct).ConfigureAwait(false) }), ct).ConfigureAwait(false);
                break;

            case Envelope.BodyOneofCase.SimListInputEvents:
                var events = await _ctx.Sim.Connection.ListInputEventsAsync(ct).ConfigureAwait(false);
                var eventList = new SimInputEventList();
                eventList.Items.AddRange(events);
                await SendAsync(Reply(e, new Envelope { SimInputEventList = eventList }), ct).ConfigureAwait(false);
                break;

            case Envelope.BodyOneofCase.StreamStats:
                break; // telemetria do vídeo: ainda sem pipeline nesta versão

            default:
                await SendErrorAsync(e.ReqId, ErrorCode.ErrorInternal, $"{e.BodyCase} ainda não implementado no host", ct).ConfigureAwait(false);
                break;
        }
    }

    private async Task<SimAck> SetVarAsync(SimSetVar request, CancellationToken ct)
    {
        try
        {
            var reference = SimRef.Parse(request.Ref);
            var backend = _ctx.Sim.Resolve(reference, SimOperation.Set);
            var datum = request.ValueCase == SimSetVar.ValueOneofCase.Text ? SimDatum.Of(request.Text) : SimDatum.Of(request.Number);
            await backend.SetAsync(reference, datum, ct).ConfigureAwait(false);
            return new SimAck { Ok = true };
        }
        catch (FormatException ex) { return Nack(ErrorCode.ErrorInvalidArgument, ex.Message); }
        catch (SimException ex) { return Nack(ex.Code, ex.Message); }
    }

    private async Task<SimAck> FireAsync(SimFireEvent request, CancellationToken ct)
    {
        try
        {
            if (request.Params.Count > 5) return Nack(ErrorCode.ErrorInvalidArgument, "no máximo 5 parâmetros");
            var reference = SimRef.Parse(request.Ref);
            var backend = _ctx.Sim.Resolve(reference, SimOperation.Fire);
            await backend.FireAsync(reference, request.Params, ct).ConfigureAwait(false);
            return new SimAck { Ok = true };
        }
        catch (FormatException ex) { return Nack(ErrorCode.ErrorInvalidArgument, ex.Message); }
        catch (SimException ex) { return Nack(ex.Code, ex.Message); }
    }

    // ------------------------------------------------------------------ eventos do sim → cliente

    private void OnSimStatus(SimStatusInfo _) => Post(new Envelope { SimStatus = SimStatusMessage() });

    private void OnAircraft(AircraftInfo aircraft) => Post(new Envelope { SimAircraftChanged = ToProto(aircraft) });

    private void OnSystemEvent(SimSystemEventType type) => Post(new Envelope { SimSystemEvent = new SimSystemEvent { Type = type } });

    private void Post(Envelope envelope) => _ = SendAsync(envelope, _cts.Token).AsTask().ContinueWith(
        t => _logger.LogDebug(t.Exception, "Falha ao enviar notificação"), TaskContinuationOptions.OnlyOnFaulted);

    private SimStatus SimStatusMessage()
    {
        var s = _ctx.Sim.Connection.Status;
        return new SimStatus
        {
            Connected = s.Connected,
            InFlight = s.InFlight,
            Paused = s.Paused,
            PauseFlags = s.PauseFlags,
            WasmBridge = _ctx.Sim.Wasm?.IsAvailable ?? false,
            SimVersion = s.SimVersion,
        };
    }

    private static SimAircraftChanged ToProto(AircraftInfo a) => new()
    {
        Title = a.Title,
        LiveryName = a.LiveryName,
        LiveryFolder = a.LiveryFolder,
        AtcModel = a.AtcModel,
        AtcType = a.AtcType,
    };

    // ------------------------------------------------------------------ utilidades

    private async Task WatchdogAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), _ctx.Time, ct).ConfigureAwait(false);
            var limit = _state switch
            {
                State.Unpaired => PairingIdleLimit,
                State.AwaitHello or State.AwaitCapabilities => HelloIdleLimit,
                _ => ProtocolConstants.HostPingTimeout,
            };
            if (_ctx.Time.GetElapsedTime(_lastRxTimestamp) <= limit) continue;

            _logger.LogInformation("Timeout de {Remote} no estado {State}", _remote, _state);
            try
            {
                await SendAsync(new Envelope { Bye = new Bye { Reason = ByeReason.Timeout } }, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
            }
            await CloseAsync().ConfigureAwait(false);
            return;
        }
    }

    private void Cleanup()
    {
        if (_pairing is not null) _ctx.PinDisplay.Clear();
        if (_state == State.Ready)
        {
            _ctx.Sim.Connection.StatusChanged -= OnSimStatus;
            _ctx.Sim.Connection.AircraftChanged -= OnAircraft;
            _ctx.Sim.Connection.SystemEvent -= OnSystemEvent;
        }
        if (_sessionId is not null)
        {
            _ctx.Subscriptions.RemoveClient(_sessionId);
            _ctx.Sessions.MarkDisconnected(_client!.ClientId, _sessionId);
        }
        if (_sessionSecret is not null) CryptographicOperations.ZeroMemory(_sessionSecret);
    }

    private Task CloseAsync() => _cts.CancelAsync();

    private ValueTask SendAsync(Envelope envelope, CancellationToken ct) => _writer.WriteAsync(envelope, ct);

    private ValueTask SendErrorAsync(uint reqId, ErrorCode code, string message, CancellationToken ct) =>
        SendAsync(new Envelope { ReqId = reqId, Error = new Error { Code = code, Message = message } }, ct);

    private static Envelope Reply(Envelope request, Envelope response)
    {
        response.ReqId = request.ReqId;
        return response;
    }

    private static SimAck Nack(ErrorCode code, string message) => new() { Ok = false, Error = new Error { Code = code, Message = message } };

    private Envelope Pong(Envelope ping)
    {
        var rx = TimestampToUs(_lastRxTimestamp);
        return Reply(ping, new Envelope { Pong = new Pong { TClientUs = ping.Ping.TClientUs, THostRxUs = rx, THostTxUs = NowUs() } });
    }

    private ulong NowUs() => TimestampToUs(_ctx.Time.GetTimestamp());

    private ulong TimestampToUs(long timestamp) => (ulong)(timestamp * 1_000_000.0 / _ctx.Time.TimestampFrequency);

    private enum State
    {
        Unpaired,
        AwaitHello,
        AwaitCapabilities,
        Ready,
    }

    private sealed class PairingAttempt(string clientId, string deviceName, Platform platform, string pin)
    {
        public string ClientId { get; } = clientId;
        public string DeviceName { get; } = deviceName;
        public Platform Platform { get; } = platform;
        public string Pin { get; } = pin;
        public Spake2? Spake { get; set; }
        public Spake2Result? Result { get; set; }
    }
}
