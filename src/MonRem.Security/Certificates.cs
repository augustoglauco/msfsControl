using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MonRem.Security;

public static class Certificates
{
    /// <summary>Certificado autoassinado ECDSA P-256 com chave exportável, válido por 20 anos.</summary>
    public static X509Certificate2 CreateSelfSigned(string commonName)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={commonName}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2")], false)); // servidor e cliente TLS

        var now = DateTimeOffset.UtcNow.AddDays(-1);
        using var cert = request.CreateSelfSigned(now, now.AddYears(20));
        // Reimporta via PKCS#12 para a chave ficar utilizável pelo SslStream em todas as plataformas.
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pkcs12), password: null,
            X509KeyStorageFlags.Exportable);
    }

    public static X509Certificate2 LoadOrCreate(string pfxPath, string commonName)
    {
        if (File.Exists(pfxPath))
            return X509CertificateLoader.LoadPkcs12FromFile(pfxPath, password: null, X509KeyStorageFlags.Exportable);

        var cert = CreateSelfSigned(commonName);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(pfxPath))!);
        File.WriteAllBytes(pfxPath, cert.Export(X509ContentType.Pkcs12));
        return cert;
    }

    /// <summary>SHA-256 do certificado DER, em hex minúsculo.</summary>
    public static string Fingerprint(X509Certificate cert) => Convert.ToHexStringLower(SHA256.HashData(cert.GetRawCertData()));

    /// <summary>AAD do SPAKE2: SHA-256(cert do host) ‖ SHA-256(cert do cliente), como vistos no TLS.</summary>
    public static byte[] PairingAad(X509Certificate hostCert, X509Certificate clientCert) =>
        [.. SHA256.HashData(hostCert.GetRawCertData()), .. SHA256.HashData(clientCert.GetRawCertData())];
}
