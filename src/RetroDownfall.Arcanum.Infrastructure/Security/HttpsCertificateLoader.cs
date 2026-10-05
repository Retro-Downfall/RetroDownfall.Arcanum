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
                X509Certificate2 rehydrated = RehydrateThroughPkcs12(
                    pemCertificate,
                    static certificate => certificate.Export(X509ContentType.Pkcs12),
                    PemRehydrationKeyStorageFlags);

                return ValidateAndHold(rehydrated, certificatePath, "PEM", logger);
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

    /// <summary>
    /// Key storage used when a PEM certificate is rehydrated through PKCS#12 on Windows. Schannel cannot
    /// bind an ephemeral key, so this must not include <see cref="X509KeyStorageFlags.EphemeralKeySet"/>;
    /// the default key set keeps the key in a process-lifetime container that is released when the
    /// certificate is disposed.
    /// </summary>
    internal static X509KeyStorageFlags PemRehydrationKeyStorageFlags =>
        X509KeyStorageFlags.DefaultKeySet;

    /// <summary>
    /// Re-imports <paramref name="pemCertificate"/> through an unencrypted PKCS#12 export. The export
    /// buffer is zeroed on every path and <paramref name="pemCertificate"/> is disposed whether or not
    /// the export or the round trip succeeds. Separated from the Windows-only call site so the disposal
    /// and zeroing contract is testable on any platform.
    /// </summary>
    internal static X509Certificate2 RehydrateThroughPkcs12(
        X509Certificate2 pemCertificate,
        Func<X509Certificate2, byte[]> exportPkcs12,
        X509KeyStorageFlags keyStorageFlags)
    {
        try
        {
            byte[] pkcs12 = exportPkcs12(pemCertificate);

            // The try starts immediately after pkcs12 is populated: the observer invoke below is not
            // exception-free, and a throw from it, or from the import, must still zero a buffer that
            // already holds the unencrypted private key.
            try
            {
                ExportedPkcs12ObserverForTests?.Invoke(pkcs12);

                return X509CertificateLoader.LoadPkcs12(
                    pkcs12,
                    password: null,
                    keyStorageFlags: keyStorageFlags);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pkcs12);
            }
        }
        finally
        {
            // The ephemeral PEM-loaded certificate has served its purpose on every path, including a
            // failed Export, so it never outlives this call.
            pemCertificate.Dispose();
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
