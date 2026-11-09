using System.Security.Cryptography;
using System.Text;
using MonRem.Security;
using Org.BouncyCastle.Math;

namespace MonRem.Tests;

public class Spake2Tests
{
    // RFC 9382, Apêndice B, primeiro vetor P-256 (A = "server", B = "client", sem AAD).
    private static readonly BigInteger W = new("2ee57912099d31560b3a44b1184b9b4866e904c49d12ac5042c97dca461b1a5f", 16);
    private static readonly BigInteger X = new("43dd0fd7215bdcb482879fca3220c6a968e66d70b1356cac18bb26c84a78d729", 16);
    private static readonly BigInteger Y = new("dcb60106f276b02606d8ef0a328c02e4b629f84f89786af5befb0bc75b6e66be", 16);

    private const string ExpectedPA = "04a56fa807caaa53a4d28dbb9853b9815c61a411118a6fe516a8798434751470f9010153ac33d0d5f2047ffdb1a3e42c9b4e6be662766e1eeb4116988ede5f912c";
    private const string ExpectedPB = "0406557e482bd03097ad0cbaa5df82115460d951e3451962f1eaf4367a420676d09857ccbc522686c83d1852abfa8ed6e4a1155cf8f1543ceca528afb591a1e0b7";
    private const string ExpectedKe = "0e0672dc86f8e45565d338b0540abe69";
    private const string ExpectedConfA = "58ad4aa88e0b60d5061eb6b5dd93e80d9c4f00d127c65b3b35b1b5281fee38f0";
    private const string ExpectedConfB = "d3e2e547f1ae04f2dbdbf0fc4b79f8ecff2dff314b5d32fe9fcef2fb26dc459b";

    [Fact]
    public void Rfc9382_P256_vector_matches()
    {
        var idA = Encoding.ASCII.GetBytes("server");
        var idB = Encoding.ASCII.GetBytes("client");
        var a = new Spake2(Spake2Role.A, idA, idB, W, X);
        var b = new Spake2(Spake2Role.B, idA, idB, W, Y);

        Assert.Equal(ExpectedPA, Convert.ToHexStringLower(a.OwnShare));
        Assert.Equal(ExpectedPB, Convert.ToHexStringLower(b.OwnShare));

        var ra = a.Finish(b.OwnShare, []);
        var rb = b.Finish(a.OwnShare, []);

        Assert.Equal(ExpectedKe, Convert.ToHexStringLower(ra.SharedKey));
        Assert.Equal(ExpectedKe, Convert.ToHexStringLower(rb.SharedKey));
        Assert.Equal(ExpectedConfA, Convert.ToHexStringLower(ra.OwnConfirmation));
        Assert.Equal(ExpectedConfB, Convert.ToHexStringLower(rb.OwnConfirmation));
        Assert.True(ra.VerifyPeer(rb.OwnConfirmation));
        Assert.True(rb.VerifyPeer(ra.OwnConfirmation));
    }

    [Fact]
    public void Same_pin_agrees_and_wrong_pin_fails()
    {
        var aad = RandomNumberGenerator.GetBytes(64);
        var wOk = Spake2.DeriveW("123456", "cli", "host", iterations: 1000);
        var wBad = Spake2.DeriveW("123457", "cli", "host", iterations: 1000);

        var a = new Spake2(Spake2Role.A, "cli", "host", wOk);
        var b = new Spake2(Spake2Role.B, "cli", "host", wOk);
        var ra = a.Finish(b.OwnShare, aad);
        var rb = b.Finish(a.OwnShare, aad);
        Assert.Equal(ra.SharedKey, rb.SharedKey);
        Assert.True(rb.VerifyPeer(ra.OwnConfirmation));

        var a2 = new Spake2(Spake2Role.A, "cli", "host", wBad);
        var b2 = new Spake2(Spake2Role.B, "cli", "host", wOk);
        var ra2 = a2.Finish(b2.OwnShare, aad);
        var rb2 = b2.Finish(a2.OwnShare, aad);
        Assert.False(rb2.VerifyPeer(ra2.OwnConfirmation));
        Assert.False(ra2.VerifyPeer(rb2.OwnConfirmation));
    }

    [Fact]
    public void Different_aad_fails_confirmation()
    {
        var w = Spake2.DeriveW("000000", "cli", "host", iterations: 1000);
        var a = new Spake2(Spake2Role.A, "cli", "host", w);
        var b = new Spake2(Spake2Role.B, "cli", "host", w);
        var ra = a.Finish(b.OwnShare, [1, 2, 3]);
        var rb = b.Finish(a.OwnShare, [9, 9, 9]);
        Assert.False(rb.VerifyPeer(ra.OwnConfirmation));
    }

    [Fact]
    public void Rejects_invalid_points()
    {
        var w = Spake2.DeriveW("000000", "cli", "host", iterations: 1000);
        Assert.Throws<CryptographicException>(() => new Spake2(Spake2Role.A, "cli", "host", w).Finish(new byte[65], []));
        Assert.Throws<CryptographicException>(() => new Spake2(Spake2Role.A, "cli", "host", w).Finish(new byte[33], []));
        var notOnCurve = new byte[65];
        notOnCurve[0] = 4;
        notOnCurve[64] = 1;
        Assert.Throws<CryptographicException>(() => new Spake2(Spake2Role.A, "cli", "host", w).Finish(notOnCurve, []));
    }

    [Fact]
    public void Cannot_finish_twice()
    {
        var w = Spake2.DeriveW("000000", "cli", "host", iterations: 1000);
        var a = new Spake2(Spake2Role.A, "cli", "host", w);
        var b = new Spake2(Spake2Role.B, "cli", "host", w);
        a.Finish(b.OwnShare, []);
        Assert.Throws<InvalidOperationException>(() => a.Finish(b.OwnShare, []));
    }
}
