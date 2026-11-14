using System.Net;
using System.Net.Sockets;
using System.Text;
using MonRem.Host.Video.Discovery;

namespace MonRem.Tests.Video;

public class VideoDiscoveryTests
{
    private static async Task<byte[]?> Ask(int port, byte[] request, TimeSpan wait)
    {
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await client.SendAsync(request, new IPEndPoint(IPAddress.Loopback, port));
        using var cts = new CancellationTokenSource(wait);
        try { return (await client.ReceiveAsync(cts.Token)).Buffer; }
        catch (OperationCanceledException) { return null; }
    }

    [Fact]
    public void Reply_layout()
    {
        var r = DiscoveryReply.Build("Flight1", 28252);

        Assert.Equal(308, r.Length);
        Assert.Equal("Flight1", Encoding.Unicode.GetString(r, 0, 14));
        Assert.Equal(28252u, BitConverter.ToUInt32(r, 260));
        Assert.Equal(10u, BitConverter.ToUInt32(r, 264));
        Assert.Equal(4u, BitConverter.ToUInt32(r, 272));
        Assert.Equal(8u, BitConverter.ToUInt32(r, 276));
        Assert.Equal(43, r[280]);
        Assert.Equal(0, r[280] & 0x10);
    }

    [Fact]
    public async Task Responder_answers_prefix_request()
    {
        await using var responder = new DiscoveryResponder(IPAddress.Loopback, 0, "MonRem", 28252);
        responder.Start();

        var reply = await Ask(responder.LocalPort, DiscoveryReply.RequestPrefix, TimeSpan.FromSeconds(3));

        Assert.NotNull(reply);
        Assert.Equal(308, reply.Length);
    }

    [Fact]
    public async Task Responder_answers_1024_byte_padded_request()
    {
        await using var responder = new DiscoveryResponder(IPAddress.Loopback, 0, "MonRem", 28252);
        responder.Start();
        var padded = new byte[1024];
        DiscoveryReply.RequestPrefix.CopyTo(padded, 0);

        var reply = await Ask(responder.LocalPort, padded, TimeSpan.FromSeconds(3));

        Assert.NotNull(reply);
        Assert.Equal(308, reply.Length);
    }

    [Fact]
    public async Task Responder_ignores_other_datagrams()
    {
        await using var responder = new DiscoveryResponder(IPAddress.Loopback, 0, "MonRem", 28252);
        responder.Start();

        var reply = await Ask(responder.LocalPort, "HELLO"u8.ToArray(), TimeSpan.FromMilliseconds(300));

        Assert.Null(reply);
    }
}
