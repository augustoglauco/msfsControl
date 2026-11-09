using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MonRem.Protocol.V1;

namespace MonRem.Host.Core.Sim;

/// <summary>
/// Junta as assinaturas de todos os clientes: uma única assinatura no backend por referência
/// (variável + unidade), repassada a cada cliente no ritmo dele (max_hz) e só quando passa do epsilon.
/// </summary>
public sealed class SubscriptionHub
{
    public const uint DefaultMaxHz = 30;
    public const uint MaxMaxHz = 60;

    private readonly SimRouter _router;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Feed> _feeds = [];
    private readonly Dictionary<(string ClientId, uint SubId), ClientSub> _subs = [];
    private readonly Dictionary<string, uint> _nextHandle = [];

    public SubscriptionHub(SimRouter router, TimeProvider time, ILogger<SubscriptionHub>? logger = null)
    {
        _router = router;
        _time = time;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _router.Connection.AircraftChanged += _ => RequestFullStateForAll();
    }

    /// <summary>Quantidade de assinaturas ativas no backend (para diagnóstico e testes).</summary>
    public int FeedCount
    {
        get { lock (_lock) return _feeds.Count; }
    }

    public SimSubscribed Subscribe(string clientId, SimSubscribe request, Func<Envelope, ValueTask> send)
    {
        var maxHz = request.MaxHz == 0 ? DefaultMaxHz : Math.Clamp(request.MaxHz, 1, MaxMaxHz);
        var response = new SimSubscribed { SubId = request.SubId };

        lock (_lock)
        {
            RemoveSubLocked(clientId, request.SubId);

            var sub = new ClientSub(clientId, request.SubId, TimeSpan.FromSeconds(1.0 / maxHz), send)
            {
                NextDue = _time.GetUtcNow(),
            };

            foreach (var spec in request.Vars)
            {
                var handle = new SimHandle();
                try
                {
                    var reference = SimRef.Parse(spec.Ref);
                    var feed = GetOrCreateFeedLocked(reference);
                    var item = new Item(sub, NextHandleLocked(clientId), feed, Math.Max(0, spec.Epsilon));
                    feed.Listeners.Add(item);
                    sub.Items.Add(item);
                    handle.Handle = item.Handle;
                }
                catch (FormatException ex)
                {
                    handle.Error = new Error { Code = ErrorCode.ErrorInvalidArgument, Message = ex.Message };
                }
                catch (SimException ex)
                {
                    handle.Error = new Error { Code = ex.Code, Message = ex.Message };
                }
                response.Handles.Add(handle);
            }

            _subs[(clientId, request.SubId)] = sub;
        }
        return response;
    }

    public bool Unsubscribe(string clientId, uint subId)
    {
        lock (_lock) return RemoveSubLocked(clientId, subId);
    }

    public void RemoveClient(string clientId)
    {
        lock (_lock)
        {
            foreach (var key in _subs.Keys.Where(k => k.ClientId == clientId).ToList())
                RemoveSubLocked(key.ClientId, key.SubId);
            _nextHandle.Remove(clientId);
        }
    }

    /// <summary>Troca de aeronave: todo cliente recebe o estado completo de novo.</summary>
    public void RequestFullStateForAll()
    {
        lock (_lock)
        {
            foreach (var sub in _subs.Values) sub.FullPending = true;
        }
    }

    /// <summary>Envia o que estiver vencido. Chamado pelo laço de <see cref="RunAsync"/> (ou por testes).</summary>
    public async Task FlushDueAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        List<(ClientSub Sub, Envelope Envelope)> outgoing = [];

        lock (_lock)
        {
            foreach (var sub in _subs.Values)
            {
                if (now < sub.NextDue) continue;
                sub.NextDue = now + sub.Period;
                var message = BuildLocked(sub);
                if (message is not null) outgoing.Add((sub, new Envelope { SimValues = message }));
            }
        }

        foreach (var (sub, envelope) in outgoing)
        {
            try
            {
                await sub.Send(envelope).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "Falha ao enviar SimValues para {Client}", sub.ClientId);
            }
        }
    }

    /// <summary>Laço de envio a cada 5 ms (o ritmo real de cada cliente vem do max_hz dele).</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), _time, ct).ConfigureAwait(false);
            await FlushDueAsync(ct).ConfigureAwait(false);
        }
    }

    private SimValues? BuildLocked(ClientSub sub)
    {
        var values = new SimValues { SubId = sub.SubId };

        if (sub.FullPending)
        {
            foreach (var item in sub.Items)
            {
                if (item.Feed.Last is not { } datum) continue;
                values.Items.Add(ToProto(item.Handle, datum));
                item.LastSent = datum;
                item.Dirty = false;
            }
            if (values.Items.Count == 0) return null;
            values.FullState = true;
            sub.FullPending = false;
            return values;
        }

        foreach (var item in sub.Items)
        {
            if (!item.Dirty) continue;
            item.Dirty = false;
            if (item.Feed.Last is not { } datum || !datum.DiffersFrom(item.LastSent, item.Epsilon)) continue;
            values.Items.Add(ToProto(item.Handle, datum));
            item.LastSent = datum;
        }
        return values.Items.Count == 0 ? null : values;
    }

    private Feed GetOrCreateFeedLocked(SimRef reference)
    {
        if (_feeds.TryGetValue(reference.Key, out var existing)) return existing;

        var backend = _router.Resolve(reference, SimOperation.Read);
        var feed = new Feed(reference);
        feed.Subscription = backend.Subscribe(reference, datum => OnValue(feed, datum));
        _feeds[reference.Key] = feed;
        return feed;
    }

    private void OnValue(Feed feed, SimDatum datum)
    {
        lock (_lock)
        {
            feed.Last = datum;
            foreach (var item in feed.Listeners) item.Dirty = true;
        }
    }

    private bool RemoveSubLocked(string clientId, uint subId)
    {
        if (!_subs.Remove((clientId, subId), out var sub)) return false;
        foreach (var item in sub.Items)
        {
            var feed = item.Feed;
            feed.Listeners.Remove(item);
            if (feed.Listeners.Count > 0) continue;
            feed.Subscription?.Dispose();
            _feeds.Remove(feed.Reference.Key);
        }
        return true;
    }

    private uint NextHandleLocked(string clientId)
    {
        var next = _nextHandle.GetValueOrDefault(clientId) + 1;
        _nextHandle[clientId] = next;
        return next;
    }

    private static SimValue ToProto(uint handle, SimDatum datum) =>
        datum.Text is { } text ? new SimValue { Handle = handle, Text = text } : new SimValue { Handle = handle, Number = datum.Number ?? double.NaN };

    private sealed class Feed(SimRef reference)
    {
        public SimRef Reference { get; } = reference;
        public IDisposable? Subscription { get; set; }
        public SimDatum? Last { get; set; }
        public List<Item> Listeners { get; } = [];
    }

    private sealed class Item(ClientSub owner, uint handle, Feed feed, double epsilon)
    {
        public ClientSub Owner { get; } = owner;
        public uint Handle { get; } = handle;
        public Feed Feed { get; } = feed;
        public double Epsilon { get; } = epsilon;
        public SimDatum? LastSent { get; set; }
        public bool Dirty { get; set; } = true;
    }

    private sealed class ClientSub(string clientId, uint subId, TimeSpan period, Func<Envelope, ValueTask> send)
    {
        public string ClientId { get; } = clientId;
        public uint SubId { get; } = subId;
        public TimeSpan Period { get; } = period;
        public Func<Envelope, ValueTask> Send { get; } = send;
        public List<Item> Items { get; } = [];
        public DateTimeOffset NextDue { get; set; }
        public bool FullPending { get; set; } = true;
    }
}
