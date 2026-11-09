using System.IO.Pipelines;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using MonRem.Host.Core.Devices;
using MonRem.Host.Core.Sim;
using MonRem.Protocol;
using MonRem.Protocol.V1;

namespace MonRem.Tests;

/// <summary>Gateway de painéis de ponta a ponta: painel falso ↔ framing serial real ↔ DeviceSession ↔ sim falso.</summary>
public sealed class DeviceSessionTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static readonly Element[] Elements =
    [
        new() { Id = 1, Kind = ElementKind.Button, Name = "AP1" },
        new() { Id = 2, Kind = ElementKind.Toggle, Name = "BAT1" },
        new() { Id = 3, Kind = ElementKind.Encoder, Name = "HDG", Detents = 1 },
        new() { Id = 20, Kind = ElementKind.Led, Name = "AP1_LED" },
        new() { Id = 24, Kind = ElementKind.Backlight, Name = "BL" },
    ];

    private readonly FakeSimConnection _sim = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly string _cacheDir = Path.Combine(Path.GetTempPath(), "monrem-desc-" + Guid.NewGuid().ToString("N"));
    private DeviceManager _manager = null!;
    private Task _hubLoop = null!;

    public Task InitializeAsync()
    {
        var router = new SimRouter(_sim);
        var hub = new SubscriptionHub(router, TimeProvider.System);
        var profiles = DeviceProfileStore.FromProfiles(
        [
            new DeviceProfile
            {
                Name = "fcu-geral",
                Model = "fcu",
                Inputs =
                [
                    new InputBinding { Element = "AP1", Press = new SimAction { Fire = "K:AP_MASTER" } },
                    new InputBinding { Element = "BAT1", On = new SimAction { Fire = "K:BAT_ON" }, Off = new SimAction { Fire = "K:BAT_OFF" } },
                    new InputBinding { Element = "HDG", Inc = new SimAction { Fire = "K:HEADING_BUG_INC" }, Dec = new SimAction { Fire = "K:HEADING_BUG_DEC" } },
                ],
                Outputs =
                [
                    new OutputBinding { Element = "AP1_LED", Source = "A:AUTOPILOT MASTER,bool" },
                    new OutputBinding { Element = "BL", Value = 200 },
                ],
            },
            new DeviceProfile
            {
                Name = "fcu-a320",
                Model = "fcu",
                Aircraft = "A320",
                Outputs = [new OutputBinding { Element = "AP1_LED", Source = "L:A32NX_AP1" }],
            },
            new DeviceProfile { Name = "radio-direto", Model = "radio", Direct = true },
        ]);
        _manager = new DeviceManager(router, hub, profiles, new DescriptorCache(_cacheDir), TimeProvider.System, NullLoggerFactory.Instance);
        _hubLoop = hub.RunAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _cts.CancelAsync();
        try { await _hubLoop; } catch (OperationCanceledException) { }
        try { Directory.Delete(_cacheDir, true); } catch (IOException) { }
    }

    private (FakePanel Panel, Task<bool> Session) Connect(string model = "fcu", string deviceId = "a0b1c2d3e4f5")
    {
        var toHost = new Pipe();
        var toPanel = new Pipe();
        var hostLink = new SerialDeviceLink(new DuplexStream(toHost.Reader, toPanel.Writer), "pipe");
        var panel = new FakePanel(new DuplexStream(toPanel.Reader, toHost.Writer), model, deviceId, Elements);
        return (panel, _manager.RunLinkAsync(hostLink, _cts.Token));
    }

    [Fact]
    public async Task Full_flow_descriptor_inputs_and_outputs()
    {
        var (panel, session) = Connect();
        var welcome = await panel.HelloAsync().WaitAsync(Timeout);
        Assert.True(welcome.NeedDescriptor);
        Assert.False(welcome.DirectMode);

        await panel.SendDescriptorAsync(pageSize: 3);              // 2 páginas
        var backlight = await panel.WaitOutputAsync(24).WaitAsync(Timeout);
        Assert.Equal(200u, backlight.Level);

        await panel.SendInputsAsync(true, new InputValue { Id = 1, Pressed = false }, new InputValue { Id = 2, Pressed = true }, new InputValue { Id = 3, Count = 0 });
        await WaitFired("K:BAT_ON");                                // sincronismo da chave na conexão

        await panel.SendInputsAsync(false, new InputValue { Id = 1, Pressed = true });
        await WaitFired("K:AP_MASTER");

        await panel.SendInputsAsync(false, new InputValue { Id = 3, Count = 3 });
        await WaitFired("K:HEADING_BUG_INC", count: 3);

        var ledTask = panel.WaitOutputAsync(20, o => o.On);
        _sim.Push("A:AUTOPILOT MASTER,bool", SimDatum.Of(1));
        Assert.True((await ledTask.WaitAsync(Timeout)).On);

        Assert.Single(_manager.Devices);
        Assert.Equal("fcu-geral", _manager.Devices[0].ProfileName);

        await panel.ByeAsync();
        Assert.True(await session.WaitAsync(Timeout));
    }

    [Fact]
    public async Task Descriptor_is_cached_by_model_and_hash()
    {
        var (first, s1) = Connect();
        await first.HelloAsync().WaitAsync(Timeout);
        await first.SendDescriptorAsync(pageSize: 16);
        await first.WaitOutputAsync(24).WaitAsync(Timeout);
        await first.ByeAsync();
        await s1.WaitAsync(Timeout);

        var (second, s2) = Connect(deviceId: "ffeeddccbbaa");
        Assert.False((await second.HelloAsync().WaitAsync(Timeout)).NeedDescriptor);
        Assert.Equal(200u, (await second.WaitOutputAsync(24).WaitAsync(Timeout)).Level); // já mapeado sem reenviar
        await second.ByeAsync();
        await s2.WaitAsync(Timeout);
    }

    [Fact]
    public async Task Aircraft_change_switches_profile()
    {
        var (panel, session) = Connect();
        await panel.HelloAsync().WaitAsync(Timeout);
        await panel.SendDescriptorAsync(pageSize: 16);
        await panel.WaitOutputAsync(24).WaitAsync(Timeout);

        _sim.ChangeAircraft(new AircraftInfo("Airbus A320neo", "", "", "A20N", ""));
        var led = panel.WaitOutputAsync(20, o => o.On);
        _sim.Push("L:A32NX_AP1", SimDatum.Of(1));                  // fonte do perfil do A320
        Assert.True((await led.WaitAsync(Timeout)).On);
        Assert.Equal("fcu-a320", _manager.Devices[0].ProfileName);

        await panel.ByeAsync();
        await session.WaitAsync(Timeout);
    }

    [Fact]
    public async Task Direct_mode_forwards_sim_messages()
    {
        var (panel, session) = Connect(model: "radio");
        Assert.True((await panel.HelloAsync().WaitAsync(Timeout)).DirectMode);

        var fire = new SimFireEvent { Ref = "K:COM_STBY_RADIO_SET_HZ" };
        fire.Params.Add(122_800_000);
        var ack = await panel.RequestAsync(new DeviceFrame { Seq = 77, SimFireEvent = fire }, DeviceFrame.BodyOneofCase.SimAck).WaitAsync(Timeout);
        Assert.Equal(77u, ack.Seq);                                 // resposta ecoa o seq do pedido
        Assert.True(ack.SimAck.Ok);
        await WaitFired("K:COM_STBY_RADIO_SET_HZ");

        var sub = new SimSubscribe { SubId = 1, MaxHz = 30 };
        sub.Vars.Add(new SimVarSpec { Ref = "A:COM ACTIVE FREQUENCY:1,MHz" });
        await panel.RequestAsync(new DeviceFrame { Seq = 78, SimSubscribe = sub }, DeviceFrame.BodyOneofCase.SimSubscribed).WaitAsync(Timeout);
        var values = panel.WaitAsync(DeviceFrame.BodyOneofCase.SimValues);
        _sim.Push("A:COM ACTIVE FREQUENCY:1,MHz", SimDatum.Of(118.1));
        Assert.Equal(118.1, (await values.WaitAsync(Timeout)).SimValues.Items[0].Number);

        await panel.ByeAsync();
        await session.WaitAsync(Timeout);
    }

    [Fact]
    public async Task Link_that_never_says_hello_is_not_a_panel()
    {
        var toHost = new Pipe();
        var toPanel = new Pipe();
        var link = new SerialDeviceLink(new DuplexStream(toHost.Reader, toPanel.Writer), "pipe");
        await toHost.Writer.CompleteAsync(); // fecha sem mandar nada
        Assert.False(await _manager.RunLinkAsync(link, _cts.Token).WaitAsync(Timeout));
    }

    private async Task WaitFired(string reference, int count = 1)
    {
        for (var i = 0; i < 500; i++)
        {
            if (_sim.Fired.Count(f => f.Ref == reference) >= count) return;
            await Task.Delay(10);
        }
        Assert.Fail($"{reference} não disparou {count}x (disparos: {string.Join(", ", _sim.Fired.Select(f => f.Ref))})");
    }

    /// <summary>Painel ESP32 falso: fala o protocolo serial real.</summary>
    private sealed class FakePanel(Stream stream, string model, string deviceId, Element[] elements)
    {
        private readonly DeviceSerialReader _reader = new(stream);
        private readonly List<DeviceFrame> _received = [];
        private readonly SemaphoreSlim _readLock = new(1, 1);

        public async Task<DeviceWelcome> HelloAsync()
        {
            await SendAsync(new DeviceFrame
            {
                Hello = new DeviceHello { ProtocolVersion = 1, DeviceId = deviceId, Model = model, FwVersion = "0.1", DescriptorHash = Hash(16), Transport = DeviceTransport.Usb },
            });
            return (await WaitAsync(DeviceFrame.BodyOneofCase.Welcome)).Welcome;
        }

        /// <summary>Hash = CRC-32 das páginas serializadas, na ordem: o mesmo cálculo que o firmware faz.</summary>
        private uint Hash(int pageSize) => Crc32.Compute([.. Pages(pageSize).SelectMany(p => p.ToByteArray())]);

        private List<DeviceDescriptor> Pages(int pageSize)
        {
            var count = (elements.Length + pageSize - 1) / pageSize;
            return [.. Enumerable.Range(0, count).Select(i =>
            {
                var page = new DeviceDescriptor { Page = (uint)i, Pages = (uint)count };
                page.Elements.AddRange(elements.Skip(i * pageSize).Take(pageSize));
                return page;
            })];
        }

        public async Task SendDescriptorAsync(int pageSize)
        {
            foreach (var p in Pages(pageSize)) await SendAsync(new DeviceFrame { IoDescriptor = p });
        }

        public Task SendInputsAsync(bool full, params InputValue[] items)
        {
            var state = new DeviceInputState { Full = full, TDeviceMs = (uint)Environment.TickCount };
            state.Items.AddRange(items);
            return SendAsync(new DeviceFrame { InputState = state });
        }

        public Task ByeAsync() => SendAsync(new DeviceFrame { Bye = new DeviceBye { Reason = DeviceByeReason.Shutdown } });

        public async Task<DeviceFrame> RequestAsync(DeviceFrame request, DeviceFrame.BodyOneofCase replyKind)
        {
            await SendAsync(request);
            return await WaitAsync(replyKind);
        }

        public async Task<OutputValue> WaitOutputAsync(uint id, Func<OutputValue, bool>? predicate = null)
        {
            while (true)
            {
                var frame = await WaitAsync(DeviceFrame.BodyOneofCase.OutputState);
                var match = frame.OutputState.Items.LastOrDefault(o => o.Id == id && (predicate?.Invoke(o) ?? true));
                if (match is not null) return match;
            }
        }

        public async Task<DeviceFrame> WaitAsync(DeviceFrame.BodyOneofCase kind)
        {
            await _readLock.WaitAsync();
            try
            {
                while (true)
                {
                    var frame = await _reader.ReadAsync(CancellationToken.None) ?? throw new IOException("host fechou");
                    _received.Add(frame);
                    if (frame.BodyCase == kind) return frame;
                }
            }
            finally
            {
                _readLock.Release();
            }
        }

        private async Task SendAsync(DeviceFrame frame)
        {
            await stream.WriteAsync(DeviceSerialFraming.Encode(frame));
            await stream.FlushAsync();
        }
    }

    /// <summary>Stream bidirecional sobre dois Pipes (simula a porta serial).</summary>
    private sealed class DuplexStream(PipeReader reader, PipeWriter writer) : Stream
    {
        private readonly Stream _in = reader.AsStream();
        private readonly Stream _out = writer.AsStream();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _out.Flush();
        public override Task FlushAsync(CancellationToken ct) => _out.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => _in.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _in.ReadAsync(buffer, ct);
        public override void Write(byte[] buffer, int offset, int count) => _out.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => _out.WriteAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _in.Dispose();
                _out.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
