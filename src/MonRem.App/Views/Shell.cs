using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MonRem.Client.Core;
using MonRem.Client.Core.Panels;
using MonRem.Protocol;
using MonRem.Protocol.Discovery;
using MonRem.Protocol.V1;

namespace MonRem.App.Views;

/// <summary>
/// Telas do app: hosts → (pareamento) → painéis. Toda a lógica de rede fica no AppSession (Client.Core).
/// </summary>
public sealed class Shell : UserControl
{
    private const string AppVersion = "0.1";

    private readonly ContentControl _content = new();
    private readonly TextBlock _toast = new()
    {
        Foreground = Brushes.White,
        Background = new SolidColorBrush(Color.Parse("#C0392B")),
        Padding = new Thickness(12, 8),
        IsVisible = false,
        TextWrapping = TextWrapping.Wrap,
    };

    private ClientIdentity? _identity;
    private AppSession? _session;
    private readonly List<PanelRuntime> _runtimes = [];
    private CancellationTokenSource _cts = new();

    public Shell()
    {
        var root = new DockPanel();
        DockPanel.SetDock(_toast, Dock.Bottom);
        root.Children.Add(_toast);
        root.Children.Add(_content);
        Content = root;
        Background = new SolidColorBrush(Color.Parse("#0F1114"));
        AttachedToVisualTree += (_, _) => ShowHosts();
    }

    private ClientIdentity Identity => _identity ??= ClientIdentity.LoadOrCreate(DataDirectory());

    // ------------------------------------------------------------------ tela 1: hosts

    private void ShowHosts(string? message = null)
    {
        var list = new StackPanel { Spacing = 8 };
        var status = new TextBlock { Text = "Procurando hosts na rede...", Foreground = Brushes.Gray };
        var manual = new TextBox { PlaceholderText = "IP do host (ex.: 192.168.0.10)", Width = 260 };
        var connectManual = new Button { Content = "Conectar" };
        connectManual.Click += (_, _) =>
        {
            var (address, port) = ParseAddress(manual.Text ?? "");
            if (address.Length > 0) _ = ConnectAsync(address, port);
        };
        var refresh = new Button { Content = "Procurar de novo" };
        refresh.Click += (_, _) => _ = DiscoverAsync(list, status);

        var page = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 16,
            MaxWidth = 640,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                new TextBlock { Text = "Monitor Remoto", FontSize = 28, FontWeight = FontWeight.SemiBold },
                status,
                list,
                refresh,
                new TextBlock { Text = "Ou digite o endereço:", Foreground = Brushes.Gray },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { manual, connectManual } },
            },
        };
        _content.Content = new ScrollViewer { Content = page };
        if (message is not null) ShowToast(message);
        _ = DiscoverAsync(list, status);
    }

    private async Task DiscoverAsync(StackPanel list, TextBlock status)
    {
        status.Text = "Procurando hosts na rede...";
        list.Children.Clear();
        IReadOnlyList<DiscoveredHost> found;
        try
        {
            found = await HostBrowser.BrowseAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException)
        {
            status.Text = $"Busca na rede falhou: {ex.Message}. Use o IP.";
            return;
        }

        var paired = Identity.Hosts.ToDictionary(h => h.HostId);
        foreach (var h in found)
        {
            var known = paired.ContainsKey(h.HostId);
            var button = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = h.Name, FontSize = 18 },
                        new TextBlock
                        {
                            Text = $"{h.Address}:{h.Port}  ·  sim {(h.SimConnected ? "conectado" : "desconectado")}  ·  {(known ? "pareado" : "novo")}",
                            Foreground = Brushes.Gray,
                        },
                    },
                },
            };
            button.Click += (_, _) => _ = ConnectAsync(h.Address.ToString(), h.Port);
            list.Children.Add(button);
        }

        // Pareados que não responderam à busca (rede sem multicast): último endereço conhecido.
        foreach (var p in paired.Values.Where(p => found.All(f => f.HostId != p.HostId)))
        {
            var (address, port) = ParseAddress(p.LastAddress);
            var button = new Button { Content = $"{p.LastAddress}  (pareado, não visto agora)", HorizontalAlignment = HorizontalAlignment.Stretch };
            button.Click += (_, _) => _ = ConnectAsync(address, port);
            list.Children.Add(button);
        }

        status.Text = list.Children.Count == 0 ? "Nenhum host encontrado. Confira se o host está rodando ou digite o IP." : "Toque num host para conectar:";
    }

    // ------------------------------------------------------------------ conexão e pareamento

    private async Task ConnectAsync(string address, int port)
    {
        await CloseSessionAsync();
        _cts = new CancellationTokenSource();
        _content.Content = Centered($"Conectando a {address}...");
        try
        {
            var session = await AppSession.ConnectAsync(address, port, Identity, _cts.Token);
            _session = session;
            if (session.NeedsPairing)
            {
                await session.PairAsync(DeviceName(), _ => AskPinAsync(session.HostFingerprint), _cts.Token);
            }
            await session.OpenAsync(AppVersion, Capabilities(), _cts.Token);
            session.AircraftChanged += layoutId => Dispatcher.UIThread.Post(() => OnAircraftChanged(layoutId));
            session.LayoutsChanged += layouts => Dispatcher.UIThread.Post(() => _ = ShowPanelsAsync(null));
            await ShowPanelsAsync(session.SuggestedLayoutId);
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or HostErrorException
                                       or PairingFailedException or OperationCanceledException or System.Security.Authentication.AuthenticationException)
        {
            await CloseSessionAsync();
            ShowHosts(Explain(ex));
        }
    }

    private Task<string> AskPinAsync(string fingerprint)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() =>
        {
            var pin = new TextBox { PlaceholderText = "PIN de 6 dígitos", MaxLength = ProtocolConstants.PinDigits, FontSize = 28, Width = 220 };
            var ok = new Button { Content = "Parear", IsDefault = true };
            var cancel = new Button { Content = "Cancelar" };
            ok.Click += (_, _) => tcs.TrySetResult(pin.Text ?? "");
            cancel.Click += (_, _) =>
            {
                _cts.Cancel();
                tcs.TrySetCanceled();
            };
            _content.Content = new StackPanel
            {
                Margin = new Thickness(24),
                Spacing = 16,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = "Parear com este host", FontSize = 24 },
                    new TextBlock { Text = "Digite o PIN que apareceu no host.", Foreground = Brushes.Gray },
                    new TextBlock { Text = $"Certificado do host: {Group(fingerprint[..16])}", Foreground = Brushes.Gray, FontFamily = new FontFamily("monospace") },
                    pin,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { ok, cancel } },
                },
            };
            pin.Focus();
        });
        return tcs.Task;
    }

    // ------------------------------------------------------------------ tela 3: painéis

    private async Task ShowPanelsAsync(string? layoutId)
    {
        var session = _session;
        if (session is null) return;
        var layouts = await session.GetLayoutsAsync(_cts.Token);
        if (layouts.Count == 0)
        {
            _content.Content = Centered("Nenhum layout no host.\nCrie um JSON em %ProgramData%\\MonRem\\layouts (veja docs/paineis-do-app.md).");
            return;
        }

        var chosen = layouts.FirstOrDefault(l => l.LayoutId == layoutId) ?? layouts[0];
        var panels = await session.LoadLayoutAsync(chosen.LayoutId, _cts.Token);
        await DisposeRuntimesAsync();

        var tabs = new TabControl();
        foreach (var panel in panels)
        {
            var runtime = session.CreateRuntime(panel);
            runtime.ActionFailed += msg => Dispatcher.UIThread.Post(() => ShowToast(msg));
            _runtimes.Add(runtime);
            await runtime.StartAsync(_cts.Token);
            foreach (var (source, error) in runtime.SourceErrors) ShowToast($"{source}: {error}");
            tabs.Items.Add(new TabItem { Header = panel.Name.Length > 0 ? panel.Name : panel.Id, Content = new PanelCanvas(runtime) });
        }

        var layoutPicker = new ComboBox { ItemsSource = layouts.Select(l => l.Name).ToList(), SelectedIndex = layouts.ToList().IndexOf(chosen) };
        layoutPicker.SelectionChanged += (_, _) =>
        {
            if (layoutPicker.SelectedIndex >= 0 && layouts[layoutPicker.SelectedIndex].LayoutId != chosen.LayoutId)
                _ = ShowPanelsAsync(layouts[layoutPicker.SelectedIndex].LayoutId);
        };
        var disconnect = new Button { Content = "Desconectar" };
        disconnect.Click += async (_, _) =>
        {
            await CloseSessionAsync();
            ShowHosts();
        };
        var aircraft = new TextBlock { Text = session.Aircraft?.Title ?? "", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center };

        var bar = new DockPanel { Margin = new Thickness(8, 4) };
        DockPanel.SetDock(disconnect, Dock.Right);
        DockPanel.SetDock(layoutPicker, Dock.Left);
        bar.Children.Add(disconnect);
        bar.Children.Add(layoutPicker);
        bar.Children.Add(new Border { Margin = new Thickness(12, 0), Child = aircraft });

        var page = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        page.Children.Add(bar);
        page.Children.Add(tabs);
        _content.Content = page;
    }

    private void OnAircraftChanged(string matchedLayoutId)
    {
        if (!string.IsNullOrEmpty(matchedLayoutId)) _ = ShowPanelsAsync(matchedLayoutId);
    }

    // ------------------------------------------------------------------ utilidades

    private async Task CloseSessionAsync()
    {
        await DisposeRuntimesAsync();
        if (_session is not null)
        {
            await _session.DisposeAsync();
            _session = null;
        }
    }

    private async Task DisposeRuntimesAsync()
    {
        foreach (var r in _runtimes) await r.DisposeAsync();
        _runtimes.Clear();
    }

    private void ShowToast(string message)
    {
        _toast.Text = message;
        _toast.IsVisible = true;
        DispatcherTimer.RunOnce(() => _toast.IsVisible = false, TimeSpan.FromSeconds(6));
    }

    private static Control Centered(string text) => new TextBlock
    {
        Text = text,
        FontSize = 18,
        TextWrapping = TextWrapping.Wrap,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = TextAlignment.Center,
        Margin = new Thickness(24),
    };

    private static Capabilities Capabilities() => new()
    {
        TouchPoints = OperatingSystem.IsAndroid() ? 10u : 0u,
        CursorOverlay = true,
        MaxDecodeStreams = 0, // vídeo ainda não implementado no app
    };

    private static string Explain(Exception ex) => ex switch
    {
        PairingFailedException p when p.RetryAfterSeconds > 0 => $"{p.Message}. Tente de novo em {p.RetryAfterSeconds} s.",
        PairingFailedException p => p.Message,
        HostErrorException h => $"O host recusou: {h.Error.Message}",
        OperationCanceledException => "Cancelado.",
        _ => $"Não foi possível conectar: {ex.Message}",
    };

    private static (string Address, int Port) ParseAddress(string value)
    {
        value = value.Trim();
        var idx = value.LastIndexOf(':');
        return idx > 0 && int.TryParse(value[(idx + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var p)
            ? (value[..idx], p)
            : (value, ProtocolConstants.DefaultControlPort);
    }

    private static string Group(string hex) => string.Join(' ', Enumerable.Range(0, hex.Length / 4).Select(i => hex.Substring(i * 4, 4)));

    private static string DeviceName() => OperatingSystem.IsAndroid() ? "Android" : Environment.MachineName;

    private static string DataDirectory()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
        if (string.IsNullOrEmpty(baseDir)) baseDir = AppContext.BaseDirectory;
        return Path.Combine(baseDir, "MonRem", "app");
    }
}
