using System.Globalization;
using MonRem.Client.Core;
using MonRem.Protocol;
using MonRem.Protocol.V1;

// Cliente de linha de comando para testar o host sem o app.
//   monrem-dev <host>[:porta] sub "A:INDICATED ALTITUDE,feet" "L:A32NX_EFIS_L_ND_MODE"
//   monrem-dev <host> fire "K:AP_MASTER"            monrem-dev <host> fire "K:AP_ALT_VAR_SET_ENGLISH" 12000
//   monrem-dev <host> set "L:MY_VAR" 1              monrem-dev <host> status
// Na primeira conexão pede o PIN mostrado pelo host.

if (args.Length < 2)
{
    Console.Error.WriteLine("uso: monrem-dev <host>[:porta] <sub|fire|set|status|clients> [args...]");
    return 2;
}

var (hostName, port) = ParseHost(args[0]);
var command = args[1].ToLowerInvariant();
var rest = args[2..];

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
if (string.IsNullOrEmpty(appData)) appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
var dataDir = Path.Combine(appData, "MonRem", "devclient");
var identity = ClientIdentity.LoadOrCreate(dataDir);

await using var client = await ControlClient.ConnectAsync(hostName, port, identity, cts.Token);
if (client.PairedHost is null)
{
    Console.WriteLine("Host ainda não pareado. Veja o PIN no host.");
    await client.PairAsync(Environment.MachineName, Platform.Windows, _ =>
    {
        Console.Write("PIN: ");
        return Task.FromResult(Console.ReadLine() ?? "");
    }, ct: cts.Token);
    Console.WriteLine("Pareado.");
}

client.Notification += e =>
{
    switch (e.BodyCase)
    {
        case Envelope.BodyOneofCase.SimStatus:
            Console.WriteLine($"[sim] conectado={e.SimStatus.Connected} voo={e.SimStatus.InFlight} pausa={e.SimStatus.Paused} wasm={e.SimStatus.WasmBridge}");
            break;
        case Envelope.BodyOneofCase.SimAircraftChanged:
            Console.WriteLine($"[aeronave] {e.SimAircraftChanged.Title} / {e.SimAircraftChanged.LiveryName}");
            break;
        case Envelope.BodyOneofCase.Bye:
            Console.WriteLine($"[host] Bye: {e.Bye.Reason}");
            break;
    }
};

await client.OpenSessionAsync(Platform.Windows, "devclient", new Capabilities { CursorOverlay = true }, ct: cts.Token);
Console.WriteLine($"Sessão {client.SessionId}");

try
{
    switch (command)
    {
        case "status":
            var status = await client.RequestAsync(new Envelope { GetSimStatus = new GetSimStatus() }, cts.Token);
            Console.WriteLine(status.SimStatus);
            break;

        case "clients":
            var list = await client.RequestAsync(new Envelope { ListPairedClients = new ListPairedClients() }, cts.Token);
            foreach (var c in list.PairedClients.Items) Console.WriteLine($"{c.ClientId}  {c.DeviceName}  {c.Platform}");
            break;

        case "fire":
            var fire = new SimFireEvent { Ref = rest[0] };
            fire.Params.AddRange(rest[1..].Select(a => long.Parse(a, CultureInfo.InvariantCulture)));
            PrintAck((await client.RequestAsync(new Envelope { SimFireEvent = fire }, cts.Token)).SimAck);
            break;

        case "set":
            var set = double.TryParse(rest[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                ? new SimSetVar { Ref = rest[0], Number = number }
                : new SimSetVar { Ref = rest[0], Text = rest[1] };
            PrintAck((await client.RequestAsync(new Envelope { SimSetVar = set }, cts.Token)).SimAck);
            break;

        case "sub":
            var sub = new SimSubscribe { SubId = 1, MaxHz = 10 };
            sub.Vars.AddRange(rest.Select(r => new SimVarSpec { Ref = r }));
            var subscribed = (await client.RequestAsync(new Envelope { SimSubscribe = sub }, cts.Token)).SimSubscribed;
            var names = new Dictionary<uint, string>();
            for (var i = 0; i < subscribed.Handles.Count; i++)
            {
                var h = subscribed.Handles[i];
                if (h.Handle == 0) Console.WriteLine($"[erro] {rest[i]}: {h.Error.Code} {h.Error.Message}");
                else names[h.Handle] = rest[i];
            }
            client.Notification += e =>
            {
                if (e.BodyCase != Envelope.BodyOneofCase.SimValues) return;
                foreach (var v in e.SimValues.Items)
                {
                    var value = v.ValueCase == SimValue.ValueOneofCase.Text ? v.Text : v.Number.ToString("G", CultureInfo.InvariantCulture);
                    Console.WriteLine($"{names.GetValueOrDefault(v.Handle, v.Handle.ToString(CultureInfo.InvariantCulture))} = {value}");
                }
            };
            Console.WriteLine("Recebendo valores (Ctrl+C para sair)...");
            await Task.Delay(Timeout.Infinite, cts.Token);
            break;

        default:
            Console.Error.WriteLine($"comando desconhecido: {command}");
            return 2;
    }
}
catch (OperationCanceledException) when (cts.IsCancellationRequested)
{
}
catch (HostErrorException ex)
{
    Console.Error.WriteLine($"Erro do host: {ex.Message}");
    return 1;
}

await client.SendAsync(new Envelope { Bye = new Bye { Reason = ByeReason.User } });
return 0;

static void PrintAck(SimAck ack) => Console.WriteLine(ack.Ok ? "ok" : $"erro: {ack.Error.Code} {ack.Error.Message}");

static (string Host, int Port) ParseHost(string value)
{
    var idx = value.LastIndexOf(':');
    return idx > 0 && int.TryParse(value[(idx + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var p)
        ? (value[..idx], p)
        : (value, ProtocolConstants.DefaultControlPort);
}
