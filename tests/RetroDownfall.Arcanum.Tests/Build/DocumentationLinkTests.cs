using System.Globalization;
using System.Text.RegularExpressions;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Build;

/// <summary>
/// Every relative link in a governed Markdown document lands on a file that exists and, when it names
/// a fragment, on a heading that exists.
/// </summary>
/// <remarks>
/// <para>The failure this prevents is silent. A link written from the wrong base directory renders as
/// a perfectly ordinary blue link and 404s only when a reader on GitHub clicks it, so a document that
/// is read once and trusted for its cross references can carry a hundred dead ones for a release. The
/// walk covers <c>README.md</c>, <c>AGENTS.md</c> and the top level of <c>docs</c>; the plan and
/// specification archive under <c>docs/superpowers</c> is a historical record of a single run and is
/// deliberately left alone.</para>
/// <para>Fenced code blocks and inline code spans are not links, so they are masked before the scan.
/// A path is checked with its exact casing, because a developer's case-insensitive file system accepts
/// a link that the case-sensitive host serving it does not. A fragment is checked only against
/// Markdown targets, using the heading identifiers GitHub generates, including the numeric suffix it
/// adds to a repeated heading.</para>
/// </remarks>
public sealed class DocumentationLinkTests
{
    /// <summary>
    /// The one link that is allowed to be dead: the guide the owner is renaming from
    /// <c>ArcanumOATH.Human.md</c> to <c>Arcanum.OATH.Human.md</c>. The README already links the new
    /// name. The allowance exists only while the old file is still present and the new one is not, so
    /// the moment the rename lands the link resolves on its own and the allowance becomes inert.
    /// </summary>
    private const string PendingRenameTarget = "docs/Arcanum.OATH.Human.md";

    private const string PendingRenameSource = "docs/ArcanumOATH.Human.md";

    private static readonly Regex FencedCodeDelimiter = new(
        @"^\s{0,3}(```|~~~)",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    private static readonly Regex InlineCodeSpan = new(
        @"(`+)(?:(?!\1).)+?\1",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// An inline link. The destination may sit on the line after the opening parenthesis, which
    /// CommonMark allows, so the whitespace around it can include a line ending.
    /// </summary>
    private static readonly Regex LinkTarget = new(
        @"\]\(\s*(?<target>[^)\s]+)(?:\s+""[^""]*"")?\s*\)",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// A reference-style definition, <c>[label]: destination</c>, at the start of a line. A footnote
    /// definition (<c>[^1]:</c>) carries text, not a destination.
    /// </summary>
    private static readonly Regex ReferenceDefinition = new(
        @"^ {0,3}\[(?!\^)[^\]\n]+\]:[ \t]+<?(?<target>[^\s>]+)>?",
        RegexOptions.CultureInvariant | RegexOptions.Multiline,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// A link or image written as HTML, which GitHub renders from the Markdown source as it is.
    /// </summary>
    private static readonly Regex HtmlAttribute = new(
        @"\b(?:href|src)[ \t]*=[ \t]*(?:""(?<target>[^""]*)""|'(?<target>[^']*)')",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(5));

    private static readonly Regex Heading = new(
        @"^ {0,3}#{1,6}[ \t]+(?<text>.*?)[ \t]*#*[ \t]*$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    private static readonly Regex MarkdownLink = new(
        @"!?\[(?<text>[^\]]*)\]\([^)]*\)",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    private static readonly Regex NonIdentifierCharacter = new(
        @"[^\p{L}\p{M}\p{N}\p{Pc}\- ]",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    [Fact]
    public void Every_relative_link_in_a_governed_document_resolves_to_a_file_and_heading()
    {
        string root = TestRepositoryPaths.RepositoryRoot();

        string[] documents = GovernedDocuments(root);

        Dictionary<string, HashSet<string>> headingCache = new(StringComparer.Ordinal);

        List<string> offenders = [];

        int linksChecked = 0;

        foreach (string document in documents)
        {
            string documentDirectory = Path.GetDirectoryName(document)!;

            foreach ((int line, string target) in RelativeLinks(File.ReadAllText(document)))
            {
                linksChecked++;

                string location = $"{ToRepositoryPath(root, document)}:{line}";

                int hash = target.IndexOf('#', StringComparison.Ordinal);

                string pathPart = Uri.UnescapeDataString(hash < 0 ? target : target[..hash]);

                string? fragment = hash < 0 ? null : Uri.UnescapeDataString(target[(hash + 1)..]);

                string resolved = pathPart.Length == 0
                    ? document
                    : Path.GetFullPath(Path.Combine(documentDirectory, pathPart.Replace('/', Path.DirectorySeparatorChar)));

                if (!ExistsWithExactCase(root, resolved))
                {
                    if (!IsPendingRename(root, resolved))
                    {
                        offenders.Add($"{location}: ({target}) names no file or directory");
                    }

                    continue;
                }

                if (string.IsNullOrEmpty(fragment)
                    || !resolved.EndsWith(".md", StringComparison.Ordinal)
                    || !File.Exists(resolved))
                {
                    continue;
                }

                if (!headingCache.TryGetValue(resolved, out HashSet<string>? identifiers))
                {
                    identifiers = HeadingIdentifiers(File.ReadAllText(resolved));

                    headingCache[resolved] = identifiers;
                }

                if (!identifiers.Contains(fragment))
                {
                    offenders.Add($"{location}: ({target}) names no heading in {ToRepositoryPath(root, resolved)}");
                }
            }
        }

        // A scan that matched nothing would pass vacuously; the governed set carries a few hundred links.
        Assert.True(linksChecked > 100, $"Only {linksChecked} relative links were found, so the scan is not reading the documents.");

        Assert.True(
            offenders.Count == 0,
            $"{offenders.Count} relative link(s) do not resolve:\n{string.Join('\n', offenders)}");
    }

    [Fact]
    public void The_governed_set_is_the_readme_agents_and_the_top_level_of_docs()
    {
        string root = TestRepositoryPaths.RepositoryRoot();

        string[] relative =
        [
            .. GovernedDocuments(root).Select(path => ToRepositoryPath(root, path)),
        ];

        Assert.Contains("README.md", relative);

        Assert.Contains("AGENTS.md", relative);

        Assert.Contains("docs/Arcanum.Engineering.md", relative);

        Assert.Contains("docs/Arcanum.DESIGN.md", relative);

        Assert.DoesNotContain(relative, static path => path.StartsWith("docs/superpowers/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Build, test & verify", "build-test--verify")]
    [InlineData("10.22.6 What is deliberately absent", "10226-what-is-deliberately-absent")]
    [InlineData("`arcanum backup`", "arcanum-backup")]
    [InlineData("The [command reference](docs/X.md) row", "the-command-reference-row")]
    [InlineData("Snake_case and *emphasis*", "snake_case-and-emphasis")]
    [InlineData("8.1 Wire contract: the `ApiResponse<T>` envelope", "81-wire-contract-the-apiresponset-envelope")]
    public void A_heading_is_identified_the_way_GitHub_identifies_it(
        string heading,
        string expected)
    {
        Assert.Equal(expected, HeadingIdentifier(heading));
    }

    [Fact]
    public void A_repeated_heading_gets_the_numeric_suffix_GitHub_adds()
    {
        HashSet<string> identifiers = HeadingIdentifiers("# Notes\n\n## Notes\n\n```\n# Not a heading\n```\n\n## Notes\n");

        Assert.Equal(["notes", "notes-1", "notes-2"], identifiers.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Code_is_not_scanned_for_links_and_a_link_around_code_is()
    {
        const string document = "A [`code`](docs/A.md) link, `[not](docs/B.md)` in a span, and an ![image](img/c.png).\n"
            + "```\n[fenced](docs/D.md)\n```\n"
            + "An external [site](https://example.com/x) and a [mail](mailto:a@b.c).\n";

        Assert.Equal(
            ["docs/A.md", "img/c.png"],
            RelativeLinks(document).Select(static link => link.Target));
    }

    /// <summary>
    /// GitHub renders a reference-style definition, an HTML <c>href</c> or <c>src</c>, and a link whose
    /// destination starts on the next line exactly as it renders an inline link, so a dead target in
    /// any of those is as dead as one written inline.
    /// </summary>
    [Fact]
    public void A_reference_definition_an_HTML_attribute_and_a_split_link_are_links_too()
    {
        const string document = "See [the guide][g] and [again].\n\n"
            + "[g]: docs/A.md\n"
            + "[again]: <docs/B.md#top> \"Title\"\n"
            + "[^1]: a footnote is text, not a destination\n\n"
            + "<img src=\"img/c.png\" alt=\"x\"> and <a href='docs/D.md'>d</a> and <a href=\"https://example.com\">e</a>\n\n"
            + "A [split\nlink](\n  docs/E.md) and an inline `[code](docs/F.md)` span.\n";

        (int Line, string Target)[] links = [.. RelativeLinks(document)];

        Assert.Equal(
            ["docs/A.md", "docs/B.md#top", "img/c.png", "docs/D.md", "docs/E.md"],
            links.Select(static link => link.Target));

        // The line is the line the destination is written on, so the offender message points at it.
        Assert.Equal([3, 4, 7, 7, 11], links.Select(static link => link.Line));
    }

    /// <summary>
    /// A checkout on Windows carries CRLF line endings, and the scan reads documents as written, so a
    /// document means the same thing with either ending. The Windows run itself is not exercised here.
    /// </summary>
    [Fact]
    public void A_document_with_CRLF_line_endings_scans_like_the_same_document_with_LF()
    {
        const string document = "# Title\n\nA [link](docs/A.md#title) and `[code](docs/B.md)`.\n"
            + "```\n[fenced](docs/C.md)\n```\n"
            + "## Title\n\n[ref]: docs/D.md\n";

        string crlf = document.Replace("\n", "\r\n", StringComparison.Ordinal);

        (int Line, string Target)[] expected = [(3, "docs/A.md#title"), (9, "docs/D.md")];

        Assert.Equal(expected, RelativeLinks(document).ToArray());

        Assert.Equal(expected, RelativeLinks(crlf).ToArray());

        Assert.Equal(["title", "title-1"], HeadingIdentifiers(crlf).Order(StringComparer.Ordinal));

        Assert.Equal(HeadingIdentifiers(document), HeadingIdentifiers(crlf));
    }

    /// <summary>
    /// A link is checked with the casing it wrote, whether or not the file system folds case: the
    /// check is what keeps a link from resolving on a developer's machine and 404ing on GitHub.
    /// </summary>
    [Fact]
    public void A_link_resolves_only_with_the_casing_the_file_has()
    {
        string root = Directory.CreateTempSubdirectory("arcanum-link-case-").FullName;

        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Docs"));

            File.WriteAllText(Path.Combine(root, "Docs", "Guide.md"), "# Guide\n");

            Assert.True(ExistsWithExactCase(root, Path.Combine(root, "Docs", "Guide.md")));

            Assert.True(ExistsWithExactCase(root, Path.Combine(root, "Docs")));

            Assert.False(ExistsWithExactCase(root, Path.Combine(root, "Docs", "guide.md")));

            Assert.False(ExistsWithExactCase(root, Path.Combine(root, "docs", "Guide.md")));

            Assert.False(ExistsWithExactCase(root, Path.Combine(root, "Docs", "Missing.md")));
        }
        finally
        {
            TestDirectoryCleanup.TryDelete(root, nameof(DocumentationLinkTests));
        }
    }

    /// <summary>
    /// The README repeats the license inline, and a copy that drifts from the file it points at is
    /// two license texts. It had once lost a word ("OUT OR IN CONNECTION") that the file spells
    /// "OUT OF OR IN CONNECTION".
    /// </summary>
    [Fact]
    public void The_readme_license_text_is_the_license_file_text()
    {
        string root = TestRepositoryPaths.RepositoryRoot();

        string readme = File.ReadAllText(Path.Combine(root, "README.md"));

        string license = File.ReadAllText(Path.Combine(root, "LICENSE"));

        string inline = readme[readme.IndexOf("Copyright (c)", StringComparison.Ordinal)..];

        int nextHeading = inline.IndexOf("\n## ", StringComparison.Ordinal);

        if (nextHeading >= 0)
        {
            inline = inline[..nextHeading];
        }

        string canonical = license[license.IndexOf("Copyright (c)", StringComparison.Ordinal)..];

        Assert.Equal(CollapseWhitespace(canonical), CollapseWhitespace(inline));
    }

    private static string CollapseWhitespace(string text) =>
        Regex.Replace(text, @"\s+", " ", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5)).Trim();

    private static string[] GovernedDocuments(string root) =>
    [
        Path.Combine(root, "README.md"),
        Path.Combine(root, "AGENTS.md"),
        .. Directory
            .EnumerateFiles(Path.Combine(root, "docs"), "*.md", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal),
    ];

    private static string ToRepositoryPath(
        string root,
        string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static bool IsPendingRename(
        string root,
        string resolved) =>
        ToRepositoryPath(root, resolved) == PendingRenameTarget
        && File.Exists(Path.Combine(root, PendingRenameSource.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>
    /// The relative link targets in a document with their 1-based line numbers, in the order they
    /// appear: inline links (including one whose destination starts on the next line), reference-style
    /// definitions, and HTML <c>href</c> and <c>src</c> attributes. Code is skipped, and so are the
    /// absolute schemes a repository check cannot resolve.
    /// </summary>
    private static IEnumerable<(int Line, string Target)> RelativeLinks(string document)
    {
        string masked = MaskCode(document);

        List<(int Index, string Target)> found = [];

        foreach (Regex pattern in (Regex[])[LinkTarget, ReferenceDefinition, HtmlAttribute])
        {
            foreach (Match match in pattern.Matches(masked))
            {
                Group target = match.Groups["target"];

                found.Add((target.Index, target.Value.Trim('<', '>')));
            }
        }

        foreach ((int index, string target) in found.OrderBy(static link => link.Index))
        {
            if (target.Length == 0
                || target.Contains("://", StringComparison.Ordinal)
                || target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                || target.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)
                || target.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                || target.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            yield return (masked.AsSpan(0, index).Count('\n') + 1, target);
        }
    }

    /// <summary>
    /// The document with fenced code blocks emptied and inline code spans replaced by a placeholder, line
    /// for line, so an offset in the result maps to the same line of the original.
    /// </summary>
    private static string MaskCode(string document)
    {
        string[] lines = document.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        bool fenced = false;

        for (int index = 0; index < lines.Length; index++)
        {
            if (FencedCodeDelimiter.IsMatch(lines[index]))
            {
                fenced = !fenced;

                lines[index] = string.Empty;

                continue;
            }

            lines[index] = fenced
                ? string.Empty
                : InlineCodeSpan.Replace(lines[index], "X");
        }

        return string.Join('\n', lines);
    }

    private static HashSet<string> HeadingIdentifiers(string document)
    {
        string[] lines = document.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        HashSet<string> identifiers = new(StringComparer.Ordinal);

        Dictionary<string, int> seen = new(StringComparer.Ordinal);

        bool fenced = false;

        foreach (string line in lines)
        {
            if (FencedCodeDelimiter.IsMatch(line))
            {
                fenced = !fenced;

                continue;
            }

            if (fenced)
            {
                continue;
            }

            Match heading = Heading.Match(line);

            if (!heading.Success)
            {
                continue;
            }

            string identifier = HeadingIdentifier(heading.Groups["text"].Value);

            if (seen.TryGetValue(identifier, out int count))
            {
                seen[identifier] = count + 1;

                identifiers.Add($"{identifier}-{count.ToString(CultureInfo.InvariantCulture)}");
            }
            else
            {
                seen[identifier] = 1;

                identifiers.Add(identifier);
            }
        }

        return identifiers;
    }

    private static string HeadingIdentifier(string heading)
    {
        string text = MarkdownLink.Replace(heading, "${text}");

        text = text
            .Replace("`", string.Empty, StringComparison.Ordinal)
            .Replace("*", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

        return NonIdentifierCharacter.Replace(text, string.Empty).Replace(' ', '-');
    }

    /// <summary>
    /// A file or directory exists at <paramref name="path"/> with the casing the link wrote, checked
    /// segment by segment under the repository root so a case-insensitive file system does not hide a
    /// link that a case-sensitive host would reject.
    /// </summary>
    private static bool ExistsWithExactCase(
        string root,
        string path)
    {
        string relative = Path.GetRelativePath(root, path);

        if (relative.StartsWith("..", StringComparison.Ordinal))
        {
            return File.Exists(path) || Directory.Exists(path);
        }

        string current = root;

        foreach (string segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Directory.Exists(current))
            {
                return false;
            }

            string? match = Directory
                .EnumerateFileSystemEntries(current)
                .Select(static entry => Path.GetFileName(entry))
                .FirstOrDefault(name => string.Equals(name, segment, StringComparison.Ordinal));

            if (match is null)
            {
                return false;
            }

            current = Path.Combine(current, segment);
        }

        return true;
    }
}
