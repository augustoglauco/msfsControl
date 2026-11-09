using System.Security.Cryptography;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto.EC;
using Org.BouncyCastle.Math;
using ECPoint = Org.BouncyCastle.Math.EC.ECPoint;

namespace MonRem.Security;

/// <summary>
/// ECDH P-256 efêmero em BouncyCastle: mesmo resultado no Windows, Linux e Android
/// (o ECDiffieHellman do .NET depende do crypto do sistema e diverge nos erros e no suporte).
/// O segredo é a coordenada X de d·Q (32 bytes, big-endian), como no ECDH "raw" padrão.
/// </summary>
public sealed class EphemeralKey : IDisposable
{
    private static readonly X9ECParameters Params = CustomNamedCurves.GetByName("secp256r1");
    private BigInteger? _d;

    private EphemeralKey(BigInteger d)
    {
        _d = d;
        PublicKey = Params.G.Multiply(d).Normalize().GetEncoded(false);
    }

    /// <summary>Ponto público não comprimido (65 bytes, 0x04 ‖ X ‖ Y).</summary>
    public byte[] PublicKey { get; }

    public static EphemeralKey Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(40); // 64 bits extras: redução sem viés
        var d = new BigInteger(1, bytes).Mod(Params.N.Subtract(BigInteger.One)).Add(BigInteger.One);
        CryptographicOperations.ZeroMemory(bytes);
        return new EphemeralKey(d);
    }

    /// <summary>Segredo compartilhado com o ponto do outro lado (validado: na curva, fora do infinito).</summary>
    public byte[] Agree(ReadOnlySpan<byte> peerPublic)
    {
        var d = _d ?? throw new ObjectDisposedException(nameof(EphemeralKey));
        if (peerPublic.Length != SessionKeys.PublicKeyLength || peerPublic[0] != 0x04)
            throw new CryptographicException("key_share deve ser P-256 não comprimido (65 bytes)");

        ECPoint peer;
        try
        {
            peer = Params.Curve.DecodePoint(peerPublic.ToArray());
        }
        catch (ArgumentException ex)
        {
            throw new CryptographicException("key_share fora da curva P-256", ex);
        }
        if (peer.IsInfinity || !peer.IsValid()) throw new CryptographicException("key_share fora da curva P-256");

        var shared = peer.Multiply(d).Normalize();
        if (shared.IsInfinity) throw new CryptographicException("ECDH resultou no infinito");

        var x = shared.AffineXCoord.ToBigInteger().ToByteArrayUnsigned();
        var result = new byte[32];
        x.CopyTo(result, 32 - x.Length);
        return result;
    }

    public void Dispose() => _d = null;
}
