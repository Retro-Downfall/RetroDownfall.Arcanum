namespace RetroDownfall.Arcanum.Secrets.Security;

/// <summary>
/// The code-pinned public root for externally authorized full-installation remediation.
/// </summary>
/// <remarks>
/// This project has no Core, Grimoire, configuration, or credential-catalog dependency. The public
/// key is therefore independent of every installation-local authority a full reset can erase.
/// </remarks>
internal sealed class FullInstallationResetRemediationTrustRootProvider
{

    internal const string Issuer = "RetroDownfall.Remediation.v1";

    private const string SubjectPublicKeyInfoBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEvzdpwXP5rYoT0+LikSunLMDUg9HApiwOwKlYDGc7ftFHTX8B471lTde7xTTyanS4iNaXHwYMApiOcXbxGOrGhg==";

    internal bool TryResolve(string issuer, out byte[] subjectPublicKeyInfo)
    {

        if (!string.Equals(issuer, Issuer, StringComparison.Ordinal))
        {

            subjectPublicKeyInfo = [];

            return false;

        }

        subjectPublicKeyInfo = Convert.FromBase64String(SubjectPublicKeyInfoBase64);

        return true;

    }

}
