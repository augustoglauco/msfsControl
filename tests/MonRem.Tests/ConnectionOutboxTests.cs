using MonRem.Host.Core.Session;
using MonRem.Host.Core.Sim;
using MonRem.Protocol;
using MonRem.Protocol.V1;

namespace MonRem.Tests;

public class ConnectionOutboxTests
{
    private static SimValues Values(uint sub, uint handle, double v, bool full = false)
    {
        var m = new SimValues { SubId = sub, FullState = full };
        m.Items.Add(new SimValue { Handle = handle, Number = v });
        return m;
    }

    private static async Task<List<Envelope>> Drain(ConnectionOutbox outbox)
    {
        var stream = new MemoryStream();
        outbox.Close();
        await outbox.RunWriterAsync(stream, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        stream.Position = 0;
        List<Envelope> result = [];
        while (await EnvelopeFraming.ReadAsync(stream, CancellationToken.None) is { } e) result.Add(e);
        return result;
    }

    [Fact]
    public async Task Sim_values_are_coalesced_per_subscription_latest_wins()
    {
        var outbox = new ConnectionOutbox(_ => { });
        outbox.OfferSimValues(Values(1, 10, 1, full: true));
        outbox.OfferSimValues(Values(1, 10, 2));
        outbox.OfferSimValues(Values(1, 11, 5));
        outbox.OfferSimValues(Values(2, 20, 7));

        var written = await Drain(outbox);
        Assert.Equal(2, written.Count);
        var sub1 = written.Single(e => e.SimValues.SubId == 1).SimValues;
        Assert.True(sub1.FullState); // full_state não se perde ao juntar
        Assert.Equal(2, sub1.Items.Single(i => i.Handle == 10).Number);
        Assert.Equal(5, sub1.Items.Single(i => i.Handle == 11).Number);
    }

    [Fact]
    public async Task Control_messages_go_before_values_and_keep_order()
    {
        var outbox = new ConnectionOutbox(_ => { });
        outbox.OfferSimValues(Values(1, 10, 1));
        outbox.Enqueue(new Envelope { ReqId = 1, Pong = new Pong() });
        outbox.Enqueue(new Envelope { ReqId = 2, Pong = new Pong() });

        var written = await Drain(outbox);
        Assert.Equal([1u, 2u, 0u], written.Select(e => e.ReqId));
    }

    [Fact]
    public void Full_queue_fails_the_connection_instead_of_blocking()
    {
        string? fatal = null;
        var outbox = new ConnectionOutbox(reason => fatal = reason);
        for (var i = 0; i < ConnectionOutbox.MaxQueuedMessages; i++)
            Assert.True(outbox.Enqueue(new Envelope { Ping = new Ping() }));
        Assert.False(outbox.Enqueue(new Envelope { Ping = new Ping() }));
        Assert.NotNull(fatal);
    }
}

public class SubscriptionLimitTests
{
    [Fact]
    public void Too_many_subscriptions_or_vars_are_refused()
    {
        var hub = new SubscriptionHub(new SimRouter(new FakeSimConnection()), new ManualTimeProvider());
        for (uint i = 1; i <= SubscriptionHub.MaxSubscriptionsPerClient; i++)
        {
            var ok = new SimSubscribe { SubId = i };
            ok.Vars.Add(new SimVarSpec { Ref = $"L:V{i}" });
            Assert.NotEqual(0u, hub.Subscribe("c", ok, _ => ValueTask.CompletedTask).Handles[0].Handle);
        }

        var extra = new SimSubscribe { SubId = 999 };
        extra.Vars.Add(new SimVarSpec { Ref = "L:X" });
        Assert.Equal(ErrorCode.ErrorResourceExhausted, hub.Subscribe("c", extra, _ => ValueTask.CompletedTask).Handles[0].Error.Code);

        var big = new SimSubscribe { SubId = 1 }; // substitui uma existente: não conta no limite de quantidade
        big.Vars.AddRange(Enumerable.Range(0, SubscriptionHub.MaxVarsPerSubscription + 1).Select(i => new SimVarSpec { Ref = $"L:B{i}" }));
        Assert.All(hub.Subscribe("c", big, _ => ValueTask.CompletedTask).Handles, h => Assert.Equal(ErrorCode.ErrorResourceExhausted, h.Error.Code));
    }
}
