using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto.EC;
using Org.BouncyCastle.Math;
using ECPoint = Org.BouncyCastle.Math.EC.ECPoint;

namespace MonRem.Security;

public enum Spake2Role
{
    /// <summary>Parte A (no app: o cliente).</summary>
    A,
    /// <summary>Parte B (no app: o host).</summary>
    B,
}

/// <summary>Resultado do SPAKE2: chave compartilhada e confirmações.</summary>
public sealed record Spake2Result(byte[] SharedKey, byte[] OwnConfirmation, byte[] ExpectedPeerConfirmation)
{
    /// <summary>Compara a confirmação recebida em tempo constante.</summary>
    public bool VerifyPeer(ReadOnlySpan<byte> peerConfirmation) =>
        CryptographicOperations.FixedTimeEquals(peerConfirmation, ExpectedPeerConfirmation);
}

/// <summary>
/// SPAKE2 conforme RFC 9382, suíte P-256 + SHA-256 + HKDF-SHA256 + HMAC-SHA256.
/// Uma instância = uma execução do protocolo (não reutilizar).
/// </summary>
public sealed class Spake2
{
    private static readonly X9ECParameters Params = CustomNamedCurves.GetByName("secp256r1");
    private static readonly ECPoint G = Params.G;
    private static readonly BigInteger Order = Params.N;
    private static readonly BigInteger Cofactor = Params.H;

    // RFC 9382 §6, pontos M e N de P-256 (SEC1 comprimido).
    private static readonly ECPoint M = Decode("02886e2f97ace46e55ba9dd7242579f2993b64e16ef3dcab95afd497333d8fa12f");
    private static readonly ECPoint N = Decode("03d8bbd6c639c62937b04d997f38c3770719c629d7014d49a24b4f98baa1292b49");

    private const int ScalarLength = 32;
    private static readonly byte[] ConfirmationKeysLabel = "ConfirmationKeys"u8.ToArray();

    private readonly Spake2Role _role;
    private readonly byte[] _idA;
    private readonly byte[] _idB;
    private readonly BigInteger _w;
    private readonly BigInteger _secret;
    private readonly byte[] _ownShare;
    private bool _finished;

    public Spake2(Spake2Role role, string idA, string idB, byte[] w)
        : this(role, Encoding.UTF8.GetBytes(idA), Encoding.UTF8.GetBytes(idB), new BigInteger(1, w).Mod(Order), RandomScalar())
    {
    }

    /// <summary>Construtor com escalar fixo: só para vetores de teste.</summary>
    internal Spake2(Spake2Role role, byte[] idA, byte[] idB, BigInteger w, BigInteger secret)
    {
        _role = role;
        _idA = idA;
        _idB = idB;
        _w = w;
        _secret = secret;
        var blind = role == Spake2Role.A ? M : N;
        _ownShare = G.Multiply(secret).Add(blind.Multiply(w)).Normalize().GetEncoded(false);
    }

    /// <summary>pA (se A) ou pB (se B), ponto não comprimido de 65 bytes.</summary>
    public byte[] OwnShare => (byte[])_ownShare.Clone();

    /// <summary>
    /// Deriva w a partir do PIN/código: PBKDF2-SHA256(pin, "monrem-v1" ‖ idA ‖ idB, 100000) com 40 bytes,
    /// 64 bits a mais que a ordem do grupo para eliminar viés na redução mod n (RFC 9382 §3.2).
    /// </summary>
    public static byte[] DeriveW(string pin, string idA, string idB, int iterations = 100_000)
    {
        var salt = Encoding.UTF8.GetBytes($"monrem-v1\0{idA}\0{idB}");
        return Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, iterations, HashAlgorithmName.SHA256, 40);
    }

    /// <summary>Conclui com a parte do outro lado. AAD entra nas chaves de confirmação.</summary>
    public Spake2Result Finish(ReadOnlySpan<byte> peerShare, ReadOnlySpan<byte> aad)
    {
        if (_finished) throw new InvalidOperationException("SPAKE2 já concluído");
        _finished = true;

        var peer = DecodeAndValidate(peerShare);
        var peerBlind = _role == Spake2Role.A ? N : M;
        // K = h · secret · (peer − w · blind)
        var k = peer.Subtract(peerBlind.Multiply(_w)).Multiply(_secret.Multiply(Cofactor)).Normalize();
        if (k.IsInfinity) throw new CryptographicException("SPAKE2: K no infinito");

        var pA = _role == Spake2Role.A ? _ownShare : peerShare.ToArray();
        var pB = _role == Spake2Role.B ? _ownShare : peerShare.ToArray();
        var tt = Transcript(_idA, _idB, pA, pB, k.GetEncoded(false), ToFixed(_w));

        var hash = SHA256.HashData(tt);
        var ke = hash[..16];
        var ka = hash[16..];

        var info = new byte[ConfirmationKeysLabel.Length + aad.Length];
        ConfirmationKeysLabel.CopyTo(info, 0);
        aad.CopyTo(info.AsSpan(ConfirmationKeysLabel.Length));
        var kc = HKDF.DeriveKey(HashAlgorithmName.SHA256, ka, 32, salt: [], info: info);
        var cA = HMACSHA256.HashData(kc[..16], tt);
        var cB = HMACSHA256.HashData(kc[16..], tt);

        return _role == Spake2Role.A ? new Spake2Result(ke, cA, cB) : new Spake2Result(ke, cB, cA);
    }

    internal static byte[] Transcript(params byte[][] fields)
    {
        var total = fields.Sum(f => 8 + f.Length);
        var tt = new byte[total];
        var offset = 0;
        foreach (var f in fields)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(tt.AsSpan(offset), (ulong)f.Length);
            offset += 8;
            f.CopyTo(tt, offset);
            offset += f.Length;
        }
        return tt;
    }

    private static ECPoint DecodeAndValidate(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length != 65 || encoded[0] != 0x04)
            throw new CryptographicException("SPAKE2: ponto deve ser P-256 não comprimido (65 bytes)");
        ECPoint p;
        try
        {
            p = Params.Curve.DecodePoint(encoded.ToArray());
        }
        catch (ArgumentException ex)
        {
            throw new CryptographicException("SPAKE2: ponto inválido", ex);
        }
        if (p.IsInfinity || !p.IsValid())
            throw new CryptographicException("SPAKE2: ponto fora da curva");
        return p;
    }

    private static byte[] ToFixed(BigInteger value)
    {
        var raw = value.ToByteArrayUnsigned();
        var fixedBytes = new byte[ScalarLength];
        raw.CopyTo(fixedBytes, ScalarLength - raw.Length);
        return fixedBytes;
    }

    private static BigInteger RandomScalar()
    {
        // 64 bits extras para redução sem viés; resultado em [1, n-1].
        var bytes = RandomNumberGenerator.GetBytes(ScalarLength + 8);
        return new BigInteger(1, bytes).Mod(Order.Subtract(BigInteger.One)).Add(BigInteger.One);
    }

    private static ECPoint Decode(string hex) => Params.Curve.DecodePoint(Convert.FromHexString(hex));
}
