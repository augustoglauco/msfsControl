using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using Google.Protobuf;
using MonRem.Protocol;
using MonRem.Protocol.V1;
using MonRem.Security;

namespace MonRem.Client.Core;

/// <summary>Resposta de erro do host a uma requisição.</summary>
public sealed class HostErrorException(Error error) : Exception($"{error.Code}: {error.Message}")
{
    public Error Error { get; } = error;
}

public sealed class PairingFailedException(string message, uint retryAfterSeconds = 0) : Exception(message)
{
    public uint RetryAfterSeconds { get; } = retryAfterSeconds;
}

/// <summary>
/// Cliente do canal de controle: TLS 1.3 com cert do cliente, pareamento SPAKE2,
/// Hello com ECDH, keepalive e correlação requisição/resposta por req_id.
/// </summary>
public sealed class ControlClient : IAsyncDisposable
{
    private readonly TcpClient _tcp;
    private readonly SslStream _ssl;
    private readonly ClientIdentity _identity;
    private readonly string _address;
    private readonly EnvelopeWriter _writer;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<Envelope>> _pending = new();
    // Limitado: notificações que ninguém espera (ex. SimValues a 60 Hz) não podem acumular sem fim.
    private readonly Channel<Envelope> _inbox = Channel.CreateBounded<Envelope>(
        new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
    private volatile Exception? _closedError;
    private Task? _readLoop;
    private Task? _pingLoop;
    private int _nextReqId;

    private ControlClient(TcpClient tcp, SslStream ssl, ClientIdentity identity, string address)
    {
        _tcp = tcp;
        _ssl = ssl;
        _identity = identity;
        _address = address;
        _writer = new EnvelopeWriter(ssl);
    }

    public X509Certificate2 ServerCertificate { get; private set; } = null!;

    /// <summary>Host já pareado (cert do host fixado); null se for preciso parear.</summary>
    public PairedHost? PairedHost { get; private set; }

    /// <summary>
    /// Já houve um host pareado neste endereço com OUTRO certificado. Pode ser reinstalação do host
    /// ou alguém se passando por ele: o app deve avisar o usuário antes de parear de novo.
    /// </summary>
    public PairedHost? CertificateChangedFrom { get; private set; }

    public string? SessionId { get; private set; }
    public byte[]? SessionSecret { get; private set; }
    public byte[]? ResumeToken { get; private set; }

    /// <summary>Mensagens sem req_id vindas do host (SimValues, SimStatus, Bye...).</summary>
    public event Action<Envelope>? Notification;

    /// <summary>
    /// A conexão caiu (host fechou, rede sumiu ou sem Pong em 5 s). Não dispara no DisposeAsync.
    /// Quem usa o cliente decide reconectar.
    /// </summary>
    public event Action<Exception>? Disconnected;

    /// <summary>Último RTT medido pelo keepalive.</summary>
    public TimeSpan? LastRtt { get; private set; }

    public static async Task<ControlClient> ConnectAsync(string host, int port, ClientIdentity identity, CancellationToken ct)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(host, port, ct).ConfigureAwait(false);
            var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            // O cert do host é validado DEPOIS: fixado (pareado) ou ainda desconhecido (só pode parear).
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "monrem-host",
                ClientCertificates = [identity.Certificate],
                RemoteCertificateValidationCallback = (_, cert, _, _) => cert is not null,
                EnabledSslProtocols = SslProtocols.Tls13,
                ApplicationProtocols = [new SslApplicationProtocol(ProtocolConstants.AlpnApp)],
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            }, ct).ConfigureAwait(false);

            var client = new ControlClient(tcp, ssl, identity, $"{host}:{port}");
            client.ServerCertificate = X509CertificateLoader.LoadCertificate(ssl.RemoteCertificate!.GetRawCertData());
            var fingerprint = Certificates.Fingerprint(client.ServerCertificate);
            client.PairedHost = identity.FindHostByFingerprint(fingerprint);
            if (client.PairedHost is null)
                client.CertificateChangedFrom = identity.Hosts.FirstOrDefault(h => h.LastAddress == client._address && h.CertFingerprint != fingerprint);
            client._readLoop = Task.Run(() => client.ReadLoopAsync(client._cts.Token), CancellationToken.None);
            return client;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Pareia com o host. <paramref name="askPin"/> recebe o host_id e devolve o PIN digitado pelo usuário.
    /// Se <see cref="CertificateChangedFrom"/> estiver preenchido, exige <paramref name="acceptChangedCertificate"/>.
    /// </summary>
    public async Task<PairedHost> PairAsync(string deviceName, Platform platform, Func<string, Task<string>> askPin,
        bool acceptChangedCertificate = false, CancellationToken ct = default)
    {
        if (CertificateChangedFrom is not null && !acceptChangedCertificate)
            throw new PairingFailedException("o certificado do host neste endereço mudou; confirme com o usuário antes de parear");

        var challengeEnv = await RequestAsync(new Envelope
        {
            PairStart = new PairStart { ClientId = _identity.ClientId, DeviceName = deviceName, Platform = platform },
        }, ct).ConfigureAwait(false);
        if (challengeEnv.BodyCase == Envelope.BodyOneofCase.PairResult)
            throw new PairingFailedException("host recusou o pareamento", challengeEnv.PairResult.RetryAfterS);
        if (challengeEnv.BodyCase != Envelope.BodyOneofCase.PairChallenge)
            throw new PairingFailedException($"resposta inesperada ao PairStart: {challengeEnv.BodyCase}");
        var challenge = challengeEnv.PairChallenge;
        if (challenge.KdfIterations is < 1_000 or > 1_000_000)
            throw new PairingFailedException($"kdf_iterations fora da faixa: {challenge.KdfIterations}");
        var hostId = challenge.HostId;

        var pin = (await askPin(hostId).ConfigureAwait(false)).Trim();
        var w = Spake2.DeriveW(pin, _identity.ClientId, hostId, (int)challenge.KdfIterations);
        var spake = new Spake2(Spake2Role.A, _identity.ClientId, hostId, w);

        var spakeReply = await RequestAsync(new Envelope
        {
            PairSpake = new PairSpake { Share = ByteString.CopyFrom(spake.OwnShare) },
        }, ct).ConfigureAwait(false);
        if (spakeReply.BodyCase != Envelope.BodyOneofCase.PairSpake)
            throw new PairingFailedException("host abortou o pareamento", spakeReply.PairResult?.RetryAfterS ?? 0);

        Spake2Result result;
        try
        {
            result = spake.Finish(spakeReply.PairSpake.Share.Span, Certificates.PairingAad(ServerCertificate, _identity.Certificate));
        }
        catch (CryptographicException ex)
        {
            throw new PairingFailedException($"resposta SPAKE2 inválida: {ex.Message}");
        }

        var confirmReply = await RequestAsync(new Envelope
        {
            PairConfirm = new PairConfirm { Mac = ByteString.CopyFrom(result.OwnConfirmation) },
        }, ct).ConfigureAwait(false);
        if (confirmReply.BodyCase == Envelope.BodyOneofCase.PairResult)
            throw new PairingFailedException("PIN incorreto", confirmReply.PairResult.RetryAfterS);
        if (confirmReply.BodyCase != Envelope.BodyOneofCase.PairConfirm)
            throw new PairingFailedException($"resposta inesperada ao PairConfirm: {confirmReply.BodyCase}");
        if (!result.VerifyPeer(confirmReply.PairConfirm.Mac.Span))
            throw new PairingFailedException("confirmação do host inválida (possível intermediário)");

        var final = await WaitForAsync(Envelope.BodyOneofCase.PairResult, ct).ConfigureAwait(false);
        if (!final.PairResult.Ok) throw new PairingFailedException("host não concluiu o pareamento");

        var host = new PairedHost(hostId, Certificates.Fingerprint(ServerCertificate), _address);
        _identity.SaveHost(host);
        PairedHost = host;
        CertificateChangedFrom = null;
        return host;
    }

    /// <summary>Hello + Capabilities. Exige host pareado. Inicia o keepalive.</summary>
    public async Task<SessionReady> OpenSessionAsync(Platform platform, string appVersion, Capabilities capabilities,
        byte[]? resumeToken = null, CancellationToken ct = default)
    {
        if (PairedHost is null) throw new InvalidOperationException("host não pareado: chame PairAsync antes");

        using var ecdh = SessionKeys.CreateEphemeral();
        var ackEnv = await RequestAsync(new Envelope
        {
            Hello = new Hello
            {
                ProtocolVersion = ProtocolConstants.ProtocolVersion,
                ClientId = _identity.ClientId,
                Platform = platform,
                AppVersion = appVersion,
                DeviceName = Environment.MachineName,
                ResumeToken = resumeToken is null ? ByteString.Empty : ByteString.CopyFrom(resumeToken),
                KeyShare = ByteString.CopyFrom(SessionKeys.ExportPublic(ecdh)),
            },
        }, ct).ConfigureAwait(false);

        if (ackEnv.BodyCase != Envelope.BodyOneofCase.HelloAck)
            throw new IOException($"resposta inesperada ao Hello: {ackEnv.BodyCase}");
        var ack = ackEnv.HelloAck;
        if (ack.HostId != PairedHost.HostId)
            throw new IOException("host_id do HelloAck não corresponde ao host pareado");
        SessionId = ack.SessionId;
        ResumeToken = ack.ResumeToken.ToByteArray();
        SessionSecret = SessionKeys.DeriveSessionSecret(ecdh, ack.KeyShare.Span, ack.SessionId);

        var ready = await RequestAsync(new Envelope { Capabilities = capabilities }, ct).ConfigureAwait(false);
        if (ready.BodyCase != Envelope.BodyOneofCase.SessionReady)
            throw new IOException($"resposta inesperada ao Capabilities: {ready.BodyCase}");
        _pingLoop = Task.Run(() => PingLoopAsync(_cts.Token), CancellationToken.None);
        return ready.SessionReady;
    }

    /// <summary>Envia com req_id novo e espera a resposta (ou Error, que vira exceção).</summary>
    public async Task<Envelope> RequestAsync(Envelope request, CancellationToken ct = default)
    {
        var id = (uint)Interlocked.Increment(ref _nextReqId);
        request.ReqId = id;
        var tcs = new TaskCompletionSource<Envelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            // A leitura já terminou: o finally dela não vai mais completar esta requisição.
            if (_closedError is { } closed) throw new IOException("conexão encerrada", closed);
            await _writer.WriteAsync(request, ct).ConfigureAwait(false);
            using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
            var response = await tcs.Task.ConfigureAwait(false);
            if (response.BodyCase == Envelope.BodyOneofCase.Error) throw new HostErrorException(response.Error);
            return response;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Envia sem esperar resposta.</summary>
    public ValueTask SendAsync(Envelope message, CancellationToken ct = default) => _writer.WriteAsync(message, ct);

    /// <summary>Espera a próxima notificação de um tipo (descarta as de outros tipos da fila interna).</summary>
    public async Task<Envelope> WaitForAsync(Envelope.BodyOneofCase kind, CancellationToken ct = default)
    {
        await foreach (var e in _inbox.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (e.BodyCase == kind) return e;
        }
        throw new IOException("conexão encerrada antes da mensagem esperada");
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        Exception? failure = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var e = await EnvelopeFraming.ReadAsync(_ssl, ct).ConfigureAwait(false);
                if (e is null) break;
                if (e.ReqId != 0 && _pending.TryGetValue(e.ReqId, out var tcs))
                {
                    tcs.TrySetResult(e);
                    continue;
                }
                _inbox.Writer.TryWrite(e);
                try
                {
                    Notification?.Invoke(e);
                }
                catch (Exception)
                {
                    // Erro em quem assina não pode derrubar a leitura.
                }
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            var error = failure ?? new IOException("conexão encerrada pelo host");
            _closedError = error;
            foreach (var p in _pending.Values) p.TrySetException(error);
            _inbox.Writer.TryComplete();
            if (!_cts.IsCancellationRequested)
            {
                try
                {
                    Disconnected?.Invoke(error);
                }
                catch (Exception)
                {
                    // Erro em quem assina não pode derrubar o laço.
                }
            }
        }
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var t0 = Environment.TickCount64;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(ProtocolConstants.ClientPongTimeout);
                await RequestAsync(new Envelope { Ping = new Ping { TClientUs = (ulong)t0 * 1000 } }, timeout.Token).ConfigureAwait(false);
                LastRtt = TimeSpan.FromMilliseconds(Environment.TickCount64 - t0);
                await Task.Delay(ProtocolConstants.PingInterval, ct).ConfigureAwait(false);
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Sem Pong em 5 s (ex.: celular dormiu, Wi-Fi caiu): a conexão está morta mesmo que o TCP
            // não tenha avisado. Fecha o socket para a leitura terminar e o Disconnected disparar.
            try { _ssl.Dispose(); } catch (Exception) { }
            try { _tcp.Dispose(); } catch (Exception) { }
        }
        catch (Exception)
        {
            // DisposeAsync em andamento.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        // Ordem: para o keepalive (que escreve), fecha o socket (destrava a leitura), espera a leitura.
        if (_pingLoop is not null)
        {
            try { await _pingLoop.ConfigureAwait(false); } catch (Exception) { }
        }
        try { await _ssl.DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
        _tcp.Dispose();
        if (_readLoop is not null)
        {
            try { await _readLoop.ConfigureAwait(false); } catch (Exception) { }
        }
        await _writer.DisposeAsync().ConfigureAwait(false);
        if (SessionSecret is not null) CryptographicOperations.ZeroMemory(SessionSecret);
        _cts.Dispose();
    }
}
