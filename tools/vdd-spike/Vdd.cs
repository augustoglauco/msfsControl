// Port mínimo de core/parsec-vdd.h (nomi-san/parsec-vdd, licença BSD) para C#/.NET.
// Só o necessário para este spike: abrir o driver, pedir versão, ligar/desligar um monitor
// virtual e mandar o ping de keep-alive. Não é destinado a entrar no host como está — é só
// para validar a hipótese antes de desenhar a integração de verdade.
using System.Runtime.InteropServices;

namespace VddSpike;

internal static class Vdd
{
    public static readonly Guid AdapterGuid = new("00b41627-04c4-429e-a26e-0265cf50c8fa");
    public static readonly Guid ClassGuid = new("4d36e968-e325-11ce-bfc1-08002be10318");
    public const string HardwareId = "Root\\Parsec\\VDA";

    private const uint VDD_IOCTL_ADD = 0x0022e004;
    private const uint VDD_IOCTL_REMOVE = 0x0022a008;
    private const uint VDD_IOCTL_UPDATE = 0x0022a00c;
    private const uint VDD_IOCTL_VERSION = 0x0022e010;

    private const uint DIGCF_PRESENT = 0x02;
    private const uint DIGCF_DEVICEINTERFACE = 0x10;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x1;
    private const uint FILE_SHARE_WRITE = 0x2;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    private const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
    private const uint FILE_FLAG_WRITE_THROUGH = 0x80000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OVERLAPPED
    {
        public IntPtr Internal;
        public IntPtr InternalHigh;
        public uint Offset;
        public uint OffsetHigh;
        public IntPtr hEvent;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsA(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetailA(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData, int deviceInterfaceDetailDataSize, out int requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr CreateFileA(string fileName, uint access, uint share, IntPtr securityAttributes, uint creationDisposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateEventA(IntPtr securityAttributes, bool manualReset, bool initialState, IntPtr name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool DeviceIoControl(IntPtr device, uint ioControlCode, byte* inBuffer, uint inBufferSize, byte* outBuffer, uint outBufferSize, IntPtr bytesReturned, ref OVERLAPPED overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(IntPtr device, ref OVERLAPPED overlapped, out uint bytesTransferred, bool wait);

    /// <summary>Abre o device interface do adaptador parsec-vdd. Lança se o driver não estiver instalado/ligado.</summary>
    public static IntPtr OpenDeviceHandle(Guid interfaceGuid)
    {
        var guid = interfaceGuid;
        var deviceInfoSet = SetupDiGetClassDevsA(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (deviceInfoSet == IntPtr.Zero || deviceInfoSet == new IntPtr(-1))
            throw new InvalidOperationException($"SetupDiGetClassDevsA falhou (erro {Marshal.GetLastWin32Error()}). O driver parsec-vdd está instalado?");

        try
        {
            var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            if (!SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref guid, 0, ref data))
                throw new InvalidOperationException($"Nenhuma interface de dispositivo encontrada para o GUID do adaptador (erro {Marshal.GetLastWin32Error()}). O driver parsec-vdd está instalado e habilitado?");

            SetupDiGetDeviceInterfaceDetailA(deviceInfoSet, ref data, IntPtr.Zero, 0, out var requiredSize, IntPtr.Zero);
            var buffer = Marshal.AllocHGlobal(requiredSize);
            try
            {
                // cbSize do SP_DEVICE_INTERFACE_DETAIL_DATA é o tamanho só do campo DWORD inicial
                // mais o padding de alinhamento até o primeiro char do caminho (o "número mágico"
                // clássico do interop com esta struct: 6 em x86, 8 em x64).
                Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                if (!SetupDiGetDeviceInterfaceDetailA(deviceInfoSet, ref data, buffer, requiredSize, out _, IntPtr.Zero))
                    throw new InvalidOperationException($"SetupDiGetDeviceInterfaceDetailA falhou (erro {Marshal.GetLastWin32Error()}).");

                var devicePath = Marshal.PtrToStringAnsi(buffer + 4)
                    ?? throw new InvalidOperationException("Caminho do dispositivo vazio.");

                var handle = CreateFileA(devicePath, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                    IntPtr.Zero, OPEN_EXISTING,
                    FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OVERLAPPED | FILE_FLAG_NO_BUFFERING | FILE_FLAG_WRITE_THROUGH,
                    IntPtr.Zero);
                if (handle == IntPtr.Zero || handle == new IntPtr(-1))
                    throw new InvalidOperationException($"CreateFileA em '{devicePath}' falhou (erro {Marshal.GetLastWin32Error()}).");
                return handle;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    public static void CloseDeviceHandle(IntPtr handle)
    {
        if (handle != IntPtr.Zero && handle != new IntPtr(-1)) CloseHandle(handle);
    }

    private static unsafe int IoControl(IntPtr vdd, uint code, byte[]? data = null)
    {
        var inBuffer = new byte[32];
        if (data is not null) Array.Copy(data, inBuffer, Math.Min(data.Length, inBuffer.Length));
        var outBuffer = new byte[4];

        var evt = CreateEventA(IntPtr.Zero, true, false, IntPtr.Zero);
        var overlapped = new OVERLAPPED { hEvent = evt };
        try
        {
            fixed (byte* inPtr = inBuffer)
            fixed (byte* outPtr = outBuffer)
            {
                DeviceIoControl(vdd, code, inPtr, (uint)inBuffer.Length, outPtr, (uint)outBuffer.Length, IntPtr.Zero, ref overlapped);
                if (!GetOverlappedResult(vdd, ref overlapped, out _, true))
                    throw new InvalidOperationException($"DeviceIoControl (code=0x{code:x8}) falhou (erro {Marshal.GetLastWin32Error()}).");
            }
            return BitConverter.ToInt32(outBuffer, 0);
        }
        finally
        {
            CloseHandle(evt);
        }
    }

    public static int Version(IntPtr vdd) => IoControl(vdd, VDD_IOCTL_VERSION);
    public static void Update(IntPtr vdd) => IoControl(vdd, VDD_IOCTL_UPDATE);

    public static int AddDisplay(IntPtr vdd)
    {
        var index = IoControl(vdd, VDD_IOCTL_ADD);
        Update(vdd);
        return index;
    }

    public static void RemoveDisplay(IntPtr vdd, int index)
    {
        // index vai como UInt16 big-endian (ver VddRemoveDisplay no header original).
        ushort indexData = (ushort)(((index & 0xFF) << 8) | ((index >> 8) & 0xFF));
        IoControl(vdd, VDD_IOCTL_REMOVE, BitConverter.GetBytes(indexData));
        Update(vdd);
    }
}
