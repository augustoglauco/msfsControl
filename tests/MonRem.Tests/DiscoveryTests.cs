using System.Net;
using MonRem.Protocol.Discovery;

namespace MonRem.Tests;

public class DiscoveryTests
{
    private static readonly IPAddress[] Addresses = [IPAddress.Parse("192.168.0.10"), IPAddress.Parse("10.0.0.5")];
    private const string HostId = "6f1c2a9e-0d3b-4c55-9a77-1e2f3a4b5c6d";

    private static MonRemService Service(bool sim = true) =>
        new(HostId, "PC do Simulador", 47800, new string('a', 64), () => sim);

    private static DnsMessage Query(string name, DnsType type, ushort id = 0)
    {
        var q = new DnsMessage { Id = id };
        q.Questions.Add(new DnsQuestion(name, type, UnicastResponse: true));
        return q;
    }

    [Fact]
    public void Ptr_query_returns_everything_needed_to_connect()
    {
        var response = Service().Answer(Query("_monrem._tcp.local", DnsType.Ptr), Addresses);
        Assert.NotNull(response);

        // Codifica e decodifica, como na rede.
        var parsed = DnsMessage.Parse(response.Encode());
        var host = Assert.Single(MonRemService.Interpret(parsed));
        Assert.Equal(HostId, host.HostId);
        Assert.Equal("PC do Simulador", host.Name);
        Assert.Equal(47800, host.Port);
        Assert.Equal(IPAddress.Parse("192.168.0.10"), host.Address);
        Assert.Equal(new string('a', 32), host.FingerprintPrefix);
        Assert.True(host.SimConnected);
        Assert.Equal(1u, host.ProtocolVersion);
    }

    [Fact]
    public void Unrelated_or_response_packets_are_ignored()
    {
        Assert.Null(Service().Answer(Query("_http._tcp.local", DnsType.Ptr), Addresses));
        var response = Service().Answer(Query("_monrem._tcp.local", DnsType.Ptr), Addresses)!;
        Assert.Null(Service().Answer(response, Addresses));
    }

    [Fact]
    public void Legacy_unicast_echoes_id_and_question()
    {
        var response = Service().Answer(Query("_monrem._tcp.local", DnsType.Ptr, id: 0x1234), Addresses, legacyUnicast: true)!;
        var parsed = DnsMessage.Parse(response.Encode());
        Assert.Equal(0x1234, parsed.Id);
        Assert.Single(parsed.Questions);
    }

    [Fact]
    public void Answers_srv_txt_and_a_queries_individually()
    {
        var s = Service(sim: false);
        var srv = DnsMessage.Parse(s.Answer(Query(s.InstanceName, DnsType.Srv), Addresses)!.Encode());
        Assert.Equal(47800, ((SrvData)srv.Answers.Single(r => r.Type == DnsType.Srv).Data).Port);

        var txt = DnsMessage.Parse(s.Answer(Query(s.InstanceName, DnsType.Txt), Addresses)!.Encode());
        Assert.Equal("0", ((Dictionary<string, string>)txt.Answers.Single(r => r.Type == DnsType.Txt).Data)["sim"]);

        var a = DnsMessage.Parse(s.Answer(Query(s.HostName.ToUpperInvariant(), DnsType.A), Addresses)!.Encode());
        Assert.Equal(2, a.Answers.Count(r => r.Type == DnsType.A));
    }

    [Fact]
    public void Goodbye_has_zero_ttl_and_is_not_interpreted_as_a_host()
    {
        var bye = DnsMessage.Parse(Service().Announcement(Addresses, goodbye: true).Encode());
        Assert.All(bye.Answers, r => Assert.Equal(0u, r.Ttl));
        Assert.Empty(MonRemService.Interpret(bye));
    }

    [Fact]
    public void Parses_compressed_names()
    {
        // Pergunta PTR para _monrem._tcp.local seguida de resposta PTR cujo nome aponta (0xC00C) para a pergunta.
        byte[] packet =
        [
            0, 0, 0x84, 0, 0, 1, 0, 1, 0, 0, 0, 0,
            7, (byte)'_', (byte)'m', (byte)'o', (byte)'n', (byte)'r', (byte)'e', (byte)'m',
            4, (byte)'_', (byte)'t', (byte)'c', (byte)'p',
            5, (byte)'l', (byte)'o', (byte)'c', (byte)'a', (byte)'l', 0,
            0, 12, 0, 1,
            0xC0, 0x0C, 0, 12, 0, 1, 0, 0, 0x11, 0x94, 0, 5,
            2, (byte)'P', (byte)'C', 0xC0, 0x0C,
        ];
        var m = DnsMessage.Parse(packet);
        Assert.Equal("_monrem._tcp.local", m.Questions[0].Name);
        Assert.Equal("_monrem._tcp.local", m.Answers[0].Name);
        Assert.Equal("PC._monrem._tcp.local", m.Answers[0].Data);
    }

    [Theory]
    [InlineData(new byte[] { 0, 0, 0, 0 })]
    [InlineData(new byte[] { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0xC0, 0x0C })] // ponteiro para si mesmo
    [InlineData(new byte[] { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 9, 1, 2 })]     // rótulo truncado
    public void Rejects_malformed_packets(byte[] packet) => Assert.Throws<FormatException>(() => DnsMessage.Parse(packet));

    [Fact]
    public void Sanitizes_instance_label()
    {
        Assert.Equal("meu-pc", MonRemService.SanitizeLabel("meu.pc"));
        Assert.Equal("MonRem", MonRemService.SanitizeLabel("  "));
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(MonRemService.SanitizeLabel(new string('é', 100))) <= 63);
    }
}
