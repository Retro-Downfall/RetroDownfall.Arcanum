namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Optional HTTPS/TLS binding for the Arcanum Kestrel host. Bound from <c>Arcanum:Host:Https</c>.
/// Disabled by default — the host keeps its plaintext HTTP loopback binding unchanged until an
/// operator opts in. When <see cref="Enabled"/> is <c>true</c>, Kestrel adds a second listener on
/// <see cref="Port"/> using the configured certificate; the plaintext HTTP listener is retained so
/// existing loopback callers are never broken by turning HTTPS on.
/// </summary>
public sealed record HttpsSettings
{

    /// <summary>
    /// Master toggle. When <c>false</c> (default), no HTTPS listener is added and none of the other
    /// fields have any effect — a complete no-op until an operator opts in.
    /// </summary>
    public bool Enabled { get; init; } = false;

    /// <summary>
    /// TLS listen port. Default <c>5443</c>; clamped 1&#8211;65535. Must differ from
    /// <see cref="HostSettings.Port"/> — the plaintext HTTP listener and the HTTPS listener cannot
    /// share a port.
    /// </summary>
    public int Port { get; init; } = 5443;

    /// <summary>
    /// Path to the certificate file. When <see cref="PrivateKeyPath"/> is set, this is a PEM
    /// certificate (chain) file paired with the private key. When <see cref="PrivateKeyPath"/> is
    /// empty, this is a PKCS#12 (<c>.pfx</c>/<c>.p12</c>) bundle carrying both certificate and key.
    /// Leading <c>~</c>, <c>~/</c>, and <c>~\</c> are expanded to the user profile directory; the
    /// result is resolved to a full path.
    /// </summary>
    public string? CertificatePath { get; init; }

    /// <summary>
    /// Optional path to a PEM private key file. When set, HTTPS is loaded from the PEM certificate at
    /// <see cref="CertificatePath"/> plus this key (<see cref="CertificatePassword"/> is ignored for
    /// PEM). When empty (default), <see cref="CertificatePath"/> is treated as a PKCS#12 bundle.
    /// </summary>
    public string? PrivateKeyPath { get; init; }

    /// <summary>
    /// Optional password for a PKCS#12 (PFX) bundle. Only consulted for PFX loading — ignored when
    /// <see cref="PrivateKeyPath"/> is set (PEM). May be stored encrypted with the <c>dp:v1:</c>
    /// Data Protection prefix (unprotected at load time) or as plaintext. Never logged and never
    /// surfaced in validation or load errors.
    /// </summary>
    public string? CertificatePassword { get; init; }

}
