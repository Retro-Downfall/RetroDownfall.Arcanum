using System.Diagnostics.CodeAnalysis;
using System.Text;
using HtmlAgilityPack;

namespace RetroDownfall.Arcanum.Infrastructure.Intelligence.WebResearch;

/// <summary>
/// Extracts bounded, model-readable Markdown from static HTML without executing active content.
/// </summary>
public sealed class WebPageContentExtractor
{
    private const int MaxTitleBytes = 2_048;

    private static readonly char[] Whitespace = [' ', '\t', '\r', '\n', '\f'];

    public WebPageExtractionResult Extract(
        string html,
        Uri baseUri,
        int maxContentBytes,
        int maxLinks,
        int maxLinkUrlChars)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(baseUri);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxContentBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(maxLinks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLinkUrlChars);

        HtmlDocument document = new()
        {
            OptionFixNestedTags = true,
        };

        document.LoadHtml(html);

        bool hasKnownApplicationMount = document.DocumentNode.SelectSingleNode(
            "//*[@id='root' or @id='app' or @id='__next']") is not null;

        RemoveUnsafeAndNonContentNodes(document);

        string title = NormalizeInlineText(
            HtmlEntity.DeEntitize(
                document.DocumentNode.SelectSingleNode("//title")?.InnerText
                    ?? string.Empty));
        title = WebResearchBounds.TruncateUtf8(
            title,
            MaxTitleBytes,
            out bool titleTruncated);

        HtmlNode contentRoot = document.DocumentNode.SelectSingleNode("//main")
            ?? document.DocumentNode.SelectSingleNode("//article")
            ?? document.DocumentNode.SelectSingleNode("//body")
            ?? document.DocumentNode;

        StringBuilder markdownBuilder = new();
        RenderChildren(contentRoot, markdownBuilder, baseUri);

        string markdown = NormalizeMarkdown(markdownBuilder.ToString());
        markdown = WebResearchBounds.TruncateUtf8(
            markdown,
            maxContentBytes,
            out bool contentTruncated);

        ExtractedWebLink[] links = ExtractLinks(
            contentRoot,
            baseUri,
            maxLinks,
            maxLinkUrlChars,
            out int omittedLinkCount);

        return new WebPageExtractionResult(
            title,
            markdown,
            links,
            hasKnownApplicationMount && string.IsNullOrWhiteSpace(markdown),
            titleTruncated || contentTruncated || omittedLinkCount > 0,
            omittedLinkCount);
    }

    private static void RemoveUnsafeAndNonContentNodes(HtmlDocument document)
    {
        HtmlNodeCollection? nodes = document.DocumentNode.SelectNodes(
            "//script|//style|//noscript|//template|//svg|//canvas|//nav|//header|//footer|//aside|//form|//iframe|//object|//embed|//*[@hidden]|//*[@aria-hidden='true']");

        if (nodes is not null)
        {
            foreach (HtmlNode node in nodes.ToArray())
            {
                node.Remove();
            }
        }

        HtmlNodeCollection? styledNodes = document.DocumentNode.SelectNodes("//*[@style]");

        if (styledNodes is null)
        {
            return;
        }

        foreach (HtmlNode node in styledNodes.ToArray())
        {
            string style = node.GetAttributeValue("style", string.Empty);

            if (style.Contains("display:none", StringComparison.OrdinalIgnoreCase)
                || style.Contains("display: none", StringComparison.OrdinalIgnoreCase)
                || style.Contains("visibility:hidden", StringComparison.OrdinalIgnoreCase)
                || style.Contains("visibility: hidden", StringComparison.OrdinalIgnoreCase))
            {
                node.Remove();
            }
        }
    }

    private static void RenderChildren(HtmlNode node, StringBuilder output, Uri baseUri)
    {
        foreach (HtmlNode child in node.ChildNodes)
        {
            RenderNode(child, output, baseUri);
        }
    }

    private static void RenderNode(HtmlNode node, StringBuilder output, Uri baseUri)
    {
        if (node.NodeType == HtmlNodeType.Comment)
        {
            return;
        }

        if (node.NodeType == HtmlNodeType.Text)
        {
            AppendInlineText(output, HtmlEntity.DeEntitize(node.InnerText));
            return;
        }

        if (node.NodeType != HtmlNodeType.Element)
        {
            RenderChildren(node, output, baseUri);
            return;
        }

        string name = node.Name.ToLowerInvariant();

        if (name.Length == 2
            && name[0] == 'h'
            && name[1] is >= '1' and <= '6')
        {
            EnsureBlankLine(output);
            output.Append('#', name[1] - '0');
            output.Append(' ');
            RenderChildren(node, output, baseUri);
            EnsureBlankLine(output);
            return;
        }

        switch (name)
        {
            case "p":
            case "div":
            case "section":
            case "article":
            case "main":
                EnsureBlankLine(output);
                RenderChildren(node, output, baseUri);
                EnsureBlankLine(output);
                return;

            case "blockquote":
                EnsureBlankLine(output);
                string quote = NormalizeInlineText(HtmlEntity.DeEntitize(node.InnerText));

                if (quote.Length > 0)
                {
                    output.Append("> ");
                    output.Append(quote);
                }

                EnsureBlankLine(output);
                return;

            case "br":
                EnsureLineBreak(output);
                return;

            case "hr":
                EnsureBlankLine(output);
                output.Append("---");
                EnsureBlankLine(output);
                return;

            case "ul":
            case "ol":
                RenderList(node, output, baseUri, ordered: name == "ol");
                return;

            case "li":
                EnsureLineBreak(output);
                output.Append("- ");
                RenderChildren(node, output, baseUri);
                EnsureLineBreak(output);
                return;

            case "pre":
                EnsureBlankLine(output);
                output.AppendLine("```");
                output.Append(HtmlEntity.DeEntitize(node.InnerText).Trim());
                output.AppendLine();
                output.Append("```");
                EnsureBlankLine(output);
                return;

            case "code":
                string code = NormalizeInlineText(HtmlEntity.DeEntitize(node.InnerText));

                if (code.Length > 0)
                {
                    AppendInlineText(
                        output,
                        "`"
                        + code.Replace("`", "\\`", StringComparison.Ordinal)
                        + "`");
                }

                return;

            case "a":
                RenderAnchor(node, output, baseUri);
                return;

            case "strong":
            case "b":
                string strong = NormalizeInlineText(
                    HtmlEntity.DeEntitize(node.InnerText));

                if (strong.Length > 0)
                {
                    AppendInlineText(output, $"**{strong}**");
                }

                return;

            case "em":
            case "i":
                string emphasis = NormalizeInlineText(
                    HtmlEntity.DeEntitize(node.InnerText));

                if (emphasis.Length > 0)
                {
                    AppendInlineText(output, $"*{emphasis}*");
                }

                return;

            case "img":
                string alt = NormalizeInlineText(
                    HtmlEntity.DeEntitize(
                        node.GetAttributeValue("alt", string.Empty)));

                if (alt.Length > 0)
                {
                    AppendInlineText(output, alt);
                }

                return;

            default:
                RenderChildren(node, output, baseUri);
                return;
        }
    }

    private static void RenderList(
        HtmlNode list,
        StringBuilder output,
        Uri baseUri,
        bool ordered)
    {
        EnsureBlankLine(output);
        int ordinal = 1;

        foreach (HtmlNode item in list.ChildNodes.Where(
                     static node => string.Equals(
                         node.Name,
                         "li",
                         StringComparison.OrdinalIgnoreCase)))
        {
            output.Append(ordered ? $"{ordinal}. " : "- ");
            RenderChildren(item, output, baseUri);
            EnsureLineBreak(output);
            ordinal++;
        }

        EnsureBlankLine(output);
    }

    private static void RenderAnchor(
        HtmlNode anchor,
        StringBuilder output,
        Uri baseUri)
    {
        string text = NormalizeInlineText(
            HtmlEntity.DeEntitize(anchor.InnerText));
        string href = anchor.GetAttributeValue("href", string.Empty).Trim();

        if (text.Length == 0)
        {
            return;
        }

        if (TryResolvePublicLink(baseUri, href, out Uri? uri))
        {
            AppendInlineText(
                output,
                "["
                + text.Replace("]", "\\]", StringComparison.Ordinal)
                + "]("
                + uri.AbsoluteUri.Replace(")", "%29", StringComparison.Ordinal)
                + ")");
            return;
        }

        AppendInlineText(output, text);
    }

    private static ExtractedWebLink[] ExtractLinks(
        HtmlNode contentRoot,
        Uri baseUri,
        int maxLinks,
        int maxLinkUrlChars,
        out int omittedLinkCount)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        List<ExtractedWebLink> retained = new(Math.Min(maxLinks, 32));
        int total = 0;

        foreach (HtmlNode anchor in contentRoot.SelectNodes(".//a[@href]")
                     ?? Enumerable.Empty<HtmlNode>())
        {
            string href = anchor.GetAttributeValue("href", string.Empty).Trim();

            if (!TryResolvePublicLink(baseUri, href, out Uri? resolved))
            {
                continue;
            }

            UriBuilder normalizedBuilder = new(resolved)
            {
                Fragment = string.Empty,
            };
            string normalized = normalizedBuilder.Uri.AbsoluteUri;

            if (!seen.Add(normalized))
            {
                continue;
            }

            total++;

            if (retained.Count >= maxLinks || normalized.Length > maxLinkUrlChars)
            {
                continue;
            }

            string text = NormalizeInlineText(
                HtmlEntity.DeEntitize(anchor.InnerText));
            text = WebResearchBounds.TruncateUtf8(
                text,
                WebResearchConstants.MaxLinkTextBytes);

            retained.Add(new ExtractedWebLink(text, normalized));
        }

        omittedLinkCount = total - retained.Count;
        return [.. retained];
    }

    private static bool TryResolvePublicLink(
        Uri baseUri,
        string href,
        [NotNullWhen(true)] out Uri? resolved)
    {
        resolved = null;

        if (string.IsNullOrWhiteSpace(href)
            || href.StartsWith('#')
            || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
            || href.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
            || href.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!Uri.TryCreate(baseUri, href, out Uri? candidate)
            || (candidate.Scheme != Uri.UriSchemeHttp
                && candidate.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        resolved = candidate;
        return true;
    }

    private static void AppendInlineText(
        StringBuilder output,
        string value)
    {
        string normalized = NormalizeInlineText(value);

        if (normalized.Length == 0)
        {
            return;
        }

        if (output.Length > 0
            && output[^1] is not (' ' or '\n')
            && normalized[0] is not ('.' or ',' or ';' or ':' or '!' or '?' or ')'))
        {
            output.Append(' ');
        }

        output.Append(normalized);
    }

    private static string NormalizeInlineText(string value)
    {
        string[] segments = value.Split(
            Whitespace,
            StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', segments);
    }

    private static string NormalizeMarkdown(string markdown)
    {
        string[] sourceLines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        StringBuilder normalized = new(markdown.Length);
        int blankLines = 0;

        foreach (string sourceLine in sourceLines)
        {
            string line = sourceLine.Trim();

            if (line.Length == 0)
            {
                blankLines++;

                if (blankLines <= 1 && normalized.Length > 0)
                {
                    normalized.AppendLine();
                }

                continue;
            }

            blankLines = 0;
            normalized.AppendLine(line);
        }

        return normalized.ToString().Trim();
    }

    private static void EnsureLineBreak(StringBuilder output)
    {
        if (output.Length > 0 && output[^1] != '\n')
        {
            output.AppendLine();
        }
    }

    private static void EnsureBlankLine(StringBuilder output)
    {
        if (output.Length == 0)
        {
            return;
        }

        if (output[^1] != '\n')
        {
            output.AppendLine();
        }

        if (output.Length < 2 || output[^2] != '\n')
        {
            output.AppendLine();
        }
    }
}

public sealed record WebPageExtractionResult(
    string Title,
    string Markdown,
    ExtractedWebLink[] Links,
    bool IsJavaScriptShell,
    bool Truncated,
    int OmittedLinkCount);

public sealed record ExtractedWebLink(
    string Text,
    string Url);
