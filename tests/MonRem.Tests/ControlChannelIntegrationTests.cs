using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using MonRem.Client.Core;
using MonRem.Host.Core;
using MonRem.Host.Core.Security;
using MonRem.Host.Core.Session;
using MonRem.Host.Core.Sim;
using MonRem.Protocol.V1;

namespace MonRem.Tests;

/// <summary>Host real em loopback (TLS 1.3) + cliente real: pareamento, sessão, Sim Bridge.</summary>
public sealed class ControlChannelIntegrationTests : IAsyncLifetime
{
    private const int TestKdfIterations = 1000;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "monrem-tests-" + Guid.NewGuid().ToString("N"));
    private readonly CapturingPinDisplay _pins = new();
    private readonly FakeSimConnection _sim = new();
    private readonly CancellationTokenSource _cts = new();
    private HostContext _ctx = null!;
    private ControlServer _server = null!;
    private Task _serverTask = null!;
    private Task _hubTask = null!;

    public async Task InitializeAsync()
    {
        var options = new MonRemHostOptions { DataDirectory = Path.Combine(_root, "host"), PinKdfIterations = TestKdfIterations };
        _ctx = HostBootstrap.Create(options, _sim, null, _pins, NullLoggerFactory.Instance);
        _server = new ControlServer(_ctx);
        var started = new TaskCompletionSource();
        _serverTask = _server.RunAsync(new IPEndPoint(IPAddress.Loopback, 0), started, _cts.Token);
        _hubTask = _ctx.Subscriptions.RunAsync(_cts.Token);
        await started.Task.WaitAsync(Timeout);
    }

    public async Task DisposeAsync()
    {
        await _cts.CancelAsync();
        try { await Task.WhenAll(_serverTask, _hubTask).WaitAsync(Timeout); } catch (OperationCanceledException) { }
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private ClientIdentity NewIdentity(string name) => ClientIdentity.LoadOrCreate(Path.Combine(_root, name));

    private Task<ControlClient> Connect(ClientIdentity id) =>
        ControlClient.ConnectAsync("127.0.0.1", _server.BoundPort, id, _cts.Token).WaitAsync(Timeout);

    private Task<PairedHost> Pair(ControlClient client, Func<string, string>? alterPin = null) =>
        client.PairAsync("Tablet de teste", Platform.Android, async _ =>
        {
            var pin = await _pins.NextPin.WaitAsync(Timeout);
            return alterPin?.Invoke(pin) ?? pin;
        }, TestKdfIterations, _cts.Token).WaitAsync(Timeout);

    private static Capabilities Caps() => new() { MaxDecodeStreams = 1, TouchPoints = 10, CursorOverlay = true };

    [Fact]
    public async Task Pair_then_session_then_subscribe_receives_values()
    {
        var id = NewIdentity("tablet");
        await using (var first = await Connect(id))
        {
            Assert.Null(first.PairedHost);
            var host = await Pair(first);
            Assert.Equal(_ctx.HostId, host.HostId);

            // Pareou e segue na mesma conexão.
            var ready = await first.OpenSessionAsync(Platform.Android, "test", Caps(), ct: _cts.Token).WaitAsync(Timeout);
            Assert.Equal(47801u, ready.UdpPorts.Media);
            Assert.NotNull(first.SessionSecret);
        }

        // Reconexão: cert do host já fixado, sem PIN.
        await using var client = await Connect(id);
        Assert.NotNull(client.PairedHost);
        await client.OpenSessionAsync(Platform.Android, "test", Caps(), ct: _cts.Token).WaitAsync(Timeout);

        var subscribe = new SimSubscribe { SubId = 1, MaxHz = 60 };
        subscribe.Vars.Add(new SimVarSpec { Ref = "A:INDICATED ALTITUDE,feet" });
        subscribe.Vars.Add(new SimVarSpec { Ref = "bogus" });
        var subscribed = (await client.RequestAsync(new Envelope { SimSubscribe = subscribe }, _cts.Token).WaitAsync(Timeout)).SimSubscribed;
        Assert.NotEqual(0u, subscribed.Handles[0].Handle);
        Assert.Equal(ErrorCode.ErrorInvalidArgument, subscribed.Handles[1].Error.Code);

        var valuesTask = client.WaitForAsync(Envelope.BodyOneofCase.SimValues, _cts.Token);
        _sim.Push("A:INDICATED ALTITUDE,feet", SimDatum.Of(35000));
        var values = (await valuesTask.WaitAsync(Timeout)).SimValues;
        Assert.Equal(subscribed.Handles[0].Handle, values.Items[0].Handle);
        Assert.Equal(35000, values.Items[0].Number);
    }

    [Fact]
    public async Task Set_and_fire_are_acknowledged_and_routed()
    {
        var client = await PairedSession("pc");
        await using (client)
        {
            var fire = new SimFireEvent { Ref = "K:AP_ALT_VAR_SET_ENGLISH" };
            fire.Params.Add(12000);
            Assert.True((await client.RequestAsync(new Envelope { SimFireEvent = fire }).WaitAsync(Timeout)).SimAck.Ok);
            Assert.Contains(_sim.Fired, f => f.Ref == "K:AP_ALT_VAR_SET_ENGLISH" && f.Args.SequenceEqual([12000L]));

            var set = await client.RequestAsync(new Envelope { SimSetVar = new SimSetVar { Ref = "L:MY_VAR", Number = 3 } }).WaitAsync(Timeout);
            Assert.True(set.SimAck.Ok);

            var h = await client.RequestAsync(new Envelope { SimFireEvent = new SimFireEvent { Ref = "H:SOME_BUTTON" } }).WaitAsync(Timeout);
            Assert.False(h.SimAck.Ok);
            Assert.Equal(ErrorCode.ErrorWasmBridgeMissing, h.SimAck.Error.Code);
        }
    }

    [Fact]
    public async Task Wrong_pin_fails_and_nothing_is_saved()
    {
        var id = NewIdentity("intruso");
        await using var client = await Connect(id);
        var ex = await Assert.ThrowsAsync<PairingFailedException>(() => Pair(client, pin => pin == "000000" ? "111111" : "000000"));
        Assert.Contains("PIN", ex.Message);
        Assert.Empty(_ctx.PairedClients.List());
        Assert.Empty(id.Hosts);
    }

    [Fact]
    public async Task Unpaired_client_cannot_open_session()
    {
        var id = NewIdentity("estranho");
        await using var client = await Connect(id);
        var hello = new Hello { ProtocolVersion = 1, ClientId = id.ClientId, KeyShare = Google.Protobuf.ByteString.CopyFrom(new byte[65]) };
        var ex = await Assert.ThrowsAsync<HostErrorException>(() => client.RequestAsync(new Envelope { Hello = hello }).WaitAsync(Timeout));
        Assert.Equal(ErrorCode.ErrorAuthUnpaired, ex.Error.Code);
    }

    [Fact]
    public async Task Resume_token_is_honored_on_reconnect()
    {
        var id = NewIdentity("resume");
        byte[] token;
        await using (var first = await Connect(id))
        {
            await Pair(first);
            await first.OpenSessionAsync(Platform.Android, "test", Caps(), ct: _cts.Token).WaitAsync(Timeout);
            token = first.ResumeToken!;
        }

        await using var second = await Connect(id);
        var ack = await second.RequestAsync(new Envelope
        {
            Hello = new Hello
            {
                ProtocolVersion = 1,
                ClientId = id.ClientId,
                ResumeToken = Google.Protobuf.ByteString.CopyFrom(token),
                KeyShare = Google.Protobuf.ByteString.CopyFrom(MonRem.Security.SessionKeys.ExportPublic(MonRem.Security.SessionKeys.CreateEphemeral())),
            },
        }).WaitAsync(Timeout);
        Assert.True(ack.HelloAck.Resumed);
    }

    private async Task<ControlClient> PairedSession(string name)
    {
        var client = await Connect(NewIdentity(name));
        await Pair(client);
        await client.OpenSessionAsync(Platform.Windows, "test", Caps(), ct: _cts.Token).WaitAsync(Timeout);
        return client;
    }

    private sealed class CapturingPinDisplay : IPinDisplay
    {
        private TaskCompletionSource<string> _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string> NextPin => _next.Task;

        public void Show(string deviceName, string pin) => _next.TrySetResult(pin);

        public void Clear() => _next = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
