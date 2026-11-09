using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MonRem.Security;

public enum UdpChannel : byte
{
    Media = 1,
    Input = 2,
}

public enum FlowDirection : byte
{
    HostToClient = 1,
    ClientToHost = 2,
}

/// <summary>Chave AES-128-GCM e salt de nonce de um fluxo UDP.</summary>
public sealed record FlowKey(byte[] Key, byte[] Salt, uint Phase)
{
    /// <summary>Bit KEY_PHASE do cabeçalho para esta fase.</summary>
    public bool PhaseBit => (Phase & 1) == 1;

    /// <summary>Nonce de 12 bytes = salt(4) ‖ u64 big-endian(número do pacote).</summary>
    public byte[] Nonce(ulong packetNumber)
    {
        var nonce = new byte[12];
        Salt.CopyTo(nonce, 0);
        BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), packetNumber);
        return nonce;
    }
}

/// <summary>
/// Troca efêmera ECDH P-256 do Hello/HelloAck e derivação das chaves UDP.
/// S = HKDF-SHA256(ECDH, salt = session_id, info = "monrem-v1 session").
/// Fluxo: key(16) ‖ salt(4) = HKDF-Expand(S, "monrem-v1 flow" ‖ canal ‖ stream_id ‖ direção ‖ fase(u32 BE), 20).
/// </summary>
public static class SessionKeys
{
    public const int PublicKeyLength = 65;
    private static readonly byte[] SessionInfo = "monrem-v1 session"u8.ToArray();
    private static readonly byte[] FlowLabel = "monrem-v1 flow"u8.ToArray();

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

    public static FlowKey DeriveFlow(byte[] sessionSecret, UdpChannel channel, byte streamId, FlowDirection direction, uint phase = 0)
    {
        var info = new byte[FlowLabel.Length + 3 + 4];
        FlowLabel.CopyTo(info, 0);
        var o = FlowLabel.Length;
        info[o] = (byte)channel;
        info[o + 1] = streamId;
        info[o + 2] = (byte)direction;
        BinaryPrimitives.WriteUInt32BigEndian(info.AsSpan(o + 3), phase);

        var okm = HKDF.Expand(HashAlgorithmName.SHA256, sessionSecret, 20, info);
        return new FlowKey(okm[..16], okm[16..], phase);
    }
}
