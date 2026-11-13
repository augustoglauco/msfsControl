using System.Security.Cryptography;
using MonRem.Security;

namespace MonRem.Tests;

public class SessionKeysTests
{
    [Fact]
    public void Both_sides_derive_same_secret()
    {
        using var host = SessionKeys.CreateEphemeral();
        using var client = SessionKeys.CreateEphemeral();
        var s1 = SessionKeys.DeriveSessionSecret(host, SessionKeys.ExportPublic(client), "sess-1");
        var s2 = SessionKeys.DeriveSessionSecret(client, SessionKeys.ExportPublic(host), "sess-1");
        Assert.Equal(s1, s2);
        Assert.Equal(32, s1.Length);

        var other = SessionKeys.DeriveSessionSecret(client, SessionKeys.ExportPublic(host), "sess-2");
        Assert.NotEqual(s1, other);
    }

    [Fact]
    public void BouncyCastle_ecdh_matches_platform_ecdh()
    {
        using var ours = SessionKeys.CreateEphemeral();
        using var platform = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var p = platform.ExportParameters(false);
        byte[] platformPublic = [0x04, .. p.Q.X!, .. p.Q.Y!];

        var fromOurs = ours.Agree(platformPublic);

        using var oursAsPlatform = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = ours.PublicKey[1..33], Y = ours.PublicKey[33..] },
        });
        var fromPlatform = platform.DeriveRawSecretAgreement(oursAsPlatform.PublicKey);
        Assert.Equal(fromPlatform, fromOurs);
    }

    [Fact]
    public void Rejects_bad_peer_key()
    {
        using var own = SessionKeys.CreateEphemeral();
        Assert.Throws<CryptographicException>(() => SessionKeys.DeriveSessionSecret(own, new byte[10], "s"));
        var offCurve = new byte[65];
        offCurve[0] = 4;
        offCurve[64] = 7;
        Assert.ThrowsAny<CryptographicException>(() => SessionKeys.DeriveSessionSecret(own, offCurve, "s"));
    }
}
