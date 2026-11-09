using MonRem.Host.Core;
using MonRem.Host.Core.Devices;
#if WINDOWS_SERVICE
using System.IO.Ports;
#endif

namespace MonRem.Host;

/// <summary>Recarrega os perfis de painel quando um .json muda na pasta (com 500 ms de espera para o editor terminar de gravar).</summary>
internal sealed class ProfileWatcher(DeviceManager devices, MonRemHostOptions options, ILogger<ProfileWatcher> logger)
{
    public async Task RunAsync(CancellationToken ct)
    {
        var dir = options.EffectiveProfilesDirectory;
        Directory.CreateDirectory(dir);
        Reload(dir);

        using var watcher = new FileSystemWatcher(dir, "*.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        var changed = 0;
        void Mark(object? _, FileSystemEventArgs __) => Interlocked.Exchange(ref changed, 1);
        watcher.Changed += Mark;
        watcher.Created += Mark;
        watcher.Deleted += Mark;
        watcher.Renamed += Mark;

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(500, ct);
            if (Interlocked.Exchange(ref changed, 0) == 1) Reload(dir);
        }
    }

    private void Reload(string dir)
    {
        devices.ReloadProfiles(dir);
        logger.LogInformation("Perfis de painel: {Count} carregados de {Dir}", devices.Profiles.Profiles.Count, dir);
        foreach (var error in devices.Profiles.Errors) logger.LogWarning("Perfil com erro: {Error}", error);
    }
}

/// <summary>
/// Mantém abertas as portas seriais configuradas e roda uma sessão de painel em cada uma.
/// Porta que não responde com DeviceHello é tentada de novo a cada 30 s; porta caída, a cada 2 s.
/// </summary>
internal sealed class SerialPanelWatcher(DeviceManager devices, MonRemHostOptions options, ILogger<SerialPanelWatcher> logger)
{
    public Task RunAsync(CancellationToken ct)
    {
        if (options.SerialPorts.Count == 0)
        {
            logger.LogInformation("Nenhuma porta serial de painel configurada (MonRem:SerialPorts)");
            return Task.CompletedTask;
        }
#if WINDOWS_SERVICE
        return Task.WhenAll(options.SerialPorts.Select(p => RunPortAsync(p, ct)));
#else
        _ = devices; // usado só no Windows
        logger.LogWarning("Painéis por porta serial só no Windows nesta versão");
        return Task.CompletedTask;
#endif
    }

#if WINDOWS_SERVICE
    private async Task RunPortAsync(string portName, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TimeSpan wait;
            try
            {
                // DTR/RTS desligados: em placas ESP32 com circuito de auto-reset, mexer neles reinicia o chip.
                var port = new SerialPort(portName, options.SerialBaudRate, Parity.None, 8, StopBits.One)
                {
                    DtrEnable = false,
                    RtsEnable = false,
                    Handshake = Handshake.None,
                };
                port.Open();
                logger.LogInformation("Porta {Port} aberta a {Baud} baud", portName, options.SerialBaudRate);
                var isPanel = await devices.RunLinkAsync(new SerialDeviceLink(port.BaseStream, portName), ct);
                wait = isPanel ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(30);
                if (!isPanel) logger.LogWarning("Porta {Port}: nenhum painel respondeu; nova tentativa em 30 s", portName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                logger.LogDebug("Porta {Port} indisponível: {Message}", portName, ex.Message);
                wait = TimeSpan.FromSeconds(5);
            }
            await Task.Delay(wait, ct);
        }
    }
#endif
}
