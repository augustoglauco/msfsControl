// Wrapper mínimo para o SudoVDA (driver/sudovda, vendorizado como submódulo a partir de
// https://github.com/SudoMaker/SudoVDA). TODOS os GUIDs, códigos de IOCTL e layouts de struct
// abaixo foram lidos direto do fonte do driver, não inventados:
//   driver/sudovda/Common/Include/sudovda-ioctl.h  (IOCTLs, GUIDs, structs)
// Os códigos de IOCTL são CTL_CODE(FILE_DEVICE_UNKNOWN=0x22, Function, METHOD_BUFFERED=0,
// FILE_ANY_ACCESS=0) = (0x22 << 16) | (Function << 2); calculados aqui em vez de macro, mas
// conferidos batendo os números com o header (ver comentário em cada constante).
using System.Runtime.InteropServices;

namespace SudovdaSpike;

internal static class Sudovda
{
    // Common/Include/AdapterOption.h: "DO NOT CHANGE" — classe padrão "Display adapters"
    // ({4d36e968-e325-11ce-bfc1-08002be10318}, a mesma classe usada pelo parsec-vdd — é a classe
    // genérica do Windows, não algo específico do SudoVDA) e GUID de interface do SudoVDA.
    public static readonly Guid ClassGuid = new("4d36e968-e325-11ce-bfc1-08002be10318");
    public static readonly Guid InterfaceGuid = new("e5bcc234-1e0c-418a-a0d4-ef8b7501414d");
    public const string HardwareId = "Root\\SudoMaker\\SudoVDA";

    // sudovda-ioctl.h: CTL_CODE(FILE_DEVICE_UNKNOWN, 0x8xx, METHOD_BUFFERED, FILE_ANY_ACCESS)
    private const uint IOCTL_ADD_VIRTUAL_DISPLAY = 0x00222000;    // Function 0x800
    private const uint IOCTL_REMOVE_VIRTUAL_DISPLAY = 0x00222004; // Function 0x801
    private const uint IOCTL_GET_WATCHDOG = 0x0022200C;           // Function 0x803
    private const uint IOCTL_DRIVER_PING = 0x00222220;            // Function 0x888
    private const uint IOCTL_GET_PROTOCOL_VERSION = 0x002223FC;   // Function 0x8FF

    private const uint DIGCF_PRESENT = 0x02;
    private const uint DIGCF_DEVICEINTERFACE = 0x10;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x1;
    private const uint FILE_SHARE_WRITE = 0x2;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;

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

    /// <summary>Abre o device interface do SudoVDA. Lança se o driver não estiver instalado/ligado.</summary>
    public static IntPtr OpenDeviceHandle()
    {
        var guid = InterfaceGuid;
        var deviceInfoSet = SetupDiGetClassDevsA(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (deviceInfoSet == IntPtr.Zero || deviceInfoSet == new IntPtr(-1))
            throw new InvalidOperationException($"SetupDiGetClassDevsA falhou (erro {Marshal.GetLastWin32Error()}). O driver SudoVDA está instalado?");

        try
        {
            var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            if (!SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref guid, 0, ref data))
                throw new InvalidOperationException($"Nenhuma interface de dispositivo encontrada para {InterfaceGuid} (erro {Marshal.GetLastWin32Error()}). O SudoVDA está instalado e ligado (ver driver/README.md — Fase 0)?");

            SetupDiGetDeviceInterfaceDetailA(deviceInfoSet, ref data, IntPtr.Zero, 0, out var requiredSize, IntPtr.Zero);
            var buffer = Marshal.AllocHGlobal(requiredSize);
            try
            {
                Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                if (!SetupDiGetDeviceInterfaceDetailA(deviceInfoSet, ref data, buffer, requiredSize, out _, IntPtr.Zero))
                    throw new InvalidOperationException($"SetupDiGetDeviceInterfaceDetailA falhou (erro {Marshal.GetLastWin32Error()}).");

                var devicePath = Marshal.PtrToStringAnsi(buffer + 4)
                    ?? throw new InvalidOperationException("Caminho do dispositivo vazio.");

                var handle = CreateFileA(devicePath, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                    IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OVERLAPPED, IntPtr.Zero);
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

    private static unsafe byte[] IoControl(IntPtr dev, uint code, byte[]? input, int outputSize)
    {
        var inBuffer = input ?? [];
        var outBuffer = new byte[Math.Max(outputSize, 1)];

        var evt = CreateEventA(IntPtr.Zero, true, false, IntPtr.Zero);
        var overlapped = new OVERLAPPED { hEvent = evt };
        try
        {
            fixed (byte* inPtr = inBuffer)
            fixed (byte* outPtr = outBuffer)
            {
                DeviceIoControl(dev, code, inPtr, (uint)inBuffer.Length, outPtr, (uint)outBuffer.Length, IntPtr.Zero, ref overlapped);
                if (!GetOverlappedResult(dev, ref overlapped, out var bytesTransferred, true))
                    throw new InvalidOperationException($"DeviceIoControl (code=0x{code:x8}) falhou (erro {Marshal.GetLastWin32Error()}).");
                return outBuffer[..(int)Math.Min(bytesTransferred, (uint)outputSize)];
            }
        }
        finally
        {
            CloseHandle(evt);
        }
    }

    /// <summary>IOCTL_DRIVER_PING — reseta o watchdog do driver (default 3s, ver sudovda-ioctl.h / Driver.cpp).</summary>
    public static void Ping(IntPtr dev) => IoControl(dev, IOCTL_DRIVER_PING, null, 0);

    public static (byte Major, byte Minor, byte Incremental, bool TestBuild) GetProtocolVersion(IntPtr dev)
    {
        var o = IoControl(dev, IOCTL_GET_PROTOCOL_VERSION, null, 4);
        return (o[0], o[1], o[2], o[3] != 0);
    }

    /// <summary>
    /// IOCTL_ADD_VIRTUAL_DISPLAY. Layout de VIRTUAL_DISPLAY_ADD_PARAMS (56 bytes):
    /// UINT Width; UINT Height; UINT RefreshRate; GUID MonitorGuid; CHAR DeviceName[14]; CHAR SerialNumber[14].
    /// Retorna VIRTUAL_DISPLAY_ADD_OUT (12 bytes): LUID AdapterLuid; UINT TargetId.
    /// </summary>
    public static (long AdapterLuid, uint TargetId) AddDisplay(IntPtr dev, uint width, uint height, uint refreshHz, Guid monitorGuid, string deviceName, string serialNumber)
    {
        var buf = new byte[56];
        BitConverter.GetBytes(width).CopyTo(buf, 0);
        BitConverter.GetBytes(height).CopyTo(buf, 4);
        BitConverter.GetBytes(refreshHz).CopyTo(buf, 8);
        monitorGuid.ToByteArray().CopyTo(buf, 12);
        WriteFixedAscii(deviceName, buf, 28, 14);
        WriteFixedAscii(serialNumber, buf, 42, 14);

        var outBuf = IoControl(dev, IOCTL_ADD_VIRTUAL_DISPLAY, buf, 12);
        var adapterLuid = BitConverter.ToInt64(outBuf, 0); // LUID = 2x DWORD = 8 bytes
        var targetId = BitConverter.ToUInt32(outBuf, 8);
        return (adapterLuid, targetId);
    }

    /// <summary>IOCTL_REMOVE_VIRTUAL_DISPLAY. VIRTUAL_DISPLAY_REMOVE_PARAMS (16 bytes): GUID MonitorGuid.</summary>
    public static void RemoveDisplay(IntPtr dev, Guid monitorGuid) =>
        IoControl(dev, IOCTL_REMOVE_VIRTUAL_DISPLAY, monitorGuid.ToByteArray(), 0);

    private static void WriteFixedAscii(string s, byte[] buf, int offset, int fieldLen)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(s);
        var n = Math.Min(bytes.Length, fieldLen - 1); // deixa ao menos 1 byte \0 final
        Array.Copy(bytes, 0, buf, offset, n);
    }
}
