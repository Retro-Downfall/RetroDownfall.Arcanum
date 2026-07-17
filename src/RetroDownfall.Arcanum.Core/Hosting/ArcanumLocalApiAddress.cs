using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Environment;

namespace RetroDownfall.Arcanum.Core.Hosting;

/// <summary>
/// Resolves the loopback URL the local CLI (and similar clients) should use to reach a running
/// Arcanum host. When all-interfaces bind is effective, the host is HTTPS-only on
/// <see cref="HttpsSettings.Port"/>; otherwise the plaintext HTTP listener on
/// <see cref="HostSettings.Port"/> is used.
/// </summary>
public static class ArcanumLocalApiAddress
{

    public static string ResolveBaseUrl(HostSettings host)
    {

        ArgumentNullException.ThrowIfNull(host);

        if (ArcanumEnvironment.IsHostAnyEnabled(host.ListenAny))
        {

            int httpsPort = ArcanumSettingClamps.HostHttpsPort(host.Https.Port);

            return $"https://localhost:{httpsPort}/";

        }

        int httpPort = ArcanumSettingClamps.HostPort(host.Port);

        return $"http://localhost:{httpPort}/";

    }

    public static string ResolveHealthProbeUrl(HostSettings host) =>
        $"{ResolveBaseUrl(host).TrimEnd('/')}/api/health";

    public static string? ResolveHttpUrl(HostSettings host, bool listenAny)
    {

        ArgumentNullException.ThrowIfNull(host);

        if (listenAny)
        {

            return null;

        }

        int httpPort = ArcanumSettingClamps.HostPort(host.Port);

        return $"http://localhost:{httpPort}";

    }

    public static string? ResolveHttpsUrl(HostSettings host, bool httpsEnabled)
    {

        ArgumentNullException.ThrowIfNull(host);

        if (!httpsEnabled)
        {

            return null;

        }

        int httpsPort = ArcanumSettingClamps.HostHttpsPort(host.Https.Port);

        return $"https://localhost:{httpsPort}";

    }

}
