using MonRem.Protocol;
using MonRem.Protocol.V1;

namespace MonRem.Host.Core.Devices;

/// <summary>Um enlace com um painel: USB serial ou TCP/TLS.</summary>
public interface IDeviceLink : IAsyncDisposable
{
    /// <summary>Porta ou endereço, para log e para a interface ("COM5", "192.168.1.40").</summary>
    string Description { get; }

    DeviceTransport Transport { get; }

    /// <summary>Próximo quadro válido; null quando o enlace fecha.</summary>
    ValueTask<DeviceFrame?> ReadAsync(CancellationToken ct);

    ValueTask WriteAsync(DeviceFrame frame, CancellationToken ct);
}

/// <summary>Enlace serial (COBS + CRC-16) sobre qualquer Stream: SerialPort.BaseStream, pipe de teste.</summary>
public sealed class SerialDeviceLink(Stream stream, string description) : IDeviceLink
{
    private readonly DeviceSerialReader _reader = new(stream);
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public string Description { get; } = description;
    public DeviceTransport Transport => DeviceTransport.Usb;
    public long DroppedFrames => _reader.DroppedFrames;

    public ValueTask<DeviceFrame?> ReadAsync(CancellationToken ct) => _reader.ReadAsync(ct);

    public async ValueTask WriteAsync(DeviceFrame frame, CancellationToken ct)
    {
        var bytes = DeviceSerialFraming.Encode(frame);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await stream.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
    }
}
