using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using MonRem.Security;

namespace MonRem.Client.Core;

/// <summary>Identidade persistente do cliente: client_id, certificado e hosts pareados (cert fixado).</summary>
public sealed class ClientIdentity
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _directory;
    private readonly Dictionary<string, PairedHost> _hosts;

    private ClientIdentity(string directory, string clientId, X509Certificate2 certificate, Dictionary<string, PairedHost> hosts)
    {
        _directory = directory;
        ClientId = clientId;
        Certificate = certificate;
        _hosts = hosts;
    }

    public string ClientId { get; }
    public X509Certificate2 Certificate { get; }
    public IReadOnlyCollection<PairedHost> Hosts => _hosts.Values;

    public static ClientIdentity LoadOrCreate(string directory)
    {
        Directory.CreateDirectory(directory);
        var idPath = Path.Combine(directory, "client-id.txt");
        if (!File.Exists(idPath)) File.WriteAllText(idPath, Guid.NewGuid().ToString("D"));
        var clientId = File.ReadAllText(idPath).Trim();

        var cert = Certificates.LoadOrCreate(Path.Combine(directory, "client.pfx"), $"MonRem Client {clientId}");
        var hostsPath = Path.Combine(directory, "paired-hosts.json");
        var hosts = File.Exists(hostsPath)
            ? (JsonSerializer.Deserialize<List<PairedHost>>(File.ReadAllText(hostsPath), Json) ?? []).ToDictionary(h => h.HostId)
            : [];
        return new ClientIdentity(directory, clientId, cert, hosts);
    }

    public PairedHost? FindHostByFingerprint(string fingerprint) => _hosts.Values.FirstOrDefault(h => h.CertFingerprint == fingerprint);

    public void SaveHost(PairedHost host)
    {
        _hosts[host.HostId] = host;
        File.WriteAllText(Path.Combine(_directory, "paired-hosts.json"), JsonSerializer.Serialize(_hosts.Values.ToList(), Json));
    }
}

public sealed record PairedHost(string HostId, string CertFingerprint, string LastAddress);
