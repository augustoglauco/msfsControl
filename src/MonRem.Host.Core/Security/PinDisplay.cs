using Microsoft.Extensions.Logging;

namespace MonRem.Host.Core.Security;

/// <summary>Onde o host mostra o PIN de pareamento ao usuário (log, bandeja, janela).</summary>
public interface IPinDisplay
{
    void Show(string deviceName, string pin);
    void Clear();
}

/// <summary>Padrão até existir a interface da bandeja: escreve o PIN no log.</summary>
public sealed class LogPinDisplay(ILogger<LogPinDisplay> logger) : IPinDisplay
{
    public void Show(string deviceName, string pin) =>
        logger.LogWarning("PIN de pareamento para \"{Device}\": {Pin}", deviceName, pin);

    public void Clear() => logger.LogInformation("Pareamento encerrado; PIN descartado");
}
