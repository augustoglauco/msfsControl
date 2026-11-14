using System.Net;
using System.Net.Sockets;
using MonRem.Host.Video.Pipeline;
using MonRem.Host.Video.Server;
using MonRem.Host.Video.Wire;

namespace MonRem.Tests.Video;

public class VideoServerTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private sealed class Harness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _run;

        public Harness(Action<VideoServerOptions>? tweak = null)
        {
            var options = new VideoServerOptions { HostName = "test", TcpPort = 0, UdpPort = 0, Fps = 50 };
            tweak?.Invoke(options);
            Server = new VideoServer(options, (w, h, f) => new SyntheticFrameSource(w, h, f), () => new TestPatternEncoder());
            _run = Server.RunAsync(_cts.Token);
        }

        public VideoServer Server { get; }

        public async Task<TestClient> ConnectAsync()
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, Server.LocalTcpPort);
            return new TestClient(tcp);
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            try { await _run; } catch (OperationCanceledException) { }
            await Server.DisposeAsync();
            _cts.Dispose();
        }
    }

    private sealed class TestClient(TcpClient tcp) : IDisposable
    {
        private readonly NetworkStream _stream = tcp.GetStream();

        public bool DataAvailable => _stream.DataAvailable;

        public ValueTask SendAsync(WireMessage m) => WireCodec.WriteAsync(_stream, m, CancellationToken.None);

        public ValueTask Identify(int w, int h, int fps) =>
            SendAsync(Identification.Create(w, h, fps, "{AAAAAAAA-0000-0000-0000-000000000001}", "Test"));

        public async Task<WireMessage> ReadAsync()
        {
            using var cts = new CancellationTokenSource(Wait);
            var message = await WireCodec.ReadAsync(_stream, 16 * 1024 * 1024, cts.Token);
            return message ?? throw new EndOfStreamException("servidor fechou");
        }

        public async Task<IReadOnlyList<WireMessage>> ReadPreambleAsync()
        {
            var list = new List<WireMessage>();
            for (var i = 0; i < 5; i++) list.Add(await ReadAsync());
            return list;
        }

        /// <summary>Lê até o servidor fechar (fim limpo, reset ou dado inválido). <c>false</c> se não fechou a tempo.</summary>
        public async Task<bool> WaitClosedAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                while (await WireCodec.ReadAsync(_stream, 16 * 1024 * 1024, cts.Token) is not null) { }
                return true;
            }
            catch (OperationCanceledException) { return false; }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidDataException) { return true; }
        }

        public void Dispose() => tcp.Dispose();
    }

    private static async Task<bool> WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }

    [Fact]
    public async Task Handshake_sends_preamble_then_frames()
    {
        await using var harness = new Harness();
        using var client = await harness.ConnectAsync();
        await client.Identify(320, 240, 50);

        var preamble = await client.ReadPreambleAsync();
        Assert.Equal(
            [MessageType.DisplaySettings, MessageType.Visibility, MessageType.Visibility, MessageType.Ping, MessageType.Ping],
            preamble.Select(m => m.Type));

        for (var i = 0; i < 3; i++)
        {
            var frame = await client.ReadAsync();
            Assert.Equal(MessageType.Framebuffer, frame.Type);
            Assert.Equal(320u, frame.Ints[2]);
            Assert.Equal(240u, frame.Ints[3]);
            Assert.Equal(new byte[] { 0, 0, 0, 1, 0x09 }, frame.Body[..5]);
            await client.SendAsync(Messages.Ack());
        }
    }

    [Fact]
    public async Task Stalls_after_window_without_ack()
    {
        await using var harness = new Harness();
        using var client = await harness.ConnectAsync();
        await client.Identify(64, 64, 50);
        await client.ReadPreambleAsync();

        await client.ReadAsync();
        await client.ReadAsync();
        await Task.Delay(500);
        Assert.False(client.DataAvailable);

        await client.SendAsync(Messages.Ack());
        var third = await client.ReadAsync();
        Assert.Equal(MessageType.Framebuffer, third.Type);
    }

    [Fact]
    public async Task Accepts_type6_before_identification()
    {
        await using var harness = new Harness();
        using var client = await harness.ConnectAsync();
        await client.SendAsync(WireMessage.Create(MessageType.EvtDisplaySettings, default, (2, 1u)));
        await client.Identify(64, 64, 50);

        var preamble = await client.ReadPreambleAsync();
        Assert.Equal(MessageType.DisplaySettings, preamble[0].Type);
    }

    [Fact]
    public async Task Rejects_oversized_header_without_allocating()
    {
        await using var harness = new Harness();
        using (var bad = await harness.ConnectAsync())
        {
            await bad.SendAsync(new WireMessage(CreateInts(type: 0, bodySize: 0xFFFFFFFF), []));
            Assert.True(await bad.WaitClosedAsync(Wait));
        }

        using var good = await harness.ConnectAsync();
        await good.Identify(64, 64, 50);
        Assert.Equal(MessageType.DisplaySettings, (await good.ReadPreambleAsync())[0].Type);

        static uint[] CreateInts(uint type, uint bodySize)
        {
            var ints = new uint[32];
            ints[0] = type;
            ints[1] = bodySize;
            return ints;
        }
    }

    [Fact]
    public async Task Identification_timeout_closes()
    {
        await using var harness = new Harness(o => o.IdentifyTimeout = TimeSpan.FromMilliseconds(200));
        using var client = await harness.ConnectAsync();

        Assert.True(await client.WaitClosedAsync(Wait));
    }

    [Fact]
    public async Task Zero_size_identification_falls_back_to_1280x720()
    {
        await using var harness = new Harness();
        using var client = await harness.ConnectAsync();
        await client.Identify(0, 0, 0);
        await client.ReadPreambleAsync();

        var frame = await client.ReadAsync();
        Assert.Equal(1280u, frame.Ints[2]);
        Assert.Equal(720u, frame.Ints[3]);
        Assert.Equal(50u, frame.Ints[12]);
    }

    [Fact]
    public async Task Oversized_dimensions_fall_back_to_1280x720()
    {
        await using var harness = new Harness();
        using var client = await harness.ConnectAsync();
        await client.Identify(100000, 100000, 50);
        await client.ReadPreambleAsync();

        var frame = await client.ReadAsync();
        Assert.Equal(1280u, frame.Ints[2]);
        Assert.Equal(720u, frame.Ints[3]);
    }

    [Fact]
    public async Task Two_clients_independent()
    {
        await using var harness = new Harness();
        var first = await harness.ConnectAsync();
        using var second = await harness.ConnectAsync();
        await first.Identify(64, 64, 50);
        await second.Identify(64, 64, 50);
        await first.ReadPreambleAsync();
        await second.ReadPreambleAsync();
        Assert.True(await WaitUntil(() => harness.Server.ActiveSessions == 2, Wait));

        first.Dispose();

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(MessageType.Framebuffer, (await second.ReadAsync()).Type);
            await second.SendAsync(Messages.Ack());
        }
        Assert.True(await WaitUntil(() => harness.Server.ActiveSessions == 1, Wait));
    }

    [Fact]
    public async Task Connection_loss_ends_session()
    {
        await using var harness = new Harness(o => o.ConnectionLossTimeout = TimeSpan.FromMilliseconds(300));
        using var client = await harness.ConnectAsync();
        await client.Identify(64, 64, 50);
        await client.ReadPreambleAsync();
        Assert.True(await WaitUntil(() => harness.Server.ActiveSessions == 1, Wait));

        Assert.True(await WaitUntil(() => harness.Server.ActiveSessions == 0, Wait));
    }
}
