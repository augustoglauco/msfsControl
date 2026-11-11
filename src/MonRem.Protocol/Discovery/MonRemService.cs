using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MonRem.Protocol.Discovery;

/// <summary>Host encontrado na rede.</summary>
public sealed record DiscoveredHost(
    string HostId,
    string Name,
    IPAddress Address,
    int Port,
    string FingerprintPrefix,
    bool SimConnected,
    uint ProtocolVersion);

/// <summary>
/// Anúncio DNS-SD do host: "&lt;nome&gt;._monrem._tcp.local" → SRV(porta, "monrem-&lt;id&gt;.local") + TXT + A.
/// Lógica pura (sem socket) para poder testar.
/// </summary>
public sealed class MonRemService
{
    public const string ServiceType = "_monrem._tcp.local";
    public const string ServiceEnumeration = "_services._dns-sd._udp.local";

    /// <summary>TTL de registros de host (SRV, A), RFC 6762 §10.</summary>
    public const uint HostTtl = 120;

    /// <summary>TTL dos demais (PTR, TXT), RFC 6762 §10.</summary>
    public const uint OtherTtl = 4500;

    public MonRemService(string hostId, string displayName, int port, string fingerprintHex, Func<bool> simConnected)
    {
        HostId = hostId;
        InstanceName = $"{SanitizeLabel(displayName)}.{ServiceType}";
        HostName = $"monrem-{hostId.Replace("-", "", StringComparison.Ordinal)[..Math.Min(12, hostId.Length)]}.local".ToLowerInvariant();
        Port = port;
        FingerprintPrefix = fingerprintHex.Length >= 32 ? fingerprintHex[..32] : fingerprintHex;
        DisplayName = displayName;
        _simConnected = simConnected;
    }

    private readonly Func<bool> _simConnected;

    public string HostId { get; }
    public string DisplayName { get; }
    public string InstanceName { get; }
    public string HostName { get; }
    /// <summary>Porta anunciada; o host atualiza com a porta que realmente abriu.</summary>
    public int Port { get; set; }
    public string FingerprintPrefix { get; }

    public Dictionary<string, string> Txt() => new()
    {
        ["v"] = ProtocolConstants.ProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["id"] = HostId,
        ["name"] = DisplayName,
        ["fp"] = FingerprintPrefix,
        ["sim"] = _simConnected() ? "1" : "0",
    };

    /// <summary>
    /// Resposta a uma pergunta, ou null se nada nela é nosso.
    /// <paramref name="ttlZero"/> = "goodbye" (RFC 6762 §10.1).
    /// </summary>
    public DnsMessage? Answer(DnsMessage query, IReadOnlyList<IPAddress> addresses, bool legacyUnicast = false)
    {
        if (query.IsResponse) return null;

        var response = new DnsMessage { Id = legacyUnicast ? query.Id : (ushort)0, IsResponse = true };
        var wantAll = false;
        foreach (var q in query.Questions)
        {
            var any = q.Type == DnsType.Any;
            if (DnsMessage.NameEquals(q.Name, ServiceType) && (any || q.Type == DnsType.Ptr))
            {
                Add(response.Answers, Ptr());
                wantAll = true;
            }
            else if (DnsMessage.NameEquals(q.Name, ServiceEnumeration) && (any || q.Type == DnsType.Ptr))
            {
                Add(response.Answers, new DnsRecord(ServiceEnumeration, DnsType.Ptr, OtherTtl, false, ServiceType));
            }
            else if (DnsMessage.NameEquals(q.Name, InstanceName))
            {
                if (any || q.Type == DnsType.Srv) Add(response.Answers, Srv());
                if (any || q.Type == DnsType.Txt) Add(response.Answers, TxtRecord());
                wantAll = true;
            }
            else if (DnsMessage.NameEquals(q.Name, HostName) && (any || q.Type == DnsType.A))
            {
                foreach (var a in ARecords(addresses)) Add(response.Answers, a);
            }
        }

        if (response.Answers.Count == 0) return null;

        // Registros adicionais (RFC 6763 §12): quem pergunta pelo serviço já recebe SRV, TXT e endereços.
        if (wantAll)
        {
            foreach (var r in new[] { Srv(), TxtRecord() }.Concat(ARecords(addresses)))
                if (!Contains(response.Answers, r)) Add(response.Additionals, r);
        }

        if (legacyUnicast)
        {
            // Unicast "legado" (porta de origem != 5353): ecoa a pergunta e usa TTL curto (RFC 6762 §6.7).
            foreach (var q in query.Questions) response.Questions.Add(q with { UnicastResponse = false });
        }
        return response;
    }

    /// <summary>Anúncio não solicitado (ao iniciar) ou despedida com TTL 0 (ao parar).</summary>
    public DnsMessage Announcement(IReadOnlyList<IPAddress> addresses, bool goodbye = false)
    {
        var m = new DnsMessage { IsResponse = true };
        foreach (var r in new[] { Ptr(), Srv(), TxtRecord() }.Concat(ARecords(addresses)))
            m.Answers.Add(goodbye ? r with { Ttl = 0 } : r);
        return m;
    }

    /// <summary>Converte uma resposta mDNS em hosts encontrados (lado do cliente).</summary>
    public static IEnumerable<DiscoveredHost> Interpret(DnsMessage response)
    {
        var all = response.Answers.Concat(response.Additionals).ToList();
        foreach (var ptr in all.Where(r => r.Type == DnsType.Ptr && DnsMessage.NameEquals(r.Name, ServiceType) && r.Ttl > 0))
        {
            var instance = (string)ptr.Data;
            var srv = all.FirstOrDefault(r => r.Type == DnsType.Srv && DnsMessage.NameEquals(r.Name, instance))?.Data as SrvData;
            var txt = all.FirstOrDefault(r => r.Type == DnsType.Txt && DnsMessage.NameEquals(r.Name, instance))?.Data as Dictionary<string, string>;
            if (srv is null || txt is null) continue;
            var address = all.Where(r => r.Type == DnsType.A && DnsMessage.NameEquals(r.Name, srv.Target))
                .Select(r => (IPAddress)r.Data).FirstOrDefault();
            if (address is null || !txt.TryGetValue("id", out var id)) continue;

            _ = uint.TryParse(txt.GetValueOrDefault("v"), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var version);
            yield return new DiscoveredHost(
                id,
                txt.GetValueOrDefault("name") ?? instance,
                address,
                srv.Port,
                txt.GetValueOrDefault("fp") ?? "",
                txt.GetValueOrDefault("sim") == "1",
                version);
        }
    }

    private DnsRecord Ptr() => new(ServiceType, DnsType.Ptr, OtherTtl, false, InstanceName);

    private DnsRecord Srv() => new(InstanceName, DnsType.Srv, HostTtl, true, new SrvData(0, 0, (ushort)Port, HostName));

    private DnsRecord TxtRecord() => new(InstanceName, DnsType.Txt, OtherTtl, true, Txt());

    private IEnumerable<DnsRecord> ARecords(IReadOnlyList<IPAddress> addresses) =>
        addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => new DnsRecord(HostName, DnsType.A, HostTtl, true, a));

    private static void Add(List<DnsRecord> list, DnsRecord record)
    {
        if (!Contains(list, record)) list.Add(record);
    }

    private static bool Contains(List<DnsRecord> list, DnsRecord record) =>
        list.Any(r => r.Type == record.Type && DnsMessage.NameEquals(r.Name, record.Name) && Equals(Key(r.Data), Key(record.Data)));

    private static object Key(object data) => data is IDictionary<string, string> ? "txt" : data;

    /// <summary>Nome de instância: até 63 bytes UTF-8, sem pontos.</summary>
    public static string SanitizeLabel(string name)
    {
        var clean = new StringBuilder();
        foreach (var ch in name.Replace('.', '-').Trim())
        {
            if (Encoding.UTF8.GetByteCount(clean.ToString() + ch) > 63) break;
            clean.Append(ch);
        }
        return clean.Length == 0 ? "MonRem" : clean.ToString();
    }
}
