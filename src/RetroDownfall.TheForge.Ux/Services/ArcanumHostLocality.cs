namespace RetroDownfall.TheForge.Ux.Services;

/// <summary>
/// Detects whether The Forge's configured Arcanum base URL targets the local machine
/// (loopback). Local folder pickers are only valid for loopback hosts.
/// </summary>
public static class ArcanumHostLocality
{

    public static bool IsLoopbackBaseUrl(string? baseUrl)
    {

        if (string.IsNullOrWhiteSpace(baseUrl))
        {

            return false;

        }

        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out Uri? uri))
        {

            return false;

        }

        if (uri.IsLoopback)
        {

            return true;

        }

        return string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);

    }

}
