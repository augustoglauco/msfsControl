using MonRem.Host.Core.Sim;
using MonRem.Protocol.V1;

namespace MonRem.Tests;

public class SubscriptionHubTests
{
    private readonly FakeSimConnection _sim = new();
    private readonly ManualTimeProvider _time = new();
    private readonly SubscriptionHub _hub;
    private readonly Dictionary<string, List<SimValues>> _sent = [];

    public SubscriptionHubTests() => _hub = new SubscriptionHub(new SimRouter(_sim), _time);

    private Func<Envelope, ValueTask> Sink(string client)
    {
        _sent[client] = [];
        return e =>
        {
            _sent[client].Add(e.SimValues);
            return ValueTask.CompletedTask;
        };
    }

    private static SimSubscribe Request(uint subId, uint maxHz, params (string Ref, double Eps)[] vars)
    {
        var r = new SimSubscribe { SubId = subId, MaxHz = maxHz };
        r.Vars.AddRange(vars.Select(v => new SimVarSpec { Ref = v.Ref, Epsilon = v.Eps }));
        return r;
    }

    private Task Tick(TimeSpan by)
    {
        _time.Advance(by);
        return _hub.FlushDueAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Same_variable_from_two_clients_uses_one_backend_subscription()
    {
        _hub.Subscribe("c1", Request(1, 30, ("A:INDICATED ALTITUDE,feet", 0)), Sink("c1"));
        _hub.Subscribe("c2", Request(7, 30, ("A:indicated altitude,FEET", 0)), Sink("c2"));
        Assert.Equal(1, _hub.FeedCount);
        Assert.Equal(1, _sim.ListenerCount("A:INDICATED ALTITUDE,feet"));

        _sim.Push("A:INDICATED ALTITUDE,feet", SimDatum.Of(1000));
        await Tick(TimeSpan.FromMilliseconds(40));

        Assert.Equal(1000, Assert.Single(Assert.Single(_sent["c1"]).Items).Number);
        Assert.Equal(1000, Assert.Single(Assert.Single(_sent["c2"]).Items).Number);
        Assert.Equal(7u, _sent["c2"][0].SubId);
    }

    [Fact]
    public async Task First_message_is_full_state_then_only_changes_above_epsilon()
    {
        _sim.Push("A:AIRSPEED INDICATED,knots", SimDatum.Of(100));
        var r = _hub.Subscribe("c1", Request(1, 30, ("A:AIRSPEED INDICATED,knots", 0.5)), Sink("c1"));
        Assert.Equal(1u, r.Handles[0].Handle);

        await Tick(TimeSpan.Zero);
        Assert.True(_sent["c1"][0].FullState);

        _sim.Push("A:AIRSPEED INDICATED,knots", SimDatum.Of(100.3)); // abaixo do epsilon
        await Tick(TimeSpan.FromMilliseconds(40));
        Assert.Single(_sent["c1"]);

        _sim.Push("A:AIRSPEED INDICATED,knots", SimDatum.Of(100.6)); // acumulado passa do epsilon
        await Tick(TimeSpan.FromMilliseconds(40));
        Assert.Equal(2, _sent["c1"].Count);
        Assert.False(_sent["c1"][1].FullState);
        Assert.Equal(100.6, _sent["c1"][1].Items[0].Number);
    }

    [Fact]
    public async Task Respects_max_hz()
    {
        _hub.Subscribe("c1", Request(1, 10, ("L:X", 0)), Sink("c1")); // 100 ms
        _sim.Push("L:X", SimDatum.Of(1));
        await Tick(TimeSpan.Zero);
        Assert.Single(_sent["c1"]);

        _sim.Push("L:X", SimDatum.Of(2));
        await Tick(TimeSpan.FromMilliseconds(50));
        Assert.Single(_sent["c1"]); // ainda não venceu

        _sim.Push("L:X", SimDatum.Of(3));
        await Tick(TimeSpan.FromMilliseconds(60));
        Assert.Equal(2, _sent["c1"].Count);
        Assert.Equal(3, _sent["c1"][1].Items[0].Number); // só o valor mais novo
    }

    [Fact]
    public async Task Aircraft_change_resends_full_state()
    {
        _hub.Subscribe("c1", Request(1, 30, ("L:X", 0)), Sink("c1"));
        _sim.Push("L:X", SimDatum.Of(5));
        await Tick(TimeSpan.FromMilliseconds(40));

        _sim.ChangeAircraft(new AircraftInfo("Other", "", "", "", ""));
        await Tick(TimeSpan.FromMilliseconds(40));
        Assert.Equal(2, _sent["c1"].Count);
        Assert.True(_sent["c1"][1].FullState);
    }

    [Fact]
    public void Unsubscribe_and_remove_client_release_backend()
    {
        _hub.Subscribe("c1", Request(1, 30, ("L:X", 0), ("L:Y", 0)), Sink("c1"));
        _hub.Subscribe("c2", Request(1, 30, ("L:X", 0)), Sink("c2"));
        Assert.Equal(2, _hub.FeedCount);

        Assert.True(_hub.Unsubscribe("c1", 1));
        Assert.Equal(1, _hub.FeedCount);
        Assert.Equal(1, _sim.ListenerCount("L:X"));
        Assert.Equal(0, _sim.ListenerCount("L:Y"));

        _hub.RemoveClient("c2");
        Assert.Equal(0, _hub.FeedCount);
        Assert.False(_hub.Unsubscribe("c2", 1));
    }

    [Fact]
    public void Reports_errors_per_item()
    {
        var r = _hub.Subscribe("c1", Request(1, 30, ("A:OK", 0), ("nope", 0), ("K:AP_MASTER", 0), ("RPN:(L:X)", 0)), Sink("c1"));
        Assert.Equal(4, r.Handles.Count);
        Assert.NotEqual(0u, r.Handles[0].Handle);
        Assert.Equal(ErrorCode.ErrorInvalidArgument, r.Handles[1].Error.Code);
        Assert.Equal(ErrorCode.ErrorInvalidArgument, r.Handles[2].Error.Code); // K: não é legível
        Assert.Equal(ErrorCode.ErrorWasmBridgeMissing, r.Handles[3].Error.Code);
    }

    [Fact]
    public async Task Resubscribing_same_sub_id_replaces_previous()
    {
        _hub.Subscribe("c1", Request(1, 30, ("L:X", 0)), Sink("c1"));
        _hub.Subscribe("c1", Request(1, 30, ("L:Y", 0)), Sink("c1"));
        Assert.Equal(1, _hub.FeedCount);
        _sim.Push("L:Y", SimDatum.Of(9));
        await Tick(TimeSpan.FromMilliseconds(40));
        Assert.Equal(9, _sent["c1"].Single().Items.Single().Number);
    }
}
