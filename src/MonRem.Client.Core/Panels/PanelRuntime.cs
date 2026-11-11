using System.Collections.Concurrent;
using System.Text.Json;
using MonRem.Protocol.Panels;
using MonRem.Protocol.V1;

namespace MonRem.Client.Core.Panels;

/// <summary>O que o painel precisa do canal de controle (abstraído para teste).</summary>
public interface IPanelSimLink
{
    Task<SimSubscribed> SubscribeAsync(SimSubscribe request, CancellationToken ct);
    Task UnsubscribeAsync(uint subId, CancellationToken ct);
    Task<SimAck> FireAsync(SimFireEvent request, CancellationToken ct);
    Task<SimAck> SetAsync(SimSetVar request, CancellationToken ct);

    /// <summary>SimValues recebidos do host (todas as assinaturas; o runtime filtra pelo sub_id).</summary>
    event Action<SimValues>? Values;
}

/// <summary>Adaptador do <see cref="ControlClient"/> real.</summary>
public sealed class ControlClientSimLink : IPanelSimLink, IDisposable
{
    private readonly ControlClient _client;

    public ControlClientSimLink(ControlClient client)
    {
        _client = client;
        _client.Notification += OnNotification;
    }

    public event Action<SimValues>? Values;

    public async Task<SimSubscribed> SubscribeAsync(SimSubscribe request, CancellationToken ct) =>
        (await _client.RequestAsync(new Envelope { SimSubscribe = request }, ct).ConfigureAwait(false)).SimSubscribed;

    public Task UnsubscribeAsync(uint subId, CancellationToken ct) =>
        _client.RequestAsync(new Envelope { SimUnsubscribe = new SimUnsubscribe { SubId = subId } }, ct);

    public async Task<SimAck> FireAsync(SimFireEvent request, CancellationToken ct) =>
        (await _client.RequestAsync(new Envelope { SimFireEvent = request }, ct).ConfigureAwait(false)).SimAck;

    public async Task<SimAck> SetAsync(SimSetVar request, CancellationToken ct) =>
        (await _client.RequestAsync(new Envelope { SimSetVar = request }, ct).ConfigureAwait(false)).SimAck;

    private void OnNotification(Envelope e)
    {
        if (e.BodyCase == Envelope.BodyOneofCase.SimValues) Values?.Invoke(e.SimValues);
    }

    public void Dispose() => _client.Notification -= OnNotification;
}

/// <summary>Widget pronto para desenhar: modelo + problemas de validação (widget com problema não age).</summary>
public sealed class WidgetState(WidgetModel model, Comparison? when, IReadOnlyList<string> problems)
{
    public WidgetModel Model { get; } = model;
    public Comparison? When { get; } = when;
    public IReadOnlyList<string> Problems { get; } = problems;
    public bool IsValid => Problems.Count == 0;

    /// <summary>Estado local do toggle quando não há source.</summary>
    internal bool LocalOn { get; set; }
}

/// <summary>
/// Lógica de um painel, independente de UI: assina as variáveis usadas, guarda os valores,
/// responde o que cada widget mostra e executa as ações. A UI só desenha e repassa toques.
/// </summary>
public sealed class PanelRuntime : IAsyncDisposable
{
    public const int MaxDetentsPerGesture = 20;
    private static int _nextSubId = 1000;

    private readonly IPanelSimLink _link;
    private readonly ConcurrentDictionary<string, (double? Number, string? Text)> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, string> _sourceByHandle = [];
    private readonly Dictionary<string, string> _sourceErrors = new(StringComparer.Ordinal);
    private uint _subId;

    public PanelRuntime(PanelModel panel, IPanelSimLink link)
    {
        Panel = panel;
        _link = link;
        Widgets = [.. panel.Widgets.Select(w =>
        {
            var problems = PanelJson.ValidateWidget(panel, w).ToList();
            Comparison? when = null;
            if (w.When is not null) Comparison.TryParse(w.When, out when);
            return new WidgetState(w, when, problems);
        })];
    }

    public PanelModel Panel { get; }
    public IReadOnlyList<WidgetState> Widgets { get; }

    /// <summary>Uma variável mudou (pode vir de outra thread: a UI deve despachar para a thread dela).</summary>
    public event Action<string>? ValueChanged;

    /// <summary>Uma ação falhou no host (mensagem pronta para mostrar).</summary>
    public event Action<string>? ActionFailed;

    /// <summary>Erros de assinatura por source (ex.: variável inexistente, ponte WASM ausente).</summary>
    public IReadOnlyDictionary<string, string> SourceErrors => _sourceErrors;

    public async Task StartAsync(CancellationToken ct)
    {
        var sources = Widgets.Where(w => w.IsValid && w.Model.Source is not null)
            .GroupBy(w => w.Model.Source!, StringComparer.Ordinal)
            .Select(g => (Source: g.Key, Epsilon: g.Min(w => w.Model.Epsilon)))
            .ToList();
        if (sources.Count == 0) return;

        _subId = (uint)Interlocked.Increment(ref _nextSubId);
        var request = new SimSubscribe { SubId = _subId, MaxHz = 30 };
        request.Vars.AddRange(sources.Select(s => new SimVarSpec { Ref = s.Source, Epsilon = s.Epsilon }));

        _link.Values += OnValues;
        var response = await _link.SubscribeAsync(request, ct).ConfigureAwait(false);
        lock (_sourceByHandle)
        {
            for (var i = 0; i < response.Handles.Count && i < sources.Count; i++)
            {
                var h = response.Handles[i];
                if (h.Handle != 0) _sourceByHandle[h.Handle] = sources[i].Source;
                else _sourceErrors[sources[i].Source] = $"{h.Error?.Code}: {h.Error?.Message}";
            }
        }
    }

    // ------------------------------------------------------------------ o que cada widget mostra

    public bool HasValue(string source) => _values.ContainsKey(source);

    public double? Number(string? source) => source is not null && _values.TryGetValue(source, out var v) ? v.Number : null;

    public string? Text(string? source) => source is not null && _values.TryGetValue(source, out var v) ? v.Text : null;

    /// <summary>Texto do display (ou "---" sem valor ainda).</summary>
    public string DisplayText(WidgetState w)
    {
        var src = w.Model.Source;
        if (src is null || !_values.TryGetValue(src, out var v)) return "---";
        return ValueFormat.Format(w.Model.Format, v.Number, v.Text);
    }

    /// <summary>Luz do botão/anunciador ou estado do toggle.</summary>
    public bool IsLit(WidgetState w)
    {
        if (w.Model.Source is null) return w.Model.Type == WidgetTypes.Toggle && w.LocalOn;
        return Number(w.Model.Source) is { } n && Comparison.IsLit(w.When, n);
    }

    /// <summary>Posição do seletor pelo source (a posição de valor mais próximo); null sem valor.</summary>
    public int? SelectorIndex(WidgetState w)
    {
        if (Number(w.Model.Source) is not { } n) return null;
        var best = -1;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < w.Model.Positions.Count; i++)
        {
            if (w.Model.Positions[i].Value is not { } value) continue;
            var d = Math.Abs(value - n);
            if (d < bestDistance)
            {
                best = i;
                bestDistance = d;
            }
        }
        return best >= 0 ? best : null;
    }

    /// <summary>Fração 0..1 do mostrador; null sem valor.</summary>
    public double? GaugeFraction(WidgetState w) =>
        Number(w.Model.Source) is { } n ? ValueFormat.Scale(n, w.Model.Min, w.Model.Max, 0, 1) : null;

    // ------------------------------------------------------------------ ações vindas da UI

    /// <summary>Toque no botão: <c>press_off</c> se ele está aceso e a tem; senão <c>press</c>.</summary>
    public Task PressAsync(WidgetState w) => Run(w, w.Model.PressOff is not null && IsLit(w) ? w.Model.PressOff : w.Model.Press);

    public Task ReleaseAsync(WidgetState w) => Run(w, w.Model.Release);

    public Task PushAsync(WidgetState w) => Run(w, w.Model.Push);

    public Task SetToggleAsync(WidgetState w, bool on)
    {
        w.LocalOn = on;
        return Run(w, on ? w.Model.On : w.Model.Off);
    }

    /// <summary>Gira o encoder: um evento por detente, no máximo <see cref="MaxDetentsPerGesture"/> por gesto.</summary>
    public async Task RotateAsync(WidgetState w, int detents)
    {
        var action = detents > 0 ? w.Model.Inc : w.Model.Dec;
        var count = Math.Min(Math.Abs(detents), MaxDetentsPerGesture);
        for (var i = 0; i < count; i++) await Run(w, action).ConfigureAwait(false);
    }

    public Task SelectAsync(WidgetState w, int position) =>
        position >= 0 && position < w.Model.Positions.Count ? Run(w, w.Model.Positions[position].Action) : Task.CompletedTask;

    private async Task Run(WidgetState w, PanelAction? action)
    {
        if (!w.IsValid || action is null) return;
        try
        {
            var ack = await Execute(action).ConfigureAwait(false);
            if (!ack.Ok) ActionFailed?.Invoke($"{Describe(w)}: {ack.Error?.Code} {ack.Error?.Message}");
        }
        catch (Exception ex) when (ex is HostErrorException or IOException or OperationCanceledException or TimeoutException)
        {
            ActionFailed?.Invoke($"{Describe(w)}: {ex.Message}");
        }
    }

    private Task<SimAck> Execute(PanelAction a)
    {
        var ct = CancellationToken.None;
        if (a.Rpn is not null) return _link.FireAsync(new SimFireEvent { Ref = $"RPN:{a.Rpn}" }, ct);
        if (a.Fire is not null)
        {
            var fire = new SimFireEvent { Ref = a.Fire };
            fire.Params.AddRange(a.Args);
            return _link.FireAsync(fire, ct);
        }
        var set = new SimSetVar { Ref = a.Set! };
        if (a.Value is { ValueKind: JsonValueKind.String } s) set.Text = s.GetString() ?? "";
        else set.Number = a.Value?.GetDouble() ?? 0;
        return _link.SetAsync(set, ct);
    }

    private void OnValues(SimValues values)
    {
        if (values.SubId != _subId) return;
        foreach (var item in values.Items)
        {
            string? source;
            lock (_sourceByHandle) _sourceByHandle.TryGetValue(item.Handle, out source);
            if (source is null) continue;
            _values[source] = item.ValueCase == SimValue.ValueOneofCase.Text ? (null, item.Text) : (item.Number, null);
            ValueChanged?.Invoke(source);
        }
    }

    private static string Describe(WidgetState w) => string.IsNullOrEmpty(w.Model.Label) ? w.Model.Type : w.Model.Label!;

    public async ValueTask DisposeAsync()
    {
        _link.Values -= OnValues;
        if (_subId == 0) return;
        try
        {
            await _link.UnsubscribeAsync(_subId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HostErrorException or IOException or OperationCanceledException)
        {
            // Conexão já caiu: o host remove as assinaturas da sessão sozinho.
        }
    }
}
