using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Coordination;

namespace RetroDownfall.Compendium.Ux.Services;

/// <summary>
/// Generates a self-signed <c>localhost</c> development certificate and owner-only PEM private key
/// under <see cref="ArcanumPaths.CertificatesDirectory"/>. The certificate is a leaf (CA=false) with
/// <c>serverAuth</c> EKU and localhost SANs, suitable for local HTTPS. It is <em>not</em> trusted by
/// any store — browsers and clients will warn — and is intended for local development, never
/// production.
/// </summary>
public sealed class LocalCertificateGenerator
{

    private const int ValidityDays = 397;

    private readonly IArcanumClientMutationBoundary? _mutationBoundary;

    public LocalCertificateGenerator(IArcanumClientMutationBoundary mutationBoundary)
    {

        _mutationBoundary = mutationBoundary
            ?? throw new ArgumentNullException(nameof(mutationBoundary));

    }

    internal LocalCertificateGenerator()
    {

    }

    public async Task<LocalCertificateResult> GenerateAsync(
        CancellationToken cancellationToken = default)
    {

        if (_mutationBoundary is null)
        {

            throw new InvalidOperationException(
                "Certificate generation requires the client-mutation boundary.");

        }

        ArcanumClientMutationResult<LocalCertificateResult> admitted =
            await _mutationBoundary
                .RunAsync(
                    admittedCancellationToken => Task.Run(
                        () => Generate(
                            ArcanumPaths.CertificatesDirectory,
                            DateTimeOffset.UtcNow),
                        admittedCancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);

        return admitted.IsCompleted
            ? admitted.Value
            : throw new InvalidOperationException(
                $"{admitted.Error.Code}: {admitted.Error.Message}");

    }

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

        string stem = $"arcanum-localhost-{now:yyyyMMddHHmmss}-{Guid.NewGuid():N}";

        string certificatePath = Path.Combine(certificatesDirectory, stem + ".crt");

        string privateKeyPath = Path.Combine(certificatesDirectory, stem + ".key");

        string certificateTempPath = certificatePath + ".tmp";

        string privateKeyTempPath = privateKeyPath + ".tmp";

        try
        {

            WriteDurableOwnerOnlyFile(
                certificateTempPath,
                System.Text.Encoding.ASCII.GetBytes(certificate.ExportCertificatePem()));

            byte[] keyBytes = System.Text.Encoding.ASCII.GetBytes(rsa.ExportPkcs8PrivateKeyPem());

            WriteDurableOwnerOnlyFile(privateKeyTempPath, keyBytes);

            File.Move(certificateTempPath, certificatePath, overwrite: false);

            File.Move(privateKeyTempPath, privateKeyPath, overwrite: false);

        }
        catch
        {

            TryDelete(certificatePath);

            TryDelete(privateKeyPath);

            throw;

        }
        finally
        {

            TryDelete(certificateTempPath);

            TryDelete(privateKeyTempPath);

        }

        // Validate that both files were created successfully
        if (!File.Exists(certificatePath))
        {

            throw new InvalidOperationException($"Failed to create certificate file at {certificatePath}");

        }

        if (!File.Exists(privateKeyPath))
        {

            throw new InvalidOperationException($"Failed to create private key file at {privateKeyPath}");

        }

        // Validate file sizes are reasonable
        FileInfo certInfo = new(certificatePath);

        FileInfo keyInfo = new(privateKeyPath);

        if (certInfo.Length == 0)
        {

            throw new InvalidOperationException($"Certificate file at {certificatePath} is empty");

        }

        if (keyInfo.Length == 0)
        {

            throw new InvalidOperationException($"Private key file at {privateKeyPath} is empty");

        }

        return new LocalCertificateResult(
            certificatePath,
            privateKeyPath,
            notAfter,
            certificate.Thumbprint,
            [
                "This certificate is self-signed. Clients may warn until you trust it manually. It is not installed into your OS trust store.",
                "The generated certificate is for local loopback development only (SANs: localhost, 127.0.0.1, ::1). If ListenAny is enabled and remote clients connect by hostname or IP, provide a certificate whose SAN includes that hostname or IP.",
                $"The certificate is valid until {notAfter:yyyy-MM-dd}. Regenerate it before it expires.",
                "The generated PEM private key is owner-only. Keep it private and do not copy it into configuration.",
            ]);

    }

    private static void WriteDurableOwnerOnlyFile(string path, byte[] contents)
    {

        using FileStream stream = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);

        SecureFilePermissions.ApplyOwnerOnlyFile(path);

        stream.Write(contents);

        stream.Flush(flushToDisk: true);

    }

    private static void TryDelete(string path)
    {

        try
        {

            File.Delete(path);

        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {

        }

    }

}

/// <summary>
/// Result of local certificate generation: owner-only certificate/private-key paths, expiration,
/// thumbprint, and operator warnings to surface in the UI.
/// </summary>
public sealed record LocalCertificateResult(
    string CertificatePath,
    string PrivateKeyPath,
    DateTimeOffset ExpiresAt,
    string Thumbprint,
    IReadOnlyList<string> Warnings);
