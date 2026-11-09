using Microsoft.Extensions.Logging;

namespace MonRem.Host.Core.Security;

/// <summary>
/// O que o host mostra ao usuário durante o pareamento. O fingerprint (16 primeiros hex do SHA-256
/// do certificado do cliente) permite conferir no aparelho se é mesmo ele que está pedindo.
/// </summary>
public sealed record PairingPrompt(string DeviceName, string ClientId, string CertFingerprint, string RemoteAddress, string Pin);

/// <summary>Onde o host mostra o PIN de pareamento ao usuário (log, bandeja, janela).</summary>
public interface IPinDisplay
{
    void Show(PairingPrompt prompt);
    void Clear();
}

/// <summary>Padrão até existir a interface da bandeja: escreve o PIN no log.</summary>
public sealed class LogPinDisplay(ILogger<LogPinDisplay> logger) : IPinDisplay
{
    public void Show(PairingPrompt p) =>
        logger.LogWarning("PIN de pareamento para \"{Device}\" ({Remote}, cert {Fingerprint}): {Pin}",
            p.DeviceName, p.RemoteAddress, p.CertFingerprint, p.Pin);

    public void Clear() => logger.LogInformation("Pareamento encerrado; PIN descartado");
}
