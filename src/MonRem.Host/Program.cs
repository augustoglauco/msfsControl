using System.Net;
using Microsoft.Extensions.Options;
using MonRem.Host;
using MonRem.Host.Core;
using MonRem.Host.Core.Security;
using MonRem.Host.Core.Session;
using MonRem.Host.Core.Sim;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.Configure<MonRemHostOptions>(builder.Configuration.GetSection(MonRemHostOptions.Section));

#if WINDOWS_SERVICE
builder.Services.AddWindowsService(o => o.ServiceName = "MonRem Host");
#endif

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IPinDisplay, LogPinDisplay>();
builder.Services.AddSingleton(SimBackends.CreateConnection);
// Pode resolver para null (sem ponte WASM): o HostBootstrap usa GetService.
builder.Services.AddSingleton<IWasmBridge>(sp => SimBackends.CreateWasm(sp)!);
builder.Services.AddSingleton(sp => HostBootstrap.Create(
    sp.GetRequiredService<IOptions<MonRemHostOptions>>().Value,
    sp.GetRequiredService<ISimConnection>(),
    sp.GetService<IWasmBridge>(),
    sp.GetRequiredService<IPinDisplay>(),
    sp.GetRequiredService<ILoggerFactory>(),
    sp.GetRequiredService<TimeProvider>()));
builder.Services.AddHostedService<HostWorker>();

await builder.Build().RunAsync();

namespace MonRem.Host
{
    /// <summary>Roda o canal de controle, o laço do hub de assinaturas e a limpeza de sessões.</summary>
    internal sealed class HostWorker(HostContext context, ILogger<HostWorker> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.LogInformation("Host {HostId} ({Name}), sim: {Sim}, WASM: {Wasm}",
                context.HostId, context.Options.HostName, context.Sim.Connection.Name, context.Sim.Wasm?.Name ?? "nenhum");

            var server = new ControlServer(context);
            var endpoint = new IPEndPoint(IPAddress.Any, context.Options.ControlPort);
            await Task.WhenAll(
                server.RunAsync(endpoint, null, stoppingToken),
                RunIgnoringCancel(context.Subscriptions.RunAsync, stoppingToken),
                RunIgnoringCancel(SweepAsync, stoppingToken));
        }

        private async Task SweepAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                context.Sessions.Sweep();
            }
        }

        private static async Task RunIgnoringCancel(Func<CancellationToken, Task> loop, CancellationToken ct)
        {
            try
            {
                await loop(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
        }
    }

    /// <summary>Escolhe os backends do sim: MSFS quando compilado com o SDK, senão o falso.</summary>
    internal static class SimBackends
    {
        public static ISimConnection CreateConnection(IServiceProvider sp)
        {
            var options = sp.GetRequiredService<IOptions<MonRemHostOptions>>().Value;
#if MSFS
            if (!options.UseFakeSim)
                return ActivatorUtilities.CreateInstance<MonRem.Host.Msfs.SimConnectBackend>(sp);
#endif
            sp.GetRequiredService<ILogger<HostWorker>>().LogWarning(
                options.UseFakeSim ? "Usando o simulador falso (UseFakeSim)." : "Compilado sem o SDK do MSFS: usando o simulador falso.");
            return new FakeSimConnection();
        }

        public static IWasmBridge? CreateWasm(IServiceProvider sp)
        {
#if MSFS
            var options = sp.GetRequiredService<IOptions<MonRemHostOptions>>().Value;
            if (!options.UseFakeSim && MonRem.Host.Msfs.WasimBridge.IsInstalled())
                return ActivatorUtilities.CreateInstance<MonRem.Host.Msfs.WasimBridge>(sp);
#endif
            return null;
        }
    }
}
