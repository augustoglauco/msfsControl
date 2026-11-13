// Spike de validação (ver tools/vdd-spike e tools/capture-spike primeiro): testa se a API
// Windows.Graphics.Capture (WGC) consegue capturar o monitor virtual onde o Desktop Duplication
// cru (IDXGIOutputDuplication) falhou — testado com dois drivers diferentes (parsec-vdd e
// Virtual Display Driver), sempre com buffer zerado e depois DXGI_ERROR_ACCESS_LOST (ver
// tools/capture-spike/README.md pro histórico completo). WGC é a API mais nova/alto-nível,
// usada pelo Xbox Game Bar, Discord e Teams, e ainda devolve texturas D3D11 (sem cair pra GDI).
//
// IMPORTANTE: não consegui compilar nem rodar este arquivo de verdade (minha sandbox não tem
// Windows nem acesso ao NuGet público). Verifiquei os GUIDs e assinaturas mais arriscados
// (IGraphicsCaptureItemInterop, IDirect3DDxgiInterfaceAccess, CreateDirect3D11DeviceFromDXGIDevice,
// RoGetActivationFactory) direto em headers/documentação oficial da Microsoft, não inventei eles.
// A parte que ainda pode estar errada é como envolver o ponteiro COM final de volta num tipo do
// Vortice (ID3D11Texture2D) — se o `dotnet run` der erro de compilação ou de marshaling bem aqui
// no fim, me manda a mensagem exata.
//
// (Já corrigido uma vez: [MarshalAs(UnmanagedType.HString)] direto num parâmetro de DllImport dá
// MarshalDirectiveException — o HSTRING precisa ser criado/destruído manualmente via
// WindowsCreateString/WindowsDeleteString antes de chamar RoGetActivationFactory.)
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.Direct3D;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

const int SampleDurationMs = 6000;
const int SampleIntervalMs = 200;

// 1) Lista os monitores via GDI (EnumDisplayMonitors) pra escolher qual HMONITOR capturar.
//    WGC trabalha com HMONITOR, não com saída DXGI — é um nível de abstração diferente do
//    capture-spike anterior.
var monitors = new List<(IntPtr HMonitor, string DeviceName, int Width, int Height)>();

EnumDisplayMonitorsDelegate callback = (IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
{
    var info = new MONITORINFOEXA { cbSize = Marshal.SizeOf<MONITORINFOEXA>() };
    if (GetMonitorInfoA(hMonitor, ref info))
    {
        var w = rect.Right - rect.Left;
        var h = rect.Bottom - rect.Top;
        monitors.Add((hMonitor, info.szDevice, w, h));
    }
    return true;
};

EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);

for (var i = 0; i < monitors.Count; i++)
{
    var m = monitors[i];
    Console.WriteLine($"[{i}] {m.DeviceName}  {m.Width}x{m.Height}");
}

Console.WriteLine();
Console.WriteLine("Digite o número do monitor a capturar (o virtual, geralmente a resolução diferente");
Console.WriteLine("das suas telas reais) e aperte ENTER:");
var typed = Console.ReadLine();
if (!int.TryParse(typed, out var chosen) || chosen < 0 || chosen >= monitors.Count)
{
    Console.WriteLine($"Entrada inválida: '{typed}'.");
    return 1;
}

var hmon = monitors[chosen].HMonitor;

// 2) Device D3D11 (adaptador padrão — WGC gerencia a composição entre adaptadores internamente).
var result = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
    null, out ID3D11Device device, out ID3D11DeviceContext context);
if (result.Failure)
{
    Console.WriteLine($"D3D11CreateDevice falhou: {result}");
    return 1;
}

using var dxgiDevice = device.QueryInterface<IDXGIDevice>();

// 3) ID3D11Device -> IDirect3DDevice (WinRT), via CreateDirect3D11DeviceFromDXGIDevice (d3d11.dll).
//    Confirmado via IDL oficial: recebe IDXGIDevice*, devolve IInspectable** (não um ID3D11Device*
//    direto) — por isso o WinRT.MarshalInterface<IDirect3DDevice>.FromAbi depois.
if (CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var graphicsDevicePtr) != 0)
{
    Console.WriteLine("CreateDirect3D11DeviceFromDXGIDevice falhou.");
    return 1;
}
var direct3DDevice = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(graphicsDevicePtr);

// 4) HMONITOR -> GraphicsCaptureItem, via RoGetActivationFactory + IGraphicsCaptureItemInterop.
//    GraphicsCaptureItem não expõe CreateForMonitor na projeção pública do C# — só existe nessa
//    interface de interop nativa (confirmado no header oficial da Microsoft).
//
//    [MarshalAs(UnmanagedType.HString)] direto num parâmetro de DllImport não é suportado pelo
//    marshaler do .NET fora do mecanismo do CsWinRT (deu MarshalDirectiveException) — então o
//    HSTRING é criado/destruído manualmente via WindowsCreateString/WindowsDeleteString
//    (confirmado no código gerado oficial do .NET, source.dot.net).
const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
if (WindowsCreateString(className, (uint)className.Length, out var classNameHandle) != 0)
{
    Console.WriteLine("WindowsCreateString falhou.");
    return 1;
}

var interopIid = typeof(IGraphicsCaptureItemInterop).GUID;
int hr;
IntPtr factoryPtr;
try
{
    hr = RoGetActivationFactory(classNameHandle, ref interopIid, out factoryPtr);
}
finally
{
    WindowsDeleteString(classNameHandle);
}

if (hr != 0)
{
    Console.WriteLine($"RoGetActivationFactory falhou pra {className}: 0x{hr:X8}");
    return 1;
}
var interop = (IGraphicsCaptureItemInterop)Marshal.GetTypedObjectForIUnknown(factoryPtr, typeof(IGraphicsCaptureItemInterop));
var itemIid = typeof(GraphicsCaptureItem).GUID;
// Assinatura explícita (HRESULT + out IntPtr), sem depender da conversão implícita "promove o
// último parâmetro de saída pro retorno" — essa deu InvalidCastException no .NET aqui.
var createHr = interop.CreateForMonitor(hmon, ref itemIid, out var itemPtr);
if (createHr != 0)
{
    Console.WriteLine($"IGraphicsCaptureItemInterop.CreateForMonitor falhou: 0x{createHr:X8}");
    return 1;
}
var item = WinRT.MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPtr);

Console.WriteLine($"GraphicsCaptureItem criado: {item.DisplayName}, tamanho {item.Size.Width}x{item.Size.Height}");

// 5) Pool de frames + sessão de captura.
using var framePool = Direct3D11CaptureFramePool.Create(
    direct3DDevice,
    DirectXPixelFormat.B8G8R8A8UIntNormalized,
    2,
    item.Size);
using var session = framePool.CreateCaptureSession(item);
session.StartCapture();

Console.WriteLine();
Console.WriteLine($"Capturando por ~{SampleDurationMs / 1000}s via Windows.Graphics.Capture. Mexa o mouse ou mude algo na tela escolhida agora.");
Console.WriteLine();

var deadline = Environment.TickCount64 + SampleDurationMs;
var attempt = 0;
var savedNonZero = false;
string? lastSavedPath = null;

while (Environment.TickCount64 < deadline)
{
    attempt++;
    using var frame = framePool.TryGetNextFrame();
    if (frame is null)
    {
        Console.WriteLine($"Tentativa {attempt}: nenhum frame disponível ainda.");
        Thread.Sleep(SampleIntervalMs);
        continue;
    }

    var access = (IDirect3DDxgiInterfaceAccess)(object)frame.Surface;
    var textureIid = typeof(ID3D11Texture2D).GUID;
    var getHr = access.GetInterface(ref textureIid, out var texturePtr);
    if (getHr != 0)
    {
        Console.WriteLine($"Tentativa {attempt}: IDirect3DDxgiInterfaceAccess.GetInterface falhou: 0x{getHr:X8}");
        Thread.Sleep(SampleIntervalMs);
        continue;
    }
    using var texture = new ID3D11Texture2D(texturePtr);

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
        Console.WriteLine($"Tentativa {attempt}: {desc.Width}x{desc.Height} formato {desc.Format} " +
            $"min={min} max={max} nãozero={nonZero}/{total} ({100.0 * nonZero / total:F1}%)");

        if (nonZero > 0 && !savedNonZero)
        {
            savedNonZero = true;
            lastSavedPath = Path.Combine(AppContext.BaseDirectory, "capture-wgc.bmp");
            SaveBmp(lastSavedPath, map.DataPointer, map.RowPitch, desc.Width, desc.Height);
            Console.WriteLine($"  -> Frame com conteúdo de verdade! Salvo em: {lastSavedPath}");
        }
        else if (!savedNonZero)
        {
            lastSavedPath = Path.Combine(AppContext.BaseDirectory, "capture-wgc.bmp");
            SaveBmp(lastSavedPath, map.DataPointer, map.RowPitch, desc.Width, desc.Height);
        }
    }
    finally
    {
        context.Unmap(staging, 0);
    }

    Thread.Sleep(SampleIntervalMs);
}

session.Dispose();

Console.WriteLine();
if (savedNonZero)
{
    Console.WriteLine($"Sucesso: Windows.Graphics.Capture trouxe conteúdo real. Confira: {lastSavedPath}");
}
else
{
    Console.WriteLine("Nenhuma tentativa teve conteúdo não-zero via Windows.Graphics.Capture também.");
    Console.WriteLine($"(Último frame (zerado) salvo em: {lastSavedPath}, pra referência.)");
}

return 0;

// Amostra algumas linhas (topo, meio, fim) do buffer capturado e retorna min/max/bytes não-zero.
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

// BMP 32bpp bottom-up sem compressão (ver tools/capture-spike pro histórico de por que bottom-up
// e por que forçar o canal alfa pra 255).
static void SaveBmp(string path, IntPtr dataPointer, uint rowPitch, uint width, uint height)
{
    const int headerSize = 54;
    var imageSize = (int)(width * height * 4);
    using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
    using var w = new BinaryWriter(fs);

    w.Write((byte)'B'); w.Write((byte)'M');
    w.Write(headerSize + imageSize);
    w.Write(0); w.Write(headerSize);
    w.Write(40); w.Write((int)width); w.Write((int)height);
    w.Write((short)1); w.Write((short)32);
    w.Write(0); w.Write(imageSize);
    w.Write(0); w.Write(0); w.Write(0); w.Write(0);

    var row = new byte[width * 4];
    for (var fileRow = 0u; fileRow < height; fileRow++)
    {
        var y = height - 1 - fileRow;
        Marshal.Copy(dataPointer + (int)(y * rowPitch), row, 0, row.Length);
        for (var x = 3; x < row.Length; x += 4)
            row[x] = 255;
        w.Write(row);
    }
}

// --- Interop verificado contra headers/documentação oficial da Microsoft (ver comentário no topo
// do arquivo) --------------------------------------------------------------------------------
//
// Num arquivo de top-level statements, TODAS as declarações de função local (mesmo `extern`)
// têm que vir antes de QUALQUER declaração de tipo (delegate/interface/struct) — não só as
// structs, como já tinha acontecido no capture-spike (CS8803). Por isso as funções `extern`
// ficam juntas aqui, e os tipos (delegate, interfaces, structs) vão todos pro fim do arquivo.

[DllImport("user32.dll")]
static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, EnumDisplayMonitorsDelegate lpfnEnum, IntPtr dwData);

[DllImport("user32.dll", CharSet = CharSet.Ansi)]
static extern bool GetMonitorInfoA(IntPtr hMonitor, ref MONITORINFOEXA lpmi);

// RoGetActivationFactory(HSTRING, REFIID, out void*) — confirmado em roapi.h / Combase.dll.
// Recebe o HSTRING já criado manualmente (ver WindowsCreateString abaixo), não uma string gerenciada.
[DllImport("combase.dll")]
static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

// WindowsCreateString/WindowsDeleteString — confirmado em winstring.h / código gerado oficial do
// .NET (source.dot.net). HSTRING tratado como IntPtr aqui (é só um handle opaco).
[DllImport("api-ms-win-core-winrt-string-l1-1-0.dll", ExactSpelling = true)]
static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string sourceString, uint length, out IntPtr hstring);

[DllImport("api-ms-win-core-winrt-string-l1-1-0.dll", ExactSpelling = true)]
static extern int WindowsDeleteString(IntPtr hstring);

// CreateDirect3D11DeviceFromDXGIDevice(IDXGIDevice*, out IInspectable**) — confirmado em
// windows.graphics.directx.direct3d11.interop.idl. Exportada por d3d11.dll.
[DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice")]
static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

// Declarações de tipo (têm que vir depois de toda função local, inclusive as extern acima).
delegate bool EnumDisplayMonitorsDelegate(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

// IGraphicsCaptureItemInterop — GUID confirmado em windows.graphics.capture.interop.h.
// Assinaturas explícitas (HRESULT + out IntPtr) em vez de deixar o marshaler "promover" o último
// parâmetro de saída pro retorno — essa conversão implícita deu InvalidCastException no .NET aqui.
[ComImport]
[Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IGraphicsCaptureItemInterop
{
    [PreserveSig] int CreateForWindow(IntPtr window, ref Guid iid, out IntPtr result);
    [PreserveSig] int CreateForMonitor(IntPtr monitor, ref Guid iid, out IntPtr result);
}

// IDirect3DDxgiInterfaceAccess — GUID confirmado em windows.graphics.directx.direct3d11.interop.idl.
[ComImport]
[Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IDirect3DDxgiInterfaceAccess
{
    [PreserveSig] int GetInterface(ref Guid iid, out IntPtr ppv);
}

[StructLayout(LayoutKind.Sequential)]
struct RECT
{
    public int Left, Top, Right, Bottom;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
struct MONITORINFOEXA
{
    public int cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
}
