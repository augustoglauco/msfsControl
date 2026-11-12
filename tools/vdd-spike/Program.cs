// Spike de validação: confirma se o driver parsec-vdd cria um monitor que o Windows reconhece
// de verdade (aparece em EnumDisplayMonitors / Configurações de Tela), antes de desenhar a
// integração real no host. Ver tools/vdd-spike/README.md para instruções.
using System.Runtime.InteropServices;
using VddSpike;

[DllImport("user32.dll")]
static extern int GetSystemMetrics(int index);
const int SM_CMONITORS = 80;

Console.WriteLine("=== Spike parsec-vdd: monitor virtual ===");
Console.WriteLine($"Monitores ANTES: {GetSystemMetrics(SM_CMONITORS)}");

IntPtr vdd;
try
{
    vdd = Vdd.OpenDeviceHandle(Vdd.AdapterGuid);
}
catch (Exception ex)
{
    Console.WriteLine($"FALHOU ao abrir o driver: {ex.Message}");
    Console.WriteLine("Confirme que o parsec-vdd está instalado (Gerenciador de Dispositivos > Monitores,");
    Console.WriteLine("ou procure 'Parsec Virtual Display Adapter').");
    return 1;
}

Console.WriteLine("Driver aberto com sucesso.");
try
{
    var version = Vdd.Version(vdd);
    Console.WriteLine($"Versão do driver (minor): {version}");
}
catch (Exception ex)
{
    Console.WriteLine($"AVISO: VDD_IOCTL_VERSION falhou: {ex.Message} (segue mesmo assim)");
}

// Ping de keep-alive: o header diz que precisa rodar em thread própria a cada <100ms,
// senão o driver derruba os monitores adicionados.
using var stopPing = new CancellationTokenSource();
var pingTask = Task.Run(async () =>
{
    while (!stopPing.IsCancellationRequested)
    {
        try { Vdd.Update(vdd); } catch { /* o loop principal já reporta erros do Add/Remove */ }
        await Task.Delay(50, stopPing.Token).ContinueWith(_ => { });
    }
});

int index;
try
{
    index = Vdd.AddDisplay(vdd);
    Console.WriteLine($"VddAddDisplay retornou índice: {index}");
}
catch (Exception ex)
{
    Console.WriteLine($"FALHOU ao adicionar o monitor virtual: {ex.Message}");
    stopPing.Cancel();
    Vdd.CloseDeviceHandle(vdd);
    return 1;
}

Console.WriteLine();
Console.WriteLine(">>> Dê uns 2 segundos e confira as Configurações de Tela do Windows agora. <<<");
Console.WriteLine(">>> Deve aparecer um monitor novo (pode estar preto/sem imagem - é esperado). <<<");
Console.WriteLine();
Thread.Sleep(2000);
Console.WriteLine($"Monitores DEPOIS do Add: {GetSystemMetrics(SM_CMONITORS)}");
Console.WriteLine();
Console.WriteLine("Pressione ENTER para remover o monitor virtual e terminar.");
Console.ReadLine();

Vdd.RemoveDisplay(vdd, index);
Thread.Sleep(500);
Console.WriteLine($"Monitores DEPOIS do Remove: {GetSystemMetrics(SM_CMONITORS)}");

stopPing.Cancel();
try { await pingTask; } catch { }
Vdd.CloseDeviceHandle(vdd);
Console.WriteLine("Fim.");
return 0;
