using MonRem.Client.Core.Panels;
using MonRem.Protocol.Panels;
using MonRem.Protocol.V1;
using MonRem.Security;

namespace MonRem.Client.Core;

/// <summary>
/// Fluxo do app sobre o <see cref="ControlClient"/>: conectar, parear se preciso, abrir sessão,
/// listar layouts e carregar painéis. Sem UI: a interface só chama e reage aos eventos.
/// </summary>
public sealed class AppSession : IAsyncDisposable
{
    private readonly ControlClient _client;
    private readonly CancellationTokenSource _cts = new();

    private AppSession(ControlClient client, string address, int port)
    {
        _client = client;
        Address = address;
        Port = port;
        Link = new ControlClientSimLink(client);
        _client.Notification += OnNotification;
        _client.Disconnected += ex => Disconnected?.Invoke(ex);
    }

    public string Address { get; }
    public int Port { get; }
    public ControlClientSimLink Link { get; }

    /// <summary>Host ainda não pareado: chame <see cref="PairAsync"/> antes de <see cref="OpenAsync"/>.</summary>
    public bool NeedsPairing => _client.PairedHost is null;

    /// <summary>SHA-256 do certificado do host, para o usuário conferir com o que o host mostra.</summary>
    public string HostFingerprint => Certificates.Fingerprint(_client.ServerCertificate);

    /// <summary>Layout indicado pelo host (aeronave atual); vazio se nenhum casar.</summary>
    public string SuggestedLayoutId { get; private set; } = "";

    public SimStatus? SimStatus { get; private set; }
    public SimAircraftChanged? Aircraft { get; private set; }

    /// <summary>Aeronave mudou; o argumento é o layout que casa (pode ser vazio).</summary>
    public event Action<string>? AircraftChanged;

    /// <summary>Layouts recarregados no host.</summary>
    public event Action<IReadOnlyList<LayoutSummary>>? LayoutsChanged;

    public event Action<SimStatus>? SimStatusChanged;

    /// <summary>Conexão perdida (o app pode reconectar com <see cref="ConnectAsync"/> ao mesmo endereço).</summary>
    public event Action<Exception>? Disconnected;

    public static async Task<AppSession> ConnectAsync(string address, int port, ClientIdentity identity, CancellationToken ct)
    {
        var client = await ControlClient.ConnectAsync(address, port, identity, ct).ConfigureAwait(false);
        return new AppSession(client, address, port);
    }

    /// <summary>Pareia; <paramref name="askPin"/> é chamado depois que o host mostra o PIN.</summary>
    public Task PairAsync(string deviceName, Func<string, Task<string>> askPin, CancellationToken ct) =>
        _client.PairAsync(deviceName, CurrentPlatform, askPin, ct: ct);

    public async Task OpenAsync(string appVersion, Capabilities capabilities, CancellationToken ct)
    {
        var ready = await _client.OpenSessionAsync(CurrentPlatform, appVersion, capabilities, ct: ct).ConfigureAwait(false);
        SuggestedLayoutId = ready.ActiveLayoutId;
    }

    public async Task<IReadOnlyList<LayoutSummary>> GetLayoutsAsync(CancellationToken ct)
    {
        var response = await _client.RequestAsync(new Envelope { GetLayouts = new GetLayouts() }, ct).ConfigureAwait(false);
        return [.. response.Layouts.Items];
    }

    /// <summary>Aplica o layout e baixa os painéis dele (painel com JSON inválido é pulado).</summary>
    public async Task<IReadOnlyList<PanelModel>> LoadLayoutAsync(string layoutId, CancellationToken ct)
    {
        var applied = (await _client.RequestAsync(new Envelope { ApplyLayout = new ApplyLayout { LayoutId = layoutId } }, ct).ConfigureAwait(false)).LayoutApplied;
        List<PanelModel> panels = [];
        foreach (var panelId in applied.PanelIds)
        {
            var def = (await _client.RequestAsync(new Envelope { GetPanelDefinition = new GetPanelDefinition { PanelId = panelId } }, ct).ConfigureAwait(false)).PanelDefinition;
            if (def.Schema != PanelJson.Schema) continue;
            try
            {
                panels.Add(PanelJson.ParsePanel(def.Json));
            }
            catch (System.Text.Json.JsonException)
            {
            }
        }
        return panels;
    }

    /// <summary>Lista os Input Events (B:) da aeronave atual no host (pode voltar vazia se o sim não respondeu a tempo).</summary>
    public async Task<IReadOnlyList<SimInputEvent>> ListInputEventsAsync(CancellationToken ct)
    {
        var response = await _client.RequestAsync(new Envelope { SimListInputEvents = new SimListInputEvents() }, ct).ConfigureAwait(false);
        return [.. response.SimInputEventList.Items];
    }

    public PanelRuntime CreateRuntime(PanelModel panel) => new(panel, Link);

    public static Platform CurrentPlatform => OperatingSystem.IsAndroid() ? Platform.Android : Platform.Windows;

    private void OnNotification(Envelope e)
    {
        switch (e.BodyCase)
        {
            case Envelope.BodyOneofCase.SimAircraftChanged:
                Aircraft = e.SimAircraftChanged;
                AircraftChanged?.Invoke(e.SimAircraftChanged.MatchedLayoutId);
                break;
            case Envelope.BodyOneofCase.Layouts:
                LayoutsChanged?.Invoke([.. e.Layouts.Items]);
                break;
            case Envelope.BodyOneofCase.SimStatus:
                SimStatus = e.SimStatus;
                SimStatusChanged?.Invoke(e.SimStatus);
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _client.Notification -= OnNotification;
        Link.Dispose();
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _client.SendAsync(new Envelope { Bye = new Bye { Reason = ByeReason.User } }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
        await _client.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
