using System.Text;
using System.Text.RegularExpressions;

namespace RetroDownfall.TheForge.Ux.Markdown;

/// <summary>
/// Prepares markdown for The Illumination: applies the preview character cap and replaces raw HTML
/// with a muted omission marker. Images are left for the Markdig AST renderer + image resolver.
/// </summary>
public static partial class MarkdownSafetySanitizer
{

    /// <summary>Maximum characters passed to the renderer (pathological-document guard).</summary>
    public const int MaxPreviewChars = 256 * 1024;

    private static readonly Regex HtmlTagRegex = HtmlTagPattern();

    public static string Sanitize(string? markdown, out bool truncated)
    {

        truncated = false;

        if (string.IsNullOrEmpty(markdown))
        {

            return string.Empty;

        }

        string text = markdown;

        if (text.Length > MaxPreviewChars)
        {

            text = text[..MaxPreviewChars];

            truncated = true;

        }

        text = HtmlTagRegex.Replace(text, "[HTML omitted]");

        if (truncated)
        {

            StringBuilder builder = new(text.Length + 64);

            builder.Append(text);

            builder.AppendLine();

            builder.AppendLine();

            builder.Append("_Preview truncated_");

            return builder.ToString();

        }

        return text;

    }

    [GeneratedRegex(@"</?[A-Za-z][^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTagPattern();

}
