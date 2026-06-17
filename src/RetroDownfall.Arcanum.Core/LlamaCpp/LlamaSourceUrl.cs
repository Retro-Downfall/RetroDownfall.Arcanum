namespace RetroDownfall.Arcanum.Core.LlamaCpp;

/// <summary>
/// Validates remote URLs for GGUF download.
/// </summary>
public static class LlamaSourceUrl
{

    /// <summary>
    /// Returns <c>true</c> when <paramref name="sourceUrl"/> is an absolute <c>http</c> or <c>https</c> URI.
    /// </summary>
    public static bool TryValidate(string? sourceUrl, out string normalizedUrl)
    {

        normalizedUrl = string.Empty;

        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return false;
        }

        string trimmed = sourceUrl.Trim();

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        normalizedUrl = trimmed;

        return true;

    }

}
