using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using MonRem.Host.Core.Sim;

namespace MonRem.Host.Core.Devices;

/// <summary>Painéis conectados. Cada enlace (porta serial, conexão Wi-Fi) vira uma <see cref="DeviceSession"/>.</summary>
public sealed class DeviceManager(
    SimRouter router,
    SubscriptionHub hub,
    DeviceProfileStore profiles,
    DescriptorCache descriptors,
    TimeProvider time,
    ILoggerFactory loggerFactory)
{
    private readonly ConcurrentDictionary<DeviceSession, byte> _active = new();

    public DeviceProfileStore Profiles { get; } = profiles;

    /// <summary>Painéis já identificados (passaram do DeviceHello).</summary>
    public IReadOnlyList<DeviceInfo> Devices => [.. _active.Keys.Select(s => s.Info).Where(i => i.DeviceId.Length > 0)];

    /// <summary>Recarrega os perfis do disco e reaplica nos painéis conectados.</summary>
    public void ReloadProfiles(string directory)
    {
        Profiles.Reload(directory);
        foreach (var session in _active.Keys) session.ReloadProfile();
    }

    /// <summary>Roda uma sessão até o enlace cair. Retorna true se era mesmo um painel (respondeu com DeviceHello).</summary>
    public async Task<bool> RunLinkAsync(IDeviceLink link, CancellationToken ct)
    {
        var session = new DeviceSession(link, router, hub, Profiles, descriptors, time, loggerFactory.CreateLogger<DeviceSession>());
        var identified = false;
        session.Identified += _ => identified = true;
        _active[session] = 0;
        try
        {
            await session.RunAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _active.TryRemove(session, out _);
            await link.DisposeAsync().ConfigureAwait(false);
        }
        return identified;
    }
}
