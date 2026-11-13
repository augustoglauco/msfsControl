using System.Security.Cryptography;
using System.Text;

namespace MonRem.Security;

/// <summary>
/// Troca efêmera ECDH P-256 do Hello/HelloAck.
/// S = HKDF-SHA256(ECDH, salt = session_id, info = "monrem-v1 session").
/// </summary>
public static class SessionKeys
{
    public const int PublicKeyLength = 65;
    private static readonly byte[] SessionInfo = "monrem-v1 session"u8.ToArray();

    /// <summary>Par efêmero P-256 para o key_share do Hello/HelloAck.</summary>
    public static EphemeralKey CreateEphemeral() => EphemeralKey.Generate();

    public static byte[] ExportPublic(EphemeralKey key) => key.PublicKey;

    public static byte[] DeriveSessionSecret(EphemeralKey own, ReadOnlySpan<byte> peerPublic, string sessionId)
    {
        var shared = own.Agree(peerPublic);
        try
        {
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, Encoding.UTF8.GetBytes(sessionId), SessionInfo);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(shared);
        }
    }
}
