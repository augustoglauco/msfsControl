// Spike de validação (ver tools/vdd-spike primeiro): confirma que dá para capturar o monitor
// virtual do parsec-vdd via Desktop Duplication (IDXGIOutputDuplication), igual a qualquer
// monitor físico, e salva um frame como BMP para prova visual.
//
// IMPORTANTE: não testei a compilação deste arquivo contra o pacote Vortice.Windows de verdade
// (minha sandbox não tem acesso de rede ao NuGet público). A API abaixo segue a convenção usual
// do Vortice (mesmo "shape" do SharpDX, do qual Vortice é sucessor direto), mas se o `dotnet run`
// der erro de compilação, me manda a mensagem exata — ajusto a assinatura certa.
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

const int TimeoutMs = 3000;

using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

IDXGIAdapter1? targetAdapter = null;
IDXGIOutput? targetOutput = null;

// Um monitor virtual IddCx (como o do parsec-vdd) não vira um adaptador DXGI próprio: ele se
// anexa como mais uma SAÍDA do adaptador da GPU real que está renderizando pra ele. Por isso a
// busca é por saída (via EnumDisplayDevicesA, que dá o nome "de verdade" do monitor), não por
// nome de adaptador.
//
// Confirmado num PC real: o monitor virtual do parsec-vdd aparece com DeviceString genérico
// ("Generic PnP Monitor"), não "Parsec" — o EDID virtual não se identifica. Então a busca por
// nome fica só como atalho (caso funcione em outra config), e o fallback é escolher na lista.
var candidates = new List<(IDXGIAdapter1 Adapter, IDXGIOutput Output, uint Index)>();

for (var i = 0u; factory.EnumAdapters1(i, out var adapter).Success; i++)
{
    var desc = adapter.Description1;
    Console.WriteLine($"Adaptador {i}: {desc.Description}");

    for (var j = 0u; adapter.EnumOutputs(j, out var output).Success; j++)
    {
        var od = output.Description;
        var w = od.DesktopCoordinates.Right - od.DesktopCoordinates.Left;
        var h = od.DesktopCoordinates.Bottom - od.DesktopCoordinates.Top;
        var monitorName = GetMonitorDeviceString(od.DeviceName);
        var listIndex = candidates.Count;
        Console.WriteLine($"  [{listIndex}] Output {j}: {od.DeviceName} ({monitorName})  {w}x{h}  anexado={od.AttachedToDesktop}");

        candidates.Add((adapter, output, j));

        if (targetOutput is null && monitorName.Contains("Parsec", StringComparison.OrdinalIgnoreCase))
        {
            targetAdapter = adapter;
            targetOutput = output;
        }
    }
}

if (targetAdapter is null || targetOutput is null)
{
    if (candidates.Count == 0)
    {
        Console.WriteLine();
        Console.WriteLine("Não encontrei nenhuma saída. Confirme que o monitor virtual está ligado");
        Console.WriteLine("(rode tools/vdd-spike e deixe ele esperando no ENTER) e tente de novo.");
        return 1;
    }

    Console.WriteLine();
    Console.WriteLine("Não encontrei uma saída com 'Parsec' no nome do monitor (normal — o monitor virtual");
    Console.WriteLine("do parsec-vdd se identifica como 'Generic PnP Monitor', não 'Parsec').");
    Console.WriteLine("Digite o número [N] da saída acima que é o monitor virtual (geralmente a de resolução");
    Console.WriteLine("diferente das suas telas reais) e aperte ENTER:");
    var typed = Console.ReadLine();
    if (!int.TryParse(typed, out var chosen) || chosen < 0 || chosen >= candidates.Count)
    {
        Console.WriteLine($"Entrada inválida: '{typed}'. Esperava um número entre 0 e {candidates.Count - 1}.");
        return 1;
    }

    (targetAdapter, targetOutput, _) = candidates[chosen];
}

Console.WriteLine();
Console.WriteLine($"Usando adaptador: {targetAdapter.Description1.Description}");

var result = D3D11.D3D11CreateDevice(targetAdapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
    null, out ID3D11Device device, out ID3D11DeviceContext context);
if (result.Failure)
{
    Console.WriteLine($"D3D11CreateDevice falhou: {result}");
    return 1;
}

using var output1 = targetOutput.QueryInterface<IDXGIOutput1>();
using var duplication = output1.DuplicateOutput(device);

// Diagnóstico: em vez de capturar só uma vez, captura repetidamente por alguns segundos.
// Objetivo: descobrir se é só o PRIMEIRO frame que vem zerado (bug de timing/estado inicial do
// IddCx) ou se TODOS os frames vêm zerados (indicando que o Desktop Duplication simplesmente não
// recebe dados reais desse output headless). Mexa o mouse ou mude algo na tela do monitor virtual
// enquanto isso roda.
const int SampleDurationMs = 6000;
const int SampleIntervalMs = 400;

Console.WriteLine();
Console.WriteLine($"Capturando por ~{SampleDurationMs / 1000}s. Mexa o mouse ou mude algo na tela do monitor escolhido agora.");
Console.WriteLine();

var deadline = Environment.TickCount64 + SampleDurationMs;
var attempt = 0;
var savedNonZero = false;
string? lastSavedPath = null;

while (Environment.TickCount64 < deadline)
{
    attempt++;
    var acquire = duplication.AcquireNextFrame(TimeoutMs, out var frameInfo, out var resource);
    if (acquire.Failure)
    {
        Console.WriteLine($"Tentativa {attempt}: AcquireNextFrame falhou: {acquire}");
        Thread.Sleep(SampleIntervalMs);
        continue;
    }

    using (resource)
    using (var texture = resource.QueryInterface<ID3D11Texture2D>())
    {
        var desc = texture.Description;
        var stagingDesc = new Texture2DDescription
        {
            Width = desc.Width,
            Height = desc.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = desc.Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        };
        using var staging = device.CreateTexture2D(stagingDesc);
        context.CopyResource(staging, texture);

        var map = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var (min, max, nonZero, total) = SampleStats(map.DataPointer, map.RowPitch, desc.Width, desc.Height);
            Console.WriteLine($"Tentativa {attempt}: AccumulatedFrames={frameInfo.AccumulatedFrames} LastPresentTime={frameInfo.LastPresentTime} " +
                $"min={min} max={max} nãozero={nonZero}/{total} ({100.0 * nonZero / total:F1}%)");

            if (nonZero > 0 && !savedNonZero)
            {
                savedNonZero = true;
                lastSavedPath = Path.Combine(AppContext.BaseDirectory, "capture.bmp");
                SaveBmp(lastSavedPath, map.DataPointer, map.RowPitch, desc.Width, desc.Height);
                Console.WriteLine($"  -> Primeiro frame com conteúdo de verdade! Salvo em: {lastSavedPath}");
            }
            else if (!savedNonZero)
            {
                // Ainda não achou nada não-zero: salva mesmo assim (sobrescrevendo), só pra ter
                // sempre um arquivo recente pra inspecionar se a janela de tempo acabar sem sucesso.
                lastSavedPath = Path.Combine(AppContext.BaseDirectory, "capture.bmp");
                SaveBmp(lastSavedPath, map.DataPointer, map.RowPitch, desc.Width, desc.Height);
            }
        }
        finally
        {
            context.Unmap(staging, 0);
        }
    }
    duplication.ReleaseFrame();
    Thread.Sleep(SampleIntervalMs);
}

Console.WriteLine();
if (savedNonZero)
{
    Console.WriteLine($"Sucesso: pelo menos um frame teve conteúdo real. Confira: {lastSavedPath}");
}
else
{
    Console.WriteLine("Nenhuma das tentativas teve conteúdo não-zero — o Desktop Duplication não está");
    Console.WriteLine("recebendo dados reais desse output em nenhum momento, não é só o primeiro frame.");
    Console.WriteLine($"(Último frame (zerado) ainda foi salvo em: {lastSavedPath}, pra referência.)");
}

return 0;

// Amostra algumas linhas (topo, meio, fim) do buffer capturado e retorna min/max/bytes não-zero.
// Se isso vier tudo zero, o problema é na captura (GPU/duplication); se vier com valores de
// verdade, o problema seria no arquivo BMP ou em como está sendo visualizado.
static (byte Min, byte Max, long NonZero, long Total) SampleStats(IntPtr dataPointer, uint rowPitch, uint width, uint height)
{
    var row = new byte[width * 4];
    var sampleRows = new[] { 0u, height / 2, height - 1 };
    byte min = 255, max = 0;
    long nonZero = 0, total = 0;

    foreach (var y in sampleRows)
    {
        Marshal.Copy(dataPointer + (int)(y * rowPitch), row, 0, row.Length);
        foreach (var b in row)
        {
            if (b < min) min = b;
            if (b > max) max = b;
            if (b != 0) nonZero++;
            total++;
        }
    }

    return (min, max, nonZero, total);
}

// Nome "de verdade" do monitor ligado a uma saída DXGI (ex.: "\\.\DISPLAY4"), via EnumDisplayDevicesA.
// Para um monitor virtual IddCx, isso é o que efetivamente identifica o driver (DeviceName do
// adaptador DXGI é só "\\.\DISPLAYn", não diz nada sobre quem está por trás).
static string GetMonitorDeviceString(string adapterDeviceName)
{
    var device = new DISPLAY_DEVICEA { cb = Marshal.SizeOf<DISPLAY_DEVICEA>() };
    if (EnumDisplayDevicesA(adapterDeviceName, 0, ref device, 0))
        return device.DeviceString;
    return "(desconhecido)";
}

[DllImport("user32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
static extern bool EnumDisplayDevicesA(string lpDevice, uint iDevNum, ref DISPLAY_DEVICEA lpDisplayDevice, uint dwFlags);

// BMP 32bpp sem compressão, a partir de um buffer BGRA linha a linha (respeitando o RowPitch,
// que pode ter padding além de width*4). Evita depender de qualquer lib de imagem.
static void SaveBmp(string path, IntPtr dataPointer, uint rowPitch, uint width, uint height)
{
    const int headerSize = 54;
    var imageSize = (int)(width * height * 4);
    using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
    using var w = new BinaryWriter(fs);

    w.Write((byte)'B'); w.Write((byte)'M');
    w.Write(headerSize + imageSize);
    w.Write(0); w.Write(headerSize);
    // Altura positiva = BMP bottom-up (convenção clássica). Tentei top-down (altura negativa)
    // antes e a imagem saía toda preta em mais de um visualizador — bottom-up é o formato que
    // todo leitor de BMP (até os mais simples/antigos) sabe decodificar sem ambiguidade.
    w.Write(40); w.Write((int)width); w.Write((int)height);
    w.Write((short)1); w.Write((short)32);
    w.Write(0); w.Write(imageSize);
    w.Write(0); w.Write(0); w.Write(0); w.Write(0);

    var row = new byte[width * 4];
    // Bottom-up: a primeira linha gravada no arquivo é a de baixo da imagem original. O buffer de
    // origem (dataPointer) está em ordem top-down (linha 0 = topo da tela), então percorremos a
    // origem de baixo para cima.
    for (var fileRow = 0u; fileRow < height; fileRow++)
    {
        var y = height - 1 - fileRow;
        System.Runtime.InteropServices.Marshal.Copy(dataPointer + (int)(y * rowPitch), row, 0, row.Length);

        // O 4º byte de cada pixel (alfa) vem do buffer interno do compositor (Desktop Duplication),
        // não é alfa "de exibição" de verdade — pode vir com valores baixos/zero em partes da
        // imagem. Alguns visualizadores (ex.: app Fotos do Windows) tratam esse byte como alfa real
        // e compõem sobre fundo preto, fazendo a imagem parecer toda preta mesmo com conteúdo
        // correto nos canais B/G/R. Forçando opaco (255) aqui evita esse efeito.
        for (var x = 3; x < row.Length; x += 4)
            row[x] = 255;

        w.Write(row);
    }
}

// Declarações de tipo num arquivo de top-level statements têm que vir depois de todo o resto.
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
struct DISPLAY_DEVICEA
{
    public int cb;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
    public uint StateFlags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
}
