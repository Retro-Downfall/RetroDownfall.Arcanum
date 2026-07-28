using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Compendium.Ux.Services;

/// <summary>
/// Generates a self-signed <c>localhost</c> development certificate and writes it as a
/// password-protected PKCS#12 (PFX) bundle under <see cref="ArcanumPaths.CertificatesDirectory"/>,
/// owner-only. The certificate is a leaf (CA=false) with <c>serverAuth</c> EKU and localhost SANs,
/// suitable for local HTTPS. It is <em>not</em> trusted by any store — browsers and clients will warn
/// — and is intended for local development, never production.
/// </summary>
public sealed class LocalCertificateGenerator
{

    private const int ValidityDays = 397;

    public LocalCertificateResult Generate() =>
        Generate(ArcanumPaths.CertificatesDirectory, DateTimeOffset.UtcNow);

    internal LocalCertificateResult Generate(string certificatesDirectory, DateTimeOffset now)
    {

        SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(certificatesDirectory);

        using RSA rsa = RSA.Create(2048);

        CertificateRequest request = new(
            "CN=localhost",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                critical: true));

        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") },
                critical: false));

        SubjectAlternativeNameBuilder sanBuilder = new();

        sanBuilder.AddDnsName("localhost");

        sanBuilder.AddIpAddress(System.Net.IPAddress.Loopback);

        sanBuilder.AddIpAddress(System.Net.IPAddress.IPv6Loopback);

        request.CertificateExtensions.Add(sanBuilder.Build());

        DateTimeOffset notBefore = now.AddDays(-1);

        DateTimeOffset notAfter = now.AddDays(ValidityDays);

        using X509Certificate2 certificate = request.CreateSelfSigned(notBefore, notAfter);

        string password = GeneratePassword();

        byte[] pfxBytes = certificate.Export(X509ContentType.Pkcs12, password);

        string fileName = $"arcanum-localhost-{now:yyyyMMddHHmmss}.pfx";

        string certificatePath = Path.Combine(certificatesDirectory, fileName);

        File.WriteAllBytes(certificatePath, pfxBytes);

        CryptographicOperations.ZeroMemory(pfxBytes);

        SecureFilePermissions.ApplyOwnerOnlyFile(certificatePath);

        return new LocalCertificateResult(
            certificatePath,
            password,
            notAfter,
            certificate.Thumbprint,
            [
                "This certificate is self-signed. Clients may warn until you trust it manually. It is not installed into your OS trust store.",
                "The generated certificate is for local loopback development only (SANs: localhost, 127.0.0.1, ::1). If ListenAny is enabled and remote clients connect by hostname or IP, provide a certificate whose SAN includes that hostname or IP.",
                $"The certificate is valid until {notAfter:yyyy-MM-dd}. Regenerate it before it expires.",
                "The generated password is stored encrypted in arcanum.json. Keep the PFX file owner-only and treat the password as a secret.",
            ]);

    }

    private static string GeneratePassword()
    {

        byte[] entropy = RandomNumberGenerator.GetBytes(32);

        string password = Convert.ToBase64String(entropy);

        CryptographicOperations.ZeroMemory(entropy);

        return password;

    }

}

/// <summary>
/// Result of a local certificate generation: the on-disk PFX path, the randomly generated PFX
/// password (plaintext — the caller is responsible for encrypting it at rest), expiration,
/// thumbprint, and operator warnings to surface in the UI.
/// </summary>
public sealed record LocalCertificateResult(
    string CertificatePath,
    string Password,
    DateTimeOffset ExpiresAt,
    string Thumbprint,
    IReadOnlyList<string> Warnings);
