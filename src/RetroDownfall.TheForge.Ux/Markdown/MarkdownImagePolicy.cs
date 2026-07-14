namespace RetroDownfall.TheForge.Ux.Markdown;

/// <summary>
/// Image policy helpers for The Illumination. Remote loads honor the opt-in toggle; relative/local
/// workspace images remain placeholders until a binary workspace API exists.
/// </summary>
public static class MarkdownImagePolicy
{

    public static bool ShouldLoadRemote(bool loadRemoteImagesEnabled) => loadRemoteImagesEnabled;

    public static bool ShouldLoadRelativeOrLocal() => false;

    public static string FormatPlaceholder(string? altText, string? url)
    {

        string alt = string.IsNullOrWhiteSpace(altText) ? "image" : altText.Trim();

        if (string.IsNullOrWhiteSpace(url))
        {

            return $"[Image: {alt}]";

        }

        return $"[Image: {alt} — {url.Trim()}]";

    }

}
