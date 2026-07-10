using System.Security.Cryptography.X509Certificates;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// Holds strong references to loaded HTTPS certificates for the lifetime of the process. Certificates
/// loaded with an ephemeral key set keep their private key alive only as long as the
/// <see cref="X509Certificate2"/> instance is reachable; Kestrel captures the certificate but does
/// not root it, so without this hold the key can be finalized out from under an in-flight TLS
/// handshake. The list is append-only and never cleared — a host loads at most a handful of
/// certificates over its lifetime.
/// </summary>
public static class HttpsCertificateLifetime
{

    private static readonly List<X509Certificate2> Held = [];

    private static readonly object Gate = new();

    public static X509Certificate2 Hold(X509Certificate2 certificate)
    {

        lock (Gate)
        {

            Held.Add(certificate);

        }

        return certificate;

    }

}
