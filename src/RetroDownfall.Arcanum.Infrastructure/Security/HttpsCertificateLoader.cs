using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// Loads the HTTPS certificate described by <see cref="HttpsSettings"/> at Kestrel bind time.
/// Supports two shapes: a PEM certificate + private key pair (<see cref="HttpsSettings.PrivateKeyPath"/>
/// set) and a PKCS#12 / PFX bundle (private key path empty). All failures are surfaced as a
/// <em>sanitized</em> public message (path + PFX/PEM + a generic reason) with the full exception logged
/// internally — the certificate password is never read into, echoed by, or logged in any message.
/// </summary>
public static class HttpsCertificateLoader
{
    /// <summary>
    /// Test seam: observes the unencrypted PKCS#12 export <see cref="LoadPem"/> rehydrates through
    /// on Windows, right after export and before it is zeroed. The branch runs only under
    /// <see cref="OperatingSystem.IsWindows"/>, so a non-Windows test cannot reach it through
    /// <see cref="Load"/> at all; this exists so a Windows run can assert the array was zeroed.
    /// </summary>
    internal static Action<byte[]>? ExportedPkcs12ObserverForTests { get; set; }

    public static HttpsCertificateLoadResult Load(
        HttpsSettings https,
        ILogger? logger = null)
    {

        string? certificatePath = HttpsCertificatePathResolver.Resolve(https.CertificatePath);

        if (string.IsNullOrWhiteSpace(certificatePath))
        {

            return HttpsCertificateLoadResult.Failure("HTTPS certificate path is not configured.");

        }

        bool usePem = !string.IsNullOrWhiteSpace(https.PrivateKeyPath);

        return usePem
            ? LoadPem(certificatePath, https.PrivateKeyPath!, logger)
            : LoadPfx(
                certificatePath,
                EnvironmentCredentialResolver.ResolveHttpsCertificatePassword(https),
                logger);

    }

    private static HttpsCertificateLoadResult LoadPem(string certificatePath, string keyPathRaw, ILogger? logger)
    {

        string? keyPath = HttpsCertificatePathResolver.Resolve(keyPathRaw);

        if (string.IsNullOrWhiteSpace(keyPath))
        {

            return HttpsCertificateLoadResult.Failure(
                FormatFailure(certificatePath, "PEM", "missing file"));

        }

        if (!File.Exists(certificatePath) || !File.Exists(keyPath))
        {

            return HttpsCertificateLoadResult.Failure(
                FormatFailure(certificatePath, "PEM", "missing file"));

        }

        try
        {

            X509Certificate2 pemCertificate = X509Certificate2.CreateFromPemFile(certificatePath, keyPath);

            // Windows Schannel cannot bind a certificate whose key lives only in an ephemeral (in-memory)
            // key set, which is what CreateFromPemFile produces. Round-tripping through a PKCS#12 export
            // rehydrates the certificate in a form Kestrel can use there; other platforms use it directly.
            if (OperatingSystem.IsWindows())
            {

                byte[] pkcs12 = pemCertificate.Export(X509ContentType.Pkcs12);

                // The try starts immediately after pkcs12 is populated: the observer invoke and
                // pemCertificate.Dispose() below are not exception-free, and a throw from either
                // one, before this method's own try/finally existed, would have skipped zeroing
                // a buffer that already held the unencrypted private key.
                try
                {

                    ExportedPkcs12ObserverForTests?.Invoke(pkcs12);

                    pemCertificate.Dispose();

                    X509Certificate2 rehydrated = X509CertificateLoader.LoadPkcs12(
                        pkcs12,
                        password: null,
                        keyStorageFlags: X509KeyStorageFlags.EphemeralKeySet);

                    return ValidateAndHold(rehydrated, certificatePath, "PEM", logger);

                }
                finally
                {

                    CryptographicOperations.ZeroMemory(pkcs12);

                }

            }

            return ValidateAndHold(pemCertificate, certificatePath, "PEM", logger);

        }
        catch (Exception ex)
        {

            logger?.LogError(ex, "Failed to load HTTPS PEM certificate from {CertificatePath}.", certificatePath);

            return HttpsCertificateLoadResult.Failure(
                FormatFailure(certificatePath, "PEM", "wrong password / unloadable certificate"));

        }

    }

    private static HttpsCertificateLoadResult LoadPfx(
        string certificatePath,
        string? password,
        ILogger? logger)
    {

        if (!File.Exists(certificatePath))
        {

            return HttpsCertificateLoadResult.Failure(
                FormatFailure(certificatePath, "PFX", "missing file"));

        }

        // macOS keychain-backed key import rejects the ephemeral key set; every other platform uses it
        // so the key is never persisted to a user or machine store during a short-lived host process.
        X509KeyStorageFlags keyStorageFlags = OperatingSystem.IsMacOS()
            ? X509KeyStorageFlags.DefaultKeySet
            : X509KeyStorageFlags.EphemeralKeySet;

        try
        {

            X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12FromFile(
                certificatePath,
                password,
                keyStorageFlags);

            return ValidateAndHold(certificate, certificatePath, "PFX", logger);

        }
        catch (Exception ex)
        {

            logger?.LogError(ex, "Failed to load HTTPS PFX certificate from {CertificatePath}.", certificatePath);

            return HttpsCertificateLoadResult.Failure(
                FormatFailure(certificatePath, "PFX", "wrong password / unloadable certificate"));

        }

    }

    private static HttpsCertificateLoadResult ValidateAndHold(
        X509Certificate2 certificate,
        string certificatePath,
        string mode,
        ILogger? logger)
    {

        if (!certificate.HasPrivateKey)
        {

            certificate.Dispose();

            return HttpsCertificateLoadResult.Failure(
                FormatFailure(certificatePath, mode, "no private key"));

        }

        if (certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
        {

            certificate.Dispose();

            return HttpsCertificateLoadResult.Failure(
                FormatFailure(certificatePath, mode, "expired certificate"));

        }

        if (certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow)
        {

            logger?.LogWarning(
                "HTTPS {Mode} certificate at {CertificatePath} is not yet valid (NotBefore={NotBefore:o}).",
                mode,
                certificatePath,
                certificate.NotBefore);

        }

        return HttpsCertificateLoadResult.Success(HttpsCertificateLifetime.Hold(certificate));

    }

    private static string FormatFailure(string certificatePath, string mode, string reason) =>
        $"HTTPS {mode} certificate at '{certificatePath}' could not be loaded ({reason}).";

}
