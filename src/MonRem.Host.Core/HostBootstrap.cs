using Microsoft.Extensions.Logging;
using MonRem.Host.Core.Security;
using MonRem.Host.Core.Session;
using MonRem.Host.Core.Sim;
using MonRem.Security;

namespace MonRem.Host.Core;

/// <summary>Monta o <see cref="HostContext"/> a partir da pasta de dados.</summary>
public static class HostBootstrap
{
    public static HostContext Create(
        MonRemHostOptions options,
        ISimConnection simConnection,
        IWasmBridge? wasm,
        IPinDisplay pinDisplay,
        ILoggerFactory loggerFactory,
        TimeProvider? time = null)
    {
        time ??= TimeProvider.System;
        Directory.CreateDirectory(options.DataDirectory);

        var hostIdPath = Path.Combine(options.DataDirectory, "host-id.txt");
        if (!File.Exists(hostIdPath)) File.WriteAllText(hostIdPath, Guid.NewGuid().ToString("D"));
        var hostId = File.ReadAllText(hostIdPath).Trim();

        var cert = Certificates.LoadOrCreate(Path.Combine(options.DataDirectory, "host.pfx"), $"MonRem Host {options.HostName}");
        var router = new SimRouter(simConnection, wasm);

        return new HostContext(
            options,
            hostId,
            cert,
            new PairedClientStore(Path.Combine(options.DataDirectory, "paired-clients.json")),
            new PairingGuard(time),
            pinDisplay,
            new SessionRegistry(time),
            router,
            new SubscriptionHub(router, time, loggerFactory.CreateLogger<SubscriptionHub>()),
            time,
            loggerFactory);
    }
}
