namespace RetroDownfall.TheForge.Ux.Markdown;

/// <summary>
/// Pure URI scheme gate for The Illumination link clicks. Only http, https, and mailto may open
/// via the OS launcher; all other schemes are ignored.
/// </summary>
public static class MarkdownLinkPolicy
{

    public static bool ShouldOpen(string? uri)
    {

        if (string.IsNullOrWhiteSpace(uri))
        {

            return false;

        }

        if (!Uri.TryCreate(uri.Trim(), UriKind.Absolute, out Uri? parsed))
        {

            return false;

        }

        return parsed.Scheme is "http" or "https" or "mailto";

    }

}
