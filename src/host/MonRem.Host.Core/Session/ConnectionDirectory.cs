using MonRem.Protocol.V1;

namespace MonRem.Host.Core.Session;

/// <summary>
/// Conexões ativas por client_id e contagem de conexões ainda sem pareamento.
/// Usado para derrubar na hora a sessão de um cliente despareado.
/// </summary>
public sealed class ConnectionDirectory
{
    /// <summary>Teto de conexões simultâneas de certificados desconhecidos (só podem tentar parear).</summary>
    public const int MaxUnpaired = 4;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, List<Func<ByeReason, Task>>> _byClient = [];
    private int _unpaired;

    public int UnpairedCount
    {
        get { lock (_lock) return _unpaired; }
    }

    public bool TryEnterUnpaired()
    {
        lock (_lock)
        {
            if (_unpaired >= MaxUnpaired) return false;
            _unpaired++;
            return true;
        }
    }

    public void LeaveUnpaired()
    {
        lock (_lock) _unpaired = Math.Max(0, _unpaired - 1);
    }

    public IDisposable Register(string clientId, Func<ByeReason, Task> close)
    {
        lock (_lock)
        {
            if (!_byClient.TryGetValue(clientId, out var list)) _byClient[clientId] = list = [];
            list.Add(close);
        }
        return new Registration(this, clientId, close);
    }

    /// <summary>Encerra todas as conexões deste cliente.</summary>
    public async Task CloseClientAsync(string clientId, ByeReason reason)
    {
        Func<ByeReason, Task>[] targets;
        lock (_lock)
            targets = _byClient.TryGetValue(clientId, out var list) ? [.. list] : [];
        foreach (var close in targets) await close(reason).ConfigureAwait(false);
    }

    private void Unregister(string clientId, Func<ByeReason, Task> close)
    {
        lock (_lock)
        {
            if (!_byClient.TryGetValue(clientId, out var list)) return;
            list.Remove(close);
            if (list.Count == 0) _byClient.Remove(clientId);
        }
    }

    private sealed class Registration(ConnectionDirectory owner, string clientId, Func<ByeReason, Task> close) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) owner.Unregister(clientId, close);
        }
    }
}
