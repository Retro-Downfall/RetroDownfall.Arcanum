using System.Security.Cryptography.X509Certificates;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// Outcome of an <see cref="HttpsCertificateLoader"/> attempt. On success carries the loaded
/// certificate; on failure carries a sanitized, password-free message safe to surface to operators.
/// </summary>
public sealed record HttpsCertificateLoadResult
{

    private HttpsCertificateLoadResult(X509Certificate2? certificate, string? error)
    {

        Certificate = certificate;

        Error = error;

    }

    public X509Certificate2? Certificate { get; }

    public string? Error { get; }

    public bool IsSuccess => Certificate is not null;

    public static HttpsCertificateLoadResult Success(X509Certificate2 certificate) =>
        new(certificate, error: null);

    public static HttpsCertificateLoadResult Failure(string error) =>
        new(certificate: null, error);

}
