using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;

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
        WritePrivateFile(pfxPath, cert.Export(X509ContentType.Pkcs12));
        return cert;
    }

    /// <summary>
    /// Grava um arquivo com segredo legível só pelo dono: no Windows, ACL sem herança para SYSTEM,
    /// Administradores e o usuário atual; no Linux/Android/macOS, modo 600.
    /// </summary>
    public static void WritePrivateFile(string path, byte[] content)
    {
        if (OperatingSystem.IsWindows())
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var sid in new[]
            {
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                WindowsIdentity.GetCurrent().User!,
            })
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));

            using var stream = new FileInfo(path).Create(FileMode.Create, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.None, security);
            stream.Write(content);
            return;
        }

        using (var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        }))
        {
            stream.Write(content);
        }
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>SHA-256 do certificado DER, em hex minúsculo.</summary>
    public static string Fingerprint(X509Certificate cert) => Convert.ToHexStringLower(SHA256.HashData(cert.GetRawCertData()));

    /// <summary>AAD do SPAKE2: SHA-256(cert do host) ‖ SHA-256(cert do cliente), como vistos no TLS.</summary>
    public static byte[] PairingAad(X509Certificate hostCert, X509Certificate clientCert) =>
        [.. SHA256.HashData(hostCert.GetRawCertData()), .. SHA256.HashData(clientCert.GetRawCertData())];
}
