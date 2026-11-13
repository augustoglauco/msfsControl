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
        Directory.CreateDirectory(options.EffectiveLayoutsDirectory);
        File.WriteAllText(Path.Combine(options.EffectiveLayoutsDirectory, "a320.json"), PanelTests.FcuLayoutJson);
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
        }, ct: _cts.Token).WaitAsync(Timeout);

    private static Capabilities Caps() => new();

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
    public async Task Second_concurrent_pairing_is_refused_until_first_ends()
    {
        await using var first = await Connect(NewIdentity("um"));
        var challenge = await first.RequestAsync(new Envelope { PairStart = new PairStart { ClientId = "um", DeviceName = "um" } }).WaitAsync(Timeout);
        Assert.Equal(Envelope.BodyOneofCase.PairChallenge, challenge.BodyCase);

        await using var second = await Connect(NewIdentity("dois"));
        var refused = await second.RequestAsync(new Envelope { PairStart = new PairStart { ClientId = "dois", DeviceName = "dois" } }).WaitAsync(Timeout);
        Assert.False(refused.PairResult.Ok);
        Assert.True(refused.PairResult.RetryAfterS > 0);
    }

    [Fact]
    public async Task Unpair_from_host_closes_active_session_immediately()
    {
        var id = NewIdentity("perdido");
        await using var client = await Connect(id);
        await Pair(client);
        await client.OpenSessionAsync(Platform.Android, "test", Caps(), ct: _cts.Token).WaitAsync(Timeout);

        var bye = client.WaitForAsync(Envelope.BodyOneofCase.Bye, _cts.Token);
        Assert.True(await _ctx.UnpairAsync(id.ClientId));
        Assert.Equal(ByeReason.Unpaired, (await bye.WaitAsync(Timeout)).Bye.Reason);

        // O cliente ainda acha que está pareado, mas o host não aceita mais o certificado.
        await using var again = await Connect(id);
        var ex = await Assert.ThrowsAsync<HostErrorException>(() =>
            again.OpenSessionAsync(Platform.Android, "test", Caps(), ct: _cts.Token).WaitAsync(Timeout));
        Assert.Equal(ErrorCode.ErrorAuthUnpaired, ex.Error.Code);
    }

    [Fact]
    public async Task Client_cannot_unpair_someone_else()
    {
        var victim = NewIdentity("vitima");
        await using (var v = await Connect(victim)) await Pair(v);

        await using var other = await PairedSession("outro");
        var ex = await Assert.ThrowsAsync<HostErrorException>(() =>
            other.RequestAsync(new Envelope { Unpair = new Unpair { ClientId = victim.ClientId } }).WaitAsync(Timeout));
        Assert.Equal(ErrorCode.ErrorNotOwner, ex.Error.Code);
        Assert.NotNull(_ctx.PairedClients.FindByClientId(victim.ClientId));
    }

    [Fact]
    public async Task Cannot_take_over_client_id_paired_with_another_certificate()
    {
        var victim = NewIdentity("dono");
        await using (var v = await Connect(victim)) await Pair(v);

        await using var attacker = await Connect(NewIdentity("atacante"));
        var ex = await Assert.ThrowsAsync<HostErrorException>(() => attacker.RequestAsync(new Envelope
        {
            PairStart = new PairStart { ClientId = victim.ClientId, DeviceName = "tablet do dono" },
        }).WaitAsync(Timeout));
        Assert.Equal(ErrorCode.ErrorInvalidArgument, ex.Error.Code);
    }

    [Fact]
    public async Task Paired_client_can_pair_again_with_same_certificate()
    {
        var id = NewIdentity("repareia");
        await using (var first = await Connect(id)) await Pair(first);

        await using var second = await Connect(id);
        Assert.NotNull(second.PairedHost);
        await Pair(second); // ex.: o primeiro pareamento caiu antes do app gravar o host
        await second.OpenSessionAsync(Platform.Android, "test", Caps(), ct: _cts.Token).WaitAsync(Timeout);
    }

    [Fact]
    public async Task At_most_four_unpaired_connections()
    {
        var open = new List<ControlClient>();
        try
        {
            for (var i = 0; i < ConnectionDirectory.MaxUnpaired; i++)
            {
                var c = await Connect(NewIdentity($"anon{i}"));
                open.Add(c);
                var expected = i + 1;
                for (var t = 0; t < 200 && _ctx.Connections.UnpairedCount < expected; t++) await Task.Delay(10); // host já contou
            }

            await using var extra = await Connect(NewIdentity("anon-extra"));
            await Assert.ThrowsAnyAsync<Exception>(() =>
                extra.RequestAsync(new Envelope { PairStart = new PairStart { ClientId = "x" } }).WaitAsync(Timeout));
        }
        finally
        {
            foreach (var c in open) await c.DisposeAsync();
        }
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

    [Fact]
    public async Task App_gets_layout_and_panel_then_buttons_and_values_flow_through_the_host()
    {
        await using var client = await PairedSession("app");

        var layouts = (await client.RequestAsync(new Envelope { GetLayouts = new GetLayouts() }).WaitAsync(Timeout)).Layouts;
        var summary = Assert.Single(layouts.Items);
        Assert.Equal("a320", summary.LayoutId);
        Assert.Equal(1u, summary.PanelCount);

        var applied = (await client.RequestAsync(new Envelope { ApplyLayout = new ApplyLayout { LayoutId = "a320" } }).WaitAsync(Timeout)).LayoutApplied;
        var panelId = Assert.Single(applied.PanelIds);
        Assert.Equal("a320/fcu", panelId);

        var definition = (await client.RequestAsync(new Envelope { GetPanelDefinition = new GetPanelDefinition { PanelId = panelId } }).WaitAsync(Timeout)).PanelDefinition;
        Assert.Equal(1u, definition.Schema);
        var panel = MonRem.Protocol.Panels.PanelJson.ParsePanel(definition.Json);

        using var link = new MonRem.Client.Core.Panels.ControlClientSimLink(client);
        await using var runtime = new MonRem.Client.Core.Panels.PanelRuntime(panel, link);
        await runtime.StartAsync(_cts.Token).WaitAsync(Timeout);

        // Botão do app → evento no sim.
        var ap1 = runtime.Widgets.Single(w => w.Model.Id == "ap1");
        await runtime.PressAsync(ap1).WaitAsync(Timeout);
        Assert.Contains(_sim.Fired, f => f.Ref == "K:AP_MASTER");

        // Valor do sim → luz do botão no app.
        var lit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.ValueChanged += source => { if (source == "L:AP1_ACTIVE") lit.TrySetResult(); };
        _sim.Push("L:AP1_ACTIVE", SimDatum.Of(1));
        await lit.Task.WaitAsync(Timeout);
        Assert.True(runtime.IsLit(ap1));

        // Painel ou layout inexistente → NotFound.
        var ex = await Assert.ThrowsAsync<HostErrorException>(() =>
            client.RequestAsync(new Envelope { GetPanelDefinition = new GetPanelDefinition { PanelId = "a320/nada" } }).WaitAsync(Timeout));
        Assert.Equal(ErrorCode.ErrorNotFound, ex.Error.Code);
    }

    [Fact]
    public async Task App_session_flow_pairs_opens_and_loads_panels()
    {
        var id = NewIdentity("app-sessao");
        await using (var first = await MonRem.Client.Core.AppSession.ConnectAsync("127.0.0.1", _server.BoundPort, id, _cts.Token).WaitAsync(Timeout))
        {
            Assert.True(first.NeedsPairing);
            Assert.Equal(MonRem.Security.Certificates.Fingerprint(_ctx.HostCertificate), first.HostFingerprint);
            await first.PairAsync("Tablet", async _ => await _pins.NextPin.WaitAsync(Timeout), _cts.Token).WaitAsync(Timeout);
        }

        await using var session = await MonRem.Client.Core.AppSession.ConnectAsync("127.0.0.1", _server.BoundPort, id, _cts.Token).WaitAsync(Timeout);
        Assert.False(session.NeedsPairing);
        await session.OpenAsync("test", Caps(), _cts.Token).WaitAsync(Timeout);
        Assert.Equal("", session.SuggestedLayoutId); // aeronave falsa não casa com o layout do A320

        var layouts = await session.GetLayoutsAsync(_cts.Token).WaitAsync(Timeout);
        var panels = await session.LoadLayoutAsync(layouts[0].LayoutId, _cts.Token).WaitAsync(Timeout);
        Assert.Equal("fcu", Assert.Single(panels).Id);

        await using var runtime = session.CreateRuntime(panels[0]);
        await runtime.StartAsync(_cts.Token).WaitAsync(Timeout);
        await runtime.PressAsync(runtime.Widgets.Single(w => w.Model.Id == "ap1")).WaitAsync(Timeout);
        Assert.Contains(_sim.Fired, f => f.Ref == "K:AP_MASTER");
    }

    [Fact]
    public async Task Aircraft_change_reports_matching_layout()
    {
        await using var client = await PairedSession("app-aeronave");
        var changed = client.WaitForAsync(Envelope.BodyOneofCase.SimAircraftChanged, _cts.Token);
        // A primeira notificação é a aeronave atual (falsa, sem layout); espera a próxima.
        var first = await changed.WaitAsync(Timeout);
        Assert.Equal("", first.SimAircraftChanged.MatchedLayoutId);

        var next = client.WaitForAsync(Envelope.BodyOneofCase.SimAircraftChanged, _cts.Token);
        _sim.ChangeAircraft(new AircraftInfo("FlyByWire A320neo", "", "", "A20N", ""));
        Assert.Equal("a320", (await next.WaitAsync(Timeout)).SimAircraftChanged.MatchedLayoutId);
    }

    [Fact]
    public async Task Busy_control_port_falls_back_to_a_free_one()
    {
        // Ocupa uma porta e mais as 9 seguintes: o servidor precisa acabar numa porta livre do sistema.
        var blockers = new List<System.Net.Sockets.TcpListener>();
        var first = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        first.Start();
        blockers.Add(first);
        var basePort = ((IPEndPoint)first.LocalEndpoint).Port;
        for (var p = basePort + 1; p <= basePort + ControlServer.PortFallbackCount; p++)
        {
            try
            {
                var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, p);
                l.Start();
                blockers.Add(l);
            }
            catch (System.Net.Sockets.SocketException)
            {
                // Já ocupada por outro: serve igual.
            }
        }

        try
        {
            var server = new ControlServer(_ctx);
            var started = new TaskCompletionSource();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            var run = server.RunAsync(new IPEndPoint(IPAddress.Loopback, basePort), started, cts.Token);
            await started.Task.WaitAsync(Timeout);
            Assert.NotEqual(basePort, server.BoundPort);
            Assert.NotEqual(0, server.BoundPort);

            await using var client = await ControlClient.ConnectAsync("127.0.0.1", server.BoundPort, NewIdentity("fallback"), _cts.Token).WaitAsync(Timeout);
            Assert.Null(client.PairedHost);
            await cts.CancelAsync();
            await run.WaitAsync(Timeout);
        }
        finally
        {
            foreach (var b in blockers) b.Stop();
        }
    }

    [Fact]
    public async Task Client_reports_disconnect_when_host_closes_the_session()
    {
        var client = await PairedSession("queda");
        var dropped = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += ex => dropped.TrySetResult(ex);

        // Despareia a si mesmo: o host responde e fecha a conexão.
        await client.RequestAsync(new Envelope { Unpair = new Unpair() }).WaitAsync(Timeout);
        await dropped.Task.WaitAsync(Timeout);

        // DisposeAsync não dispara Disconnected de novo nem lança.
        await client.DisposeAsync();
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

        public void Show(PairingPrompt prompt) => _next.TrySetResult(prompt.Pin);

        public void Clear() => _next = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
