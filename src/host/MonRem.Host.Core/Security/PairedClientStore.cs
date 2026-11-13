using System.Text.Json;
using MonRem.Protocol.V1;

namespace MonRem.Host.Core.Security;

public sealed record PairedClientRecord(
    string ClientId,
    string DeviceName,
    Platform Platform,
    string CertFingerprint,
    DateTimeOffset PairedAt,
    DateTimeOffset LastSeen);

/// <summary>Clientes pareados (cert fixado por SHA-256), persistidos em JSON.</summary>
public sealed class PairedClientStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, PairedClientRecord> _byClientId;

    public PairedClientStore(string path)
    {
        _path = path;
        _byClientId = File.Exists(path)
            ? (JsonSerializer.Deserialize<List<PairedClientRecord>>(File.ReadAllText(path), Json) ?? [])
                .ToDictionary(r => r.ClientId)
            : [];
    }

    public PairedClientRecord? FindByFingerprint(string fingerprint)
    {
        lock (_lock)
            return _byClientId.Values.FirstOrDefault(r => r.CertFingerprint == fingerprint);
    }

    public PairedClientRecord? FindByClientId(string clientId)
    {
        lock (_lock)
            return _byClientId.GetValueOrDefault(clientId);
    }

    public IReadOnlyList<PairedClientRecord> List()
    {
        lock (_lock)
            return [.. _byClientId.Values.OrderBy(r => r.DeviceName)];
    }

    /// <summary>Grava ou substitui (re-pareamento com outro cert troca o anterior).</summary>
    public void Upsert(PairedClientRecord record)
    {
        lock (_lock)
        {
            _byClientId[record.ClientId] = record;
            Save();
        }
    }

    public void Touch(string clientId)
    {
        lock (_lock)
        {
            if (_byClientId.TryGetValue(clientId, out var r))
            {
                _byClientId[clientId] = r with { LastSeen = DateTimeOffset.UtcNow };
                Save();
            }
        }
    }

    public bool Remove(string clientId)
    {
        lock (_lock)
        {
            var removed = _byClientId.Remove(clientId);
            if (removed) Save();
            return removed;
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_byClientId.Values.ToList(), Json));
        File.Move(tmp, _path, overwrite: true);
    }
}
