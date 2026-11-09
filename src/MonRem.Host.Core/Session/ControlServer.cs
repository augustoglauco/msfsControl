using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using MonRem.Host.Core.Security;
using MonRem.Host.Core.Sim;
using MonRem.Protocol;
using ProtocolV1 = MonRem.Protocol.V1;

namespace MonRem.Host.Core.Session;

/// <summary>Dependências compartilhadas por todas as conexões.</summary>
public sealed record HostContext(
    MonRemHostOptions Options,
    string HostId,
    X509Certificate2 HostCertificate,
    PairedClientStore PairedClients,
    PairingGuard PairingGuard,
    IPinDisplay PinDisplay,
    SessionRegistry Sessions,
    SimRouter Sim,
    SubscriptionHub Subscriptions,
    TimeProvider Time,
    ILoggerFactory LoggerFactory)
{
    public ConnectionDirectory Connections { get; } = new();

    /// <summary>Layouts de painéis do app (pasta layouts/).</summary>
    public Panels.LayoutStore Layouts { get; init; } = new();

    /// <summary>Remove o pareamento e derruba na hora as conexões ativas desse cliente.</summary>
    public async Task<bool> UnpairAsync(string clientId)
    {
        var removed = PairedClients.Remove(clientId);
        await Connections.CloseClientAsync(clientId, ProtocolV1.ByeReason.Unpaired).ConfigureAwait(false);
        return removed;
    }
}

/// <summary>Aceita conexões TCP, faz o TLS 1.3 e entrega cada uma a um <see cref="ClientConnection"/>.</summary>
public sealed class ControlServer(HostContext context)
{
    /// <summary>Teto de conexões simultâneas (clientes legítimos são poucos; o resto é abuso).</summary>
    public const int MaxConnections = 64;

    private readonly ILogger _logger = context.LoggerFactory.CreateLogger<ControlServer>();
    private readonly SemaphoreSlim _slots = new(MaxConnections, MaxConnections);

    /// <summary>Porta efetiva (útil quando a configurada é 0, nos testes).</summary>
    public int BoundPort { get; private set; }

    public async Task RunAsync(IPEndPoint endpoint, TaskCompletionSource? started, CancellationToken ct)
    {
        using var listener = new TcpListener(endpoint);
        listener.Start();
        BoundPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        _logger.LogInformation("Canal de controle ouvindo em {Endpoint}", listener.LocalEndpoint);
        started?.TrySetResult();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var tcp = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                if (!_slots.Wait(0))
                {
                    _logger.LogWarning("Limite de {Max} conexões atingido; recusando {Remote}", MaxConnections, tcp.Client.RemoteEndPoint);
                    tcp.Dispose();
                    continue;
                }
                _ = Task.Run(async () =>
                {
                    try { await HandleAsync(tcp, ct).ConfigureAwait(false); }
                    finally { _slots.Release(); }
                }, CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task HandleAsync(TcpClient tcp, CancellationToken ct)
    {
        var remote = tcp.Client.RemoteEndPoint;
        using (tcp)
        {
            tcp.NoDelay = true;
            await using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            try
            {
                using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = context.HostCertificate,
                    ClientCertificateRequired = true,
                    // Aceita qualquer cert aqui: o pinning é decidido depois (pareado ou só pode parear).
                    RemoteCertificateValidationCallback = (_, cert, _, _) => cert is not null,
                    EnabledSslProtocols = SslProtocols.Tls13,
                    ApplicationProtocols = [new SslApplicationProtocol(ProtocolConstants.AlpnApp)],
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                }, handshakeTimeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is AuthenticationException or IOException or OperationCanceledException)
            {
                _logger.LogInformation("Handshake TLS falhou com {Remote}: {Message}", remote, ex.Message);
                return;
            }

            if (ssl.RemoteCertificate is null) return;
            using var clientCert = X509CertificateLoader.LoadCertificate(ssl.RemoteCertificate.GetRawCertData());
            var paired = context.PairedClients.FindByFingerprint(MonRem.Security.Certificates.Fingerprint(clientCert));
            var countedUnpaired = false;
            if (paired is null)
            {
                // Certificados desconhecidos só podem parear, e no máximo 4 ao mesmo tempo.
                if (!context.Connections.TryEnterUnpaired())
                {
                    _logger.LogInformation("Recusando {Remote}: limite de conexões sem pareamento", remote);
                    return;
                }
                countedUnpaired = true;
            }
            var connection = new ClientConnection(context, ssl, clientCert, remote?.ToString() ?? "?", paired, countedUnpaired);
            try
            {
                await connection.RunAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ProtocolException or OperationCanceledException)
            {
                _logger.LogInformation("Conexão {Remote} encerrada: {Message}", remote, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro inesperado na conexão {Remote}", remote);
            }

        }
    }
}
