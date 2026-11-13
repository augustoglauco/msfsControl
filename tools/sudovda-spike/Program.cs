// Spike da Fase 0 (ver driver/README.md): prova que o SudoVDA compilado/assinado/instalado sem
// mudanças (driver/sudovda) cria e remove um monitor virtual quando pedido pelo nosso host, usando
// as IOCTLs reais do driver (ver Sudovda.cs). Critério de sucesso da Fase 0 na spec do protocolo:
// "Monitor criado e removido pelo nosso host".
using SudovdaSpike;

Console.WriteLine("=== SudoVDA spike — Fase 0 ===");
Console.WriteLine();

IntPtr dev;
try
{
    dev = Sudovda.OpenDeviceHandle();
}
catch (Exception ex)
{
    Console.WriteLine($"Não consegui abrir o device do SudoVDA: {ex.Message}");
    Console.WriteLine("Confirme: driver instalado (ver driver/README.md), assinado, e o serviço WUDFRd rodando.");
    return;
}

Console.WriteLine("Device aberto.");

try
{
    var version = Sudovda.GetProtocolVersion(dev);
    Console.WriteLine($"Versão do protocolo do driver: {version.Major}.{version.Minor}.{version.Incremental}" +
                       (version.TestBuild ? " (test build)" : ""));
}
catch (Exception ex)
{
    Console.WriteLine($"IOCTL_GET_PROTOCOL_VERSION falhou: {ex.Message}");
}

var monitorGuid = Guid.NewGuid();
Console.WriteLine();
Console.WriteLine($"Criando monitor virtual 1920x1080@60Hz (MonitorGuid={monitorGuid})...");

(long AdapterLuid, uint TargetId) added;
try
{
    added = Sudovda.AddDisplay(dev, 1920, 1080, 60, monitorGuid, "MonRemF0", "MONREMF0001");
    Console.WriteLine($"IOCTL_ADD_VIRTUAL_DISPLAY ok: AdapterLuid=0x{added.AdapterLuid:x}, TargetId={added.TargetId}");
}
catch (Exception ex)
{
    Console.WriteLine($"IOCTL_ADD_VIRTUAL_DISPLAY falhou: {ex.Message}");
    Sudovda.CloseDeviceHandle(dev);
    return;
}

Console.WriteLine();
Console.WriteLine("Vá em Configurações > Sistema > Vídeo (ou Configurações de Tela) e confirme que");
Console.WriteLine("um monitor novo apareceu — é a prova da Fase 0 ('monitor criado... pelo nosso host').");
Console.WriteLine();
Console.WriteLine("O driver tem um watchdog (default 3s, ver driver/sudovda/Common/Include/sudovda-ioctl.h):");
Console.WriteLine("sem nenhuma IOCTL nesse intervalo, ele pode derrubar o monitor sozinho. Por isso este");
Console.WriteLine("spike manda um IOCTL_DRIVER_PING a cada 1s enquanto espera você.");
Console.WriteLine();
Console.WriteLine("ENTER para remover o monitor e sair.");

using var cts = new CancellationTokenSource();
var pingTask = Task.Run(async () =>
{
    while (!cts.IsCancellationRequested)
    {
        try { Sudovda.Ping(dev); } catch { /* reportado no ENTER se o device já tiver caído */ }
        try { await Task.Delay(1000, cts.Token); } catch (TaskCanceledException) { }
    }
});

Console.ReadLine();
cts.Cancel();
try { pingTask.Wait(2000); } catch { }

Console.WriteLine("Removendo monitor...");
try
{
    Sudovda.RemoveDisplay(dev, monitorGuid);
    Console.WriteLine("IOCTL_REMOVE_VIRTUAL_DISPLAY ok. Confirme nas Configurações de Tela que ele desapareceu.");
}
catch (Exception ex)
{
    Console.WriteLine($"IOCTL_REMOVE_VIRTUAL_DISPLAY falhou: {ex.Message}");
}

Sudovda.CloseDeviceHandle(dev);
Console.WriteLine("Fim.");
