using System.Collections.Concurrent;
using System.Globalization;
using Google.Protobuf;
using MonRem.Protocol.V1;

namespace MonRem.Host.Core.Devices;

/// <summary>
/// Descritores de I/O já recebidos, por modelo + hash (em memória e em disco), para o painel
/// não precisar reenviar o descritor a cada conexão.
/// </summary>
public sealed class DescriptorCache(string? directory)
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<Element>> _memory = new();

    public bool TryGet(string model, uint hash, out IReadOnlyList<Element> elements)
    {
        var key = Key(model, hash);
        if (_memory.TryGetValue(key, out elements!)) return true;
        if (directory is null) return false;

        var path = Path.Combine(directory, key + ".bin");
        if (!File.Exists(path)) return false;
        try
        {
            elements = [.. DeviceDescriptor.Parser.ParseFrom(File.ReadAllBytes(path)).Elements];
            _memory[key] = elements;
            return true;
        }
        catch (Exception ex) when (ex is InvalidProtocolBufferException or IOException)
        {
            return false;
        }
    }

    public void Put(string model, uint hash, IReadOnlyList<Element> elements)
    {
        var key = Key(model, hash);
        _memory[key] = elements;
        if (directory is null) return;

        Directory.CreateDirectory(directory);
        var all = new DeviceDescriptor { Page = 0, Pages = 1 };
        all.Elements.AddRange(elements);
        File.WriteAllBytes(Path.Combine(directory, key + ".bin"), all.ToByteArray());
    }

    private static string Key(string model, uint hash)
    {
        var safe = string.Concat(model.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        return $"{safe}-{hash.ToString("x8", CultureInfo.InvariantCulture)}";
    }
}

/// <summary>CRC-32 (IEEE 802.3, refletido, 0xEDB88320), o mesmo do esp_crc32_le do ESP-IDF.</summary>
public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    public static uint Compute(ReadOnlySpan<byte> data, uint crc = 0)
    {
        crc = ~crc;
        foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }
}
