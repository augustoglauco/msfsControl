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
/// Toda escrita passa pelo <see cref="ConnectionOutbox"/>: nada aqui espera o socket.
/// </summary>
internal sealed class ClientConnection
{
    /// <summary>Conexão desconhecida tem 15 s para começar a parear.</summary>
    private static readonly TimeSpan UnpairedIdleLimit = TimeSpan.FromSeconds(15);

    /// <summary>Prazo absoluto do pareamento a partir do PairStart (digitar o PIN cabe folgado).</summary>
    private static readonly TimeSpan PairingDeadline = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan HelloIdleLimit = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CloseFlushLimit = TimeSpan.FromSeconds(1);

    private readonly HostContext _ctx;
    private readonly SslStream _ssl;
    private readonly X509Certificate2 _clientCert;
    private readonly string _fingerprint;
    private readonly string _remote;
    private readonly ILogger _logger;
    private readonly ConnectionOutbox _outbox;
    private readonly CancellationTokenSource _cts = new();

    private State _state;
    private PairedClientRecord? _client;
    private PairingAttempt? _pairing;
    private string? _sessionId;
    private byte[]? _sessionSecret;
    private IDisposable? _registration;
    private bool _countedUnpaired;
    private long _lastRxTimestamp;

    /// <param name="countedUnpaired">true se o servidor reservou uma vaga de conexão sem pareamento para esta.</param>
    public ClientConnection(HostContext ctx, SslStream ssl, X509Certificate2 clientCert, string remote, PairedClientRecord? client, bool countedUnpaired)
    {
        _ctx = ctx;
        _ssl = ssl;
        _clientCert = clientCert;
        _fingerprint = Certificates.Fingerprint(clientCert);
        _remote = remote;
        _logger = ctx.LoggerFactory.CreateLogger<ClientConnection>();
        _outbox = new ConnectionOutbox(reason =>
        {
            _logger.LogInformation("Encerrando {Remote}: {Reason}", _remote, reason);
            _cts.Cancel();
        });
        _client = client;
        _countedUnpaired = countedUnpaired;
        _state = _client is null ? State.Unpaired : State.AwaitHello;
    }

    /// <summary>Segredo da sessão (base das chaves UDP); null antes do Hello.</summary>
    internal byte[]? SessionSecret => _sessionSecret;

    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var token = linked.Token;
        using var writerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var writer = _outbox.RunWriterAsync(_ssl, writerCts.Token);
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
            // Encerrada por nós (timeout, Bye, despareamento, cliente sem ler).
        }
        finally
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            try { await watchdog.ConfigureAwait(false); } catch (OperationCanceledException) { }
            Cleanup();

            // Dá até 1 s para o Bye e as últimas respostas saírem.
            _outbox.Close();
            if (await Task.WhenAny(writer, Task.Delay(CloseFlushLimit, CancellationToken.None)).ConfigureAwait(false) != writer)
                await writerCts.CancelAsync().ConfigureAwait(false);
            await writer.ConfigureAwait(false);
        }
    }

    private async Task DispatchAsync(Envelope e, CancellationToken ct)
    {
        // Re-pareamento de um cert já conhecido (ex.: pareamento anterior caiu antes do cliente gravar o host).
        if (_state == State.AwaitHello && e.BodyCase == Envelope.BodyOneofCase.PairStart)
            _state = State.Unpaired;

        switch (_state)
        {
            case State.Unpaired:
                await HandlePairingAsync(e).ConfigureAwait(false);
                return;
            case State.AwaitHello when e.BodyCase == Envelope.BodyOneofCase.Hello:
                HandleHello(e);
                return;
            case State.AwaitCapabilities when e.BodyCase == Envelope.BodyOneofCase.Capabilities:
                HandleCapabilities(e);
                return;
            case State.Ready:
                await HandleReadyAsync(e, ct).ConfigureAwait(false);
                return;
        }

        if (e.BodyCase == Envelope.BodyOneofCase.Ping)
        {
            Send(Pong(e));
            return;
        }
        SendError(e.ReqId, ErrorCode.ErrorInvalidArgument, $"{e.BodyCase} não esperado no estado {_state}");
    }

    // ------------------------------------------------------------------ pareamento

    private Task HandlePairingAsync(Envelope e)
    {
        switch (e.BodyCase)
        {
            case Envelope.BodyOneofCase.PairStart when _pairing is null:
                OnPairStart(e);
                break;
            case Envelope.BodyOneofCase.PairSpake when _pairing is { Result: null }:
                OnPairSpake(e);
                break;
            case Envelope.BodyOneofCase.PairConfirm when _pairing is { Result: not null }:
                OnPairConfirm(e);
                break;
            default:
                SendError(e.ReqId, ErrorCode.ErrorAuthUnpaired, "cliente não pareado");
                Close();
                break;
        }
        return Task.CompletedTask;
    }

    private void OnPairStart(Envelope e)
    {
        var start = e.PairStart;
        if (!_ctx.Options.AllowPairing)
        {
            SendError(e.ReqId, ErrorCode.ErrorPairingLocked, "pareamento desativado no host");
            Close();
            return;
        }
        var retry = _ctx.PairingGuard.RetryAfter;
        if (retry > TimeSpan.Zero)
        {
            Send(Reply(e, new Envelope { PairResult = new PairResult { Ok = false, RetryAfterS = (uint)Math.Ceiling(retry.TotalSeconds) } }));
            Close();
            return;
        }
        if (string.IsNullOrWhiteSpace(start.ClientId) || start.ClientId.Length > 64 || start.DeviceName.Length > 64)
        {
            SendError(e.ReqId, ErrorCode.ErrorInvalidArgument, "client_id ou device_name inválido");
            Close();
            return;
        }
        // Um client_id já pareado com OUTRO certificado não pode ser tomado: despareie no host antes.
        var existing = _ctx.PairedClients.FindByClientId(start.ClientId);
        if (existing is not null && existing.CertFingerprint != _fingerprint)
        {
            SendError(e.ReqId, ErrorCode.ErrorInvalidArgument, "client_id já pareado com outro certificado; despareie no host");
            Close();
            return;
        }
        if (!_ctx.PairingGuard.TryBegin(this))
        {
            Send(Reply(e, new Envelope { PairResult = new PairResult { Ok = false, RetryAfterS = 5 } }));
            Close();
            return;
        }

        var pin = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        _pairing = new PairingAttempt(start.ClientId, start.DeviceName, start.Platform, pin, _ctx.Time.GetTimestamp());
        _ctx.PinDisplay.Show(new PairingPrompt(
            string.IsNullOrWhiteSpace(start.DeviceName) ? start.ClientId : start.DeviceName,
            start.ClientId, _fingerprint[..16], _remote, pin));
        _logger.LogInformation("Pareamento iniciado por {Client} ({Remote}, cert {Fingerprint})", start.ClientId, _remote, _fingerprint[..16]);

        Send(Reply(e, new Envelope
        {
            PairChallenge = new PairChallenge
            {
                HostId = _ctx.HostId,
                PinDigits = ProtocolConstants.PinDigits,
                KdfIterations = (uint)_ctx.Options.PinKdfIterations,
            },
        }));
    }

    private void OnPairSpake(Envelope e)
    {
        var attempt = _pairing!;
        try
        {
            var w = Spake2.DeriveW(attempt.Pin, attempt.ClientId, _ctx.HostId, _ctx.Options.PinKdfIterations);
            var spake = new Spake2(Spake2Role.B, attempt.ClientId, _ctx.HostId, w);
            attempt.Result = spake.Finish(e.PairSpake.Share.Span, Certificates.PairingAad(_ctx.HostCertificate, _clientCert));
            Send(Reply(e, new Envelope { PairSpake = new PairSpake { Share = Google.Protobuf.ByteString.CopyFrom(spake.OwnShare) } }));
        }
        catch (CryptographicException ex)
        {
            FailPairing(e, $"SPAKE2 inválido: {ex.Message}");
        }
    }

    private void OnPairConfirm(Envelope e)
    {
        var attempt = _pairing!;
        if (!attempt.Result!.VerifyPeer(e.PairConfirm.Mac.Span))
        {
            FailPairing(e, "PIN incorreto");
            return;
        }

        // Resposta ao PairConfirm do cliente = a confirmação do host; o PairResult vem em seguida, como notificação.
        Send(Reply(e, new Envelope { PairConfirm = new PairConfirm { Mac = Google.Protobuf.ByteString.CopyFrom(attempt.Result.OwnConfirmation) } }));

        var now = DateTimeOffset.UtcNow;
        _client = new PairedClientRecord(attempt.ClientId, attempt.DeviceName, attempt.Platform, _fingerprint, now, now);
        _ctx.PairedClients.Upsert(_client);
        _ctx.PairingGuard.RecordSuccess();
        EndPairing();
        _state = State.AwaitHello;
        _lastRxTimestamp = _ctx.Time.GetTimestamp();
        LeaveUnpairedSlot();
        _logger.LogInformation("Cliente {Client} pareado", attempt.ClientId);

        Send(new Envelope { PairResult = new PairResult { Ok = true, HostId = _ctx.HostId } });
    }

    private void FailPairing(Envelope e, string reason)
    {
        _ctx.PairingGuard.RecordFailure();
        EndPairing();
        _logger.LogWarning("Pareamento falhou ({Remote}): {Reason}", _remote, reason);
        Send(Reply(e, new Envelope
        {
            PairResult = new PairResult { Ok = false, RetryAfterS = (uint)Math.Ceiling(_ctx.PairingGuard.RetryAfter.TotalSeconds) },
        }));
        Close();
    }

    private void EndPairing()
    {
        if (_pairing is not null) _ctx.PinDisplay.Clear();
        _pairing = null;
        _ctx.PairingGuard.End(this);
    }

    // ------------------------------------------------------------------ sessão

    private void HandleHello(Envelope e)
    {
        var hello = e.Hello;
        if (hello.ProtocolVersion != ProtocolConstants.ProtocolVersion)
        {
            SendError(e.ReqId, ErrorCode.ErrorVersionUnsupported, $"versão {hello.ProtocolVersion} não suportada (host: {ProtocolConstants.ProtocolVersion})");
            Close();
            return;
        }
        if (hello.ClientId != _client!.ClientId)
        {
            SendError(e.ReqId, ErrorCode.ErrorAuthUnpaired, "client_id não corresponde ao certificado");
            Close();
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
            _ctx.Sessions.MarkDisconnected(hello.ClientId, sessionId);
            SendError(e.ReqId, ErrorCode.ErrorInvalidArgument, $"key_share inválido: {ex.Message}");
            Close();
            return;
        }

        _sessionId = sessionId;
        _state = State.AwaitCapabilities;
        _registration = _ctx.Connections.Register(hello.ClientId, CloseWithByeAsync);
        _ctx.PairedClients.Touch(hello.ClientId);
        _logger.LogInformation("Sessão {Session} aberta para {Client} (resume: {Resumed})", sessionId, hello.ClientId, resumed);

        Send(Reply(e, new Envelope
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
        }));
    }

    private void HandleCapabilities(Envelope e)
    {
        _state = State.Ready;
        _ctx.Sim.Connection.StatusChanged += OnSimStatus;
        _ctx.Sim.Connection.AircraftChanged += OnAircraft;
        _ctx.Sim.Connection.SystemEvent += OnSystemEvent;

        Send(Reply(e, new Envelope
        {
            SessionReady = new SessionReady
            {
                UdpPorts = new UdpPorts { Media = (uint)_ctx.Options.MediaPort, Input = (uint)_ctx.Options.InputPort },
                ActiveLayoutId = "",
            },
        }));
        Send(new Envelope { SimStatus = SimStatusMessage() });
        if (_ctx.Sim.Connection.Aircraft is { } aircraft)
            Send(new Envelope { SimAircraftChanged = ToProto(aircraft) });
    }

    private async Task HandleReadyAsync(Envelope e, CancellationToken ct)
    {
        switch (e.BodyCase)
        {
            case Envelope.BodyOneofCase.Ping:
                Send(Pong(e));
                break;

            case Envelope.BodyOneofCase.Bye:
                _logger.LogInformation("Cliente {Client} saiu: {Reason}", _client?.ClientId, e.Bye.Reason);
                Close();
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
                Send(Reply(e, new Envelope { PairedClients = list }));
                break;

            case Envelope.BodyOneofCase.Unpair:
                // Pelo protocolo, um cliente só despareia a si mesmo. Despareiar outros é ação do host.
                if (e.Unpair.ClientId.Length > 0 && e.Unpair.ClientId != _client!.ClientId)
                {
                    SendError(e.ReqId, ErrorCode.ErrorNotOwner, "um cliente só pode despareiar a si mesmo");
                    break;
                }
                Send(Reply(e, new Envelope { SimAck = new SimAck { Ok = true } }));
                await _ctx.UnpairAsync(_client!.ClientId).ConfigureAwait(false);
                break;

            case Envelope.BodyOneofCase.GetSimStatus:
                Send(Reply(e, new Envelope { SimStatus = SimStatusMessage() }));
                break;

            case Envelope.BodyOneofCase.SimSubscribe:
                var subscribed = _ctx.Subscriptions.Subscribe(_sessionId!, e.SimSubscribe, env =>
                {
                    _outbox.OfferSimValues(env.SimValues);
                    return ValueTask.CompletedTask;
                });
                Send(Reply(e, new Envelope { SimSubscribed = subscribed }));
                break;

            case Envelope.BodyOneofCase.SimUnsubscribe:
                var ok = _ctx.Subscriptions.Unsubscribe(_sessionId!, e.SimUnsubscribe.SubId);
                if (e.ReqId != 0)
                    Send(Reply(e, new Envelope { SimAck = ok ? new SimAck { Ok = true } : Nack(ErrorCode.ErrorNotFound, "assinatura inexistente") }));
                break;

            case Envelope.BodyOneofCase.SimSetVar:
                Send(Reply(e, new Envelope { SimAck = await SimCommands.SetAsync(_ctx.Sim, e.SimSetVar, ct).ConfigureAwait(false) }));
                break;

            case Envelope.BodyOneofCase.SimFireEvent:
                Send(Reply(e, new Envelope { SimAck = await SimCommands.FireAsync(_ctx.Sim, e.SimFireEvent, ct).ConfigureAwait(false) }));
                break;

            case Envelope.BodyOneofCase.SimListInputEvents:
                var eventList = new SimInputEventList();
                try
                {
                    eventList.Items.AddRange(await _ctx.Sim.Connection.ListInputEventsAsync(ct).ConfigureAwait(false));
                }
                catch (Exception ex) when (ex is SimException or TimeoutException)
                {
                    _logger.LogDebug(ex, "ListInputEvents falhou");
                }
                Send(Reply(e, new Envelope { SimInputEventList = eventList }));
                break;

            case Envelope.BodyOneofCase.StreamStats:
                break; // telemetria do vídeo: ainda sem pipeline nesta versão

            default:
                SendError(e.ReqId, ErrorCode.ErrorInternal, $"{e.BodyCase} ainda não implementado no host");
                break;
        }
    }

    // ------------------------------------------------------------------ eventos do sim → cliente

    private void OnSimStatus(SimStatusInfo _) => Send(new Envelope { SimStatus = SimStatusMessage() });

    private void OnAircraft(AircraftInfo aircraft) => Send(new Envelope { SimAircraftChanged = ToProto(aircraft) });

    private void OnSystemEvent(SimSystemEventType type) => Send(new Envelope { SimSystemEvent = new SimSystemEvent { Type = type } });

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

            if (_pairing is { } attempt && _ctx.Time.GetElapsedTime(attempt.StartedTimestamp) > PairingDeadline)
            {
                _logger.LogInformation("Pareamento de {Remote} expirou", _remote);
                Close(ByeReason.Timeout);
                return;
            }

            var limit = _state switch
            {
                State.Unpaired => _pairing is null ? UnpairedIdleLimit : PairingDeadline,
                State.AwaitHello or State.AwaitCapabilities => HelloIdleLimit,
                _ => ProtocolConstants.HostPingTimeout,
            };
            if (_ctx.Time.GetElapsedTime(_lastRxTimestamp) <= limit) continue;

            _logger.LogInformation("Timeout de {Remote} no estado {State}", _remote, _state);
            Close(ByeReason.Timeout);
            return;
        }
    }

    private void Cleanup()
    {
        EndPairing();
        LeaveUnpairedSlot();
        _registration?.Dispose();
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

    private void LeaveUnpairedSlot()
    {
        if (!_countedUnpaired) return;
        _countedUnpaired = false;
        _ctx.Connections.LeaveUnpaired();
    }

    /// <summary>Encerra a conexão; com motivo, manda Bye antes (sai pela fila, até 1 s).</summary>
    private void Close(ByeReason? reason = null)
    {
        if (reason is { } r) _outbox.Enqueue(new Envelope { Bye = new Bye { Reason = r } });
        _cts.Cancel();
    }

    private Task CloseWithByeAsync(ByeReason reason)
    {
        Close(reason);
        return Task.CompletedTask;
    }

    private void Send(Envelope envelope) => _outbox.Enqueue(envelope);

    private void SendError(uint reqId, ErrorCode code, string message) =>
        Send(new Envelope { ReqId = reqId, Error = new Error { Code = code, Message = message } });

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

    private sealed class PairingAttempt(string clientId, string deviceName, Platform platform, string pin, long startedTimestamp)
    {
        public string ClientId { get; } = clientId;
        public string DeviceName { get; } = deviceName;
        public Platform Platform { get; } = platform;
        public string Pin { get; } = pin;
        public long StartedTimestamp { get; } = startedTimestamp;
        public Spake2Result? Result { get; set; }
    }
}
