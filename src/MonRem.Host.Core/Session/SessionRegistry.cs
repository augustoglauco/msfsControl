using System.Security.Cryptography;

namespace MonRem.Host.Core.Session;

/// <summary>
/// Sessões ativas e tokens de resume. Token: uso único, vale <see cref="Protocol.ProtocolConstants.ResumeWindow"/>
/// a partir da queda, e só para o mesmo client_id.
/// </summary>
public sealed class SessionRegistry(TimeProvider time)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, ResumeEntry> _tokens = [];
    private readonly Dictionary<string, string> _activeByClient = [];

    public event Action<string>? ResumeExpired;

    public (string SessionId, byte[] ResumeToken) Open(string clientId)
    {
        var sessionId = Guid.NewGuid().ToString("D");
        var token = RandomNumberGenerator.GetBytes(32);
        lock (_lock)
        {
            _activeByClient[clientId] = sessionId;
            _tokens[Convert.ToBase64String(token)] = new ResumeEntry(clientId, sessionId, null);
        }
        return (sessionId, token);
    }

    /// <summary>Consome o token. True se era deste cliente e a janela não venceu.</summary>
    public bool TryResume(string clientId, ReadOnlySpan<byte> token)
    {
        if (token.IsEmpty) return false;
        var key = Convert.ToBase64String(token);
        lock (_lock)
        {
            if (!_tokens.TryGetValue(key, out var entry) || entry.ClientId != clientId) return false;
            _tokens.Remove(key);
            return entry.DisconnectedAt is not { } at || time.GetUtcNow() - at <= Protocol.ProtocolConstants.ResumeWindow;
        }
    }

    public void MarkDisconnected(string clientId, string sessionId)
    {
        lock (_lock)
        {
            if (_activeByClient.GetValueOrDefault(clientId) == sessionId) _activeByClient.Remove(clientId);
            foreach (var (k, v) in _tokens.Where(t => t.Value.SessionId == sessionId).ToList())
                _tokens[k] = v with { DisconnectedAt = time.GetUtcNow() };
        }
    }

    /// <summary>Remove tokens vencidos; o host libera os recursos desses clientes (monitores, assinaturas).</summary>
    public void Sweep()
    {
        List<string> expired = [];
        lock (_lock)
        {
            var now = time.GetUtcNow();
            foreach (var (k, v) in _tokens.ToList())
            {
                if (v.DisconnectedAt is { } at && now - at > Protocol.ProtocolConstants.ResumeWindow)
                {
                    _tokens.Remove(k);
                    if (!_activeByClient.ContainsKey(v.ClientId)) expired.Add(v.ClientId);
                }
            }
        }
        foreach (var c in expired) ResumeExpired?.Invoke(c);
    }

    public bool IsActive(string clientId)
    {
        lock (_lock) return _activeByClient.ContainsKey(clientId);
    }

    private sealed record ResumeEntry(string ClientId, string SessionId, DateTimeOffset? DisconnectedAt);
}
