using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

using RetroDownfall.Arcanum.Api.Primitives;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Build;

/// <summary>
/// The error code catalog in <c>docs/Arcanum.API.md</c> section 8.23 names every code the status
/// mapper arms, under the status the mapper answers.
/// </summary>
/// <remarks>
/// <para>The catalog says it is kept in sync with the mapper, and a client that switches on a code
/// reads it as the contract. The failure it prevents is the silent kind: a new arm lands with its
/// behavior and its tests, the catalog stays plausible, and a client author finds out that a code
/// exists, and what status carries it, only when it arrives on the wire.</para>
/// <para>The direction is deliberately one way. The catalog also lists codes the mapper does not arm
/// (route-local statuses, codes reported inside a tool result), and each of those is documented where
/// its route is. A code the mapper does arm has no other place to be documented.</para>
/// </remarks>
public sealed class ApiErrorCatalogDocumentationTests
{
    private static readonly Regex NamedErrorCode = new(
        @"ErrorCodes\.(?<group>[A-Za-z]+)\.(?<name>[A-Za-z]+)",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    private static readonly Regex CatalogRow = new(
        @"^\| (?<codes>.+?) \| (?<status>\d{3}|—) \| (?<meaning>.*) \|$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    private static readonly Regex BacktickedToken = new(
        @"`(?<token>[^`]+)`",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// A row's claim that the default-400 resolver leaves its status alone, in any of the phrasings the
    /// catalog has used or could reasonably use: "never downgraded", "not rewritten to 400", "never
    /// remapped", "never turned into a 400".
    /// </summary>
    private static readonly Regex NoDowngradeClaim = new(
        @"\b(?:never|not)\s+(?:be\s+)?(?:downgrad(?:ed|es?)|rewrit(?:ten|es?)|remap(?:ped|s)?)\b"
            + @"|\b(?:never|not)\s+(?:turned|changed|mapped)\s+(?:in)?to\s+(?:a\s+)?(?:\*\*)?400\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// The opposite statement: the resolver does not protect the row's codes and would rewrite them.
    /// </summary>
    private static readonly Regex ResolverDisclaimer = new(
        @"\b(?:does\s+not|doesn't)\s+protect\b|\bwould\s+(?:rewrite|downgrade|remap)\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(5));

    private static readonly Regex SectionCitation = new(
        @"§(?<number>\d+(?:\.\d+)*)",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    [Fact]
    public void Every_code_the_mapper_arms_name_has_a_row_with_the_same_status()
    {
        Dictionary<string, HashSet<int>> documented = DocumentedStatuses();

        List<string> offenders = [];

        foreach ((string code, int status) in MapperArmedCodes())
        {
            if (!documented.TryGetValue(code, out HashSet<int>? statuses))
            {
                offenders.Add($"{code} (mapper answers {status.ToString(CultureInfo.InvariantCulture)}) has no row in section 8.23");

                continue;
            }

            if (!statuses.Contains(status))
            {
                offenders.Add(
                    $"{code}: mapper answers {status.ToString(CultureInfo.InvariantCulture)}, section 8.23 lists {string.Join(", ", statuses.Order())}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"{offenders.Count} code(s) the mapper arms are missing or misfiled in section 8.23:\n{string.Join('\n', offenders)}");
    }

    /// <summary>
    /// <c>browse_web</c> reports its failures inside the tool result, so the status mapper never arms
    /// them and the contract above cannot see them. They still reach the <c>/api/tools/invoke</c> wire,
    /// and a client that switches on one has to find it in the catalog.
    /// </summary>
    [Fact]
    public void Every_code_the_browse_web_tool_can_report_has_a_row_in_section_8_23()
    {
        Dictionary<string, HashSet<int>> documented = DocumentedStatuses();

        string[] declared =
        [
            .. typeof(ErrorCodes.WebBrowsing)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(static field => field is { IsLiteral: true } && field.FieldType == typeof(string))
                .Select(static field => (string)field.GetRawConstantValue()!)
                .Order(StringComparer.Ordinal),
        ];

        Assert.NotEmpty(declared);

        string[] missing = [.. declared.Where(code => !documented.ContainsKey(code))];

        Assert.True(
            missing.Length == 0,
            $"WebBrowsing codes with no row in section 8.23: {string.Join(", ", missing)}");
    }

    [Fact]
    public void The_catalog_reading_finds_the_codes_the_mapper_arms()
    {
        (string Code, int Status)[] armed = [.. MapperArmedCodes()];

        // A reading that found nothing would let the contract above pass vacuously.
        Assert.True(armed.Length > 200, $"Only {armed.Length} armed codes were read from the mapper.");

        Assert.Contains((ErrorCodes.Validation.InvalidBody, 400), armed);

        Assert.Contains((GrimoireMaintenanceUnavailableException.Code, 503), armed);

        Dictionary<string, HashSet<int>> documented = DocumentedStatuses();

        Assert.Contains(400, documented[ErrorCodes.Validation.InvalidBody]);

        // A continuation after a "/" inherits the group of the code before it.
        Assert.Contains(ErrorCodes.Apprentice.PendingQueueFull, documented.Keys);

        Assert.Contains(404, documented[ErrorCodes.Workspace.FileNotFound]);
    }

    /// <summary>
    /// A row that says its codes are not downgraded by <c>ResolveStatusCodeDefaultBadRequest</c> makes a
    /// claim about that function. It protects only an explicit set of codes and rewrites every other
    /// <b>500</b> to <b>400</b>, so the claim is true for a code in the set and false for one outside it.
    /// </summary>
    [Fact]
    public void A_row_that_claims_a_code_is_never_downgraded_names_only_codes_the_resolver_keeps_at_that_status()
    {
        List<string> offenders = [];

        int claims = 0;

        foreach (string line in CatalogSection(ReadApi()).Split('\n'))
        {
            Match row = CatalogRow.Match(line);

            if (!row.Success
                || row.Groups["status"].Value == "—"
                || !NoDowngradeClaim.IsMatch(row.Groups["meaning"].Value))
            {
                continue;
            }

            claims++;

            int status = int.Parse(row.Groups["status"].Value, CultureInfo.InvariantCulture);

            foreach (string code in CodesIn(row.Groups["codes"].Value))
            {
                int actual = ArcanumErrorMapper.ResolveStatusCodeDefaultBadRequest(code);

                if (actual != status)
                {
                    offenders.Add(
                        $"{code}: the row says it is not downgraded and lists {status.ToString(CultureInfo.InvariantCulture)}, but ResolveStatusCodeDefaultBadRequest answers {actual.ToString(CultureInfo.InvariantCulture)}");
                }
            }
        }

        // A reading that found no claim would let the contract above pass vacuously.
        Assert.True(claims >= 2, $"Only {claims} no-downgrade claim(s) were read from section 8.23.");

        Assert.True(
            offenders.Count == 0,
            $"{offenders.Count} no-downgrade claim(s) in section 8.23 are false for the resolver:\n{string.Join('\n', offenders)}");
    }

    /// <summary>
    /// The opposite claim is held to the resolver too: a row that says the resolver would rewrite its
    /// codes names only codes it does rewrite, so the sentence goes stale the day someone adds the code
    /// to the resolver's protected set.
    /// </summary>
    [Fact]
    public void A_row_that_says_the_resolver_would_rewrite_its_codes_names_only_codes_it_rewrites()
    {
        List<string> offenders = [];

        int disclaimers = 0;

        foreach (string line in CatalogSection(ReadApi()).Split('\n'))
        {
            Match row = CatalogRow.Match(line);

            if (!row.Success
                || row.Groups["status"].Value == "—"
                || !ResolverDisclaimer.IsMatch(row.Groups["meaning"].Value))
            {
                continue;
            }

            disclaimers++;

            int status = int.Parse(row.Groups["status"].Value, CultureInfo.InvariantCulture);

            foreach (string code in CodesIn(row.Groups["codes"].Value))
            {
                int actual = ArcanumErrorMapper.ResolveStatusCodeDefaultBadRequest(code);

                if (actual == status)
                {
                    offenders.Add(
                        $"{code}: the row says the resolver would rewrite it from {status.ToString(CultureInfo.InvariantCulture)}, but ResolveStatusCodeDefaultBadRequest keeps it there");
                }
            }
        }

        // A reading that found no disclaimer would let the contract above pass vacuously.
        Assert.True(disclaimers >= 1, "No resolver disclaimer was read from section 8.23.");

        Assert.True(
            offenders.Count == 0,
            $"{offenders.Count} resolver disclaimer(s) in section 8.23 are false:\n{string.Join('\n', offenders)}");
    }

    /// <summary>
    /// A row that mentions the resolver says one thing or the other. A third phrasing the two readings
    /// above do not recognise would slip past both, so a mention that matches neither fails here, and
    /// the sentence is reworded or the reading is widened.
    /// </summary>
    [Fact]
    public void Every_row_that_mentions_the_default_400_resolver_states_a_claim_or_a_disclaimer()
    {
        List<string> unclassified = [];

        int mentions = 0;

        foreach (string line in CatalogSection(ReadApi()).Split('\n'))
        {
            Match row = CatalogRow.Match(line);

            if (!row.Success
                || !row.Groups["meaning"].Value.Contains("DefaultBadRequest", StringComparison.Ordinal))
            {
                continue;
            }

            mentions++;

            string meaning = row.Groups["meaning"].Value;

            if (!NoDowngradeClaim.IsMatch(meaning) && !ResolverDisclaimer.IsMatch(meaning))
            {
                unclassified.Add(row.Groups["codes"].Value);
            }
        }

        Assert.True(mentions >= 3, $"Only {mentions} row(s) in section 8.23 mention the resolver.");

        Assert.True(
            unclassified.Count == 0,
            "A section 8.23 row mentions ResolveStatusCodeDefaultBadRequest without a claim or disclaimer the guards read:\n"
                + string.Join('\n', unclassified));
    }

    [Theory]
    [InlineData("Explicit infra/search failures (never downgraded by DefaultBadRequest)", true)]
    [InlineData("Never downgraded by `ResolveStatusCodeDefaultBadRequest`", true)]
    [InlineData("Stays 500: it is not rewritten to 400 by the resolver", true)]
    [InlineData("The default-400 resolver never remaps it", true)]
    [InlineData("It is never turned into a 400", true)]
    [InlineData("`ResolveStatusCodeDefaultBadRequest` does not protect them and would rewrite any of them to **400**", false)]
    [InlineData("A client error, not a server fault", false)]
    public void The_no_downgrade_reading_recognises_the_phrasings_a_row_can_use(
        string meaning,
        bool isClaim)
    {
        Assert.Equal(isClaim, NoDowngradeClaim.IsMatch(meaning));
    }

    [Theory]
    [InlineData("`ResolveStatusCodeDefaultBadRequest` does not protect them and would rewrite any of them to **400**", true)]
    [InlineData("The resolver would downgrade it", true)]
    [InlineData("Never downgraded by `ResolveStatusCodeDefaultBadRequest`", false)]
    public void The_resolver_disclaimer_reading_recognises_what_a_row_says_about_codes_it_does_not_protect(
        string meaning,
        bool isDisclaimer)
    {
        Assert.Equal(isDisclaimer, ResolverDisclaimer.IsMatch(meaning));
    }

    /// <summary>
    /// The paragraph that opens section 8.23 lists the explicit <b>500</b> codes the default-400 resolver
    /// honours, and the sentence after the table refers back to "the explicit 500 set above", so the
    /// list is the one place a reader learns which codes are not rewritten. It is read against the
    /// resolver's own answers, over every code <c>ErrorCodes</c> declares, so a code added to the protected
    /// set (or dropped from it) fails here until the paragraph says so.
    /// </summary>
    [Fact]
    public void The_paragraph_that_lists_the_explicit_500_set_lists_exactly_the_codes_the_resolver_keeps_at_500()
    {
        string[] kept =
        [
            .. DeclaredErrorCodes()
                .Where(static code => ArcanumErrorMapper.ResolveStatusCodeDefaultBadRequest(code) == 500)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];

        // A reading that found nothing would let the comparison below pass vacuously.
        Assert.Contains(ErrorCodes.Hub.Error, kept);

        Assert.Contains(ErrorCodes.Saga.WriteFailed, kept);

        string paragraph = Assert.Single(
            CatalogSection(ReadApi()).Split('\n'),
            static line => line.Contains("`ResolveStatusCodeDefaultBadRequest` treats unmapped codes as", StringComparison.Ordinal));

        Match list = Regex.Match(
            paragraph,
            @"explicit \*\*500\*\* mappings \((?<codes>[^)]*)\)",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(5));

        Assert.True(list.Success, "The paragraph no longer lists the explicit 500 mappings in a parenthesis after 'explicit **500** mappings'.");

        string[] documented =
        [
            .. BacktickedToken
                .Matches(list.Groups["codes"].Value)
                .Select(static match => match.Groups["token"].Value)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];

        Assert.True(
            kept.SequenceEqual(documented, StringComparer.Ordinal),
            "The explicit 500 set in the paragraph that opens section 8.23 is not the set ResolveStatusCodeDefaultBadRequest keeps at 500.\n"
                + $"Resolver keeps: {string.Join(", ", kept)}\nParagraph lists: {string.Join(", ", documented)}");
    }

    [Fact]
    public void A_section_cited_by_a_catalog_row_exists_and_documents_the_route_the_row_names()
    {
        string api = ReadApi();

        string[] lines = CatalogSection(api).Split('\n');

        string row = Assert.Single(
            lines,
            static line => line.StartsWith("| `Mcp.DiagnosticDisabled`", StringComparison.Ordinal));

        Match citation = SectionCitation.Match(row);

        Assert.True(citation.Success, "The Mcp.DiagnosticDisabled row cites no section.");

        string cited = SectionText(api, citation.Groups["number"].Value);

        Assert.Contains("/api/mcp/tools/invoke", cited, StringComparison.Ordinal);
    }

    /// <summary>Every code <c>ErrorCodes</c> declares, in any of its nested groups.</summary>
    private static IEnumerable<string> DeclaredErrorCodes() =>
        typeof(ErrorCodes)
            .GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .SelectMany(static group => group.GetFields(BindingFlags.Public | BindingFlags.Static))
            .Where(static field => field is { IsLiteral: true } && field.FieldType == typeof(string))
            .Select(static field => (string)field.GetRawConstantValue()!);

    private static IEnumerable<(string Code, int Status)> MapperArmedCodes()
    {
        string source = File
            .ReadAllText(
                Path.Combine(
                    TestRepositoryPaths.RepositoryRoot(),
                    "src",
                    "RetroDownfall.Arcanum.Api",
                    "Primitives",
                    "ArcanumErrorMapper.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        int start = source.IndexOf("ResolveStatusCode(string errorCode) =>", StringComparison.Ordinal);

        int end = source.IndexOf("ResolveStatusCodeDefaultBadRequest", start, StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start, "The mapper's ResolveStatusCode method could not be located.");

        string method = string.Join(
            '\n',
            source[start..end]
                .Split('\n')
                .Where(static line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        HashSet<string> codes = new(StringComparer.Ordinal);

        foreach (Match match in NamedErrorCode.Matches(method))
        {
            string group = match.Groups["group"].Value;

            string name = match.Groups["name"].Value;

            Type? type = typeof(ErrorCodes).GetNestedType(group, BindingFlags.Public | BindingFlags.NonPublic);

            FieldInfo? field = type?.GetField(name, BindingFlags.Public | BindingFlags.Static);

            Assert.True(field is { IsLiteral: true }, $"ErrorCodes.{group}.{name} is not a constant.");

            codes.Add((string)field!.GetRawConstantValue()!);
        }

        // The one arm that names its code through a constant on the exception that carries it.
        Assert.Contains("GrimoireMaintenanceUnavailableException.Code", method, StringComparison.Ordinal);

        codes.Add(GrimoireMaintenanceUnavailableException.Code);

        foreach (string code in codes.Order(StringComparer.Ordinal))
        {
            yield return (code, ArcanumErrorMapper.ResolveStatusCode(code));
        }
    }

    private static Dictionary<string, HashSet<int>> DocumentedStatuses()
    {
        Dictionary<string, HashSet<int>> documented = new(StringComparer.Ordinal);

        foreach (string line in CatalogSection(ReadApi()).Split('\n'))
        {
            Match row = CatalogRow.Match(line);

            if (!row.Success || row.Groups["status"].Value == "—")
            {
                continue;
            }

            int status = int.Parse(row.Groups["status"].Value, CultureInfo.InvariantCulture);

            foreach (string code in CodesIn(row.Groups["codes"].Value))
            {
                if (!documented.TryGetValue(code, out HashSet<int>? statuses))
                {
                    statuses = [];

                    documented[code] = statuses;
                }

                statuses.Add(status);
            }
        }

        return documented;
    }

    /// <summary>
    /// The codes a row's first cell names. A backticked token with a dot is a full code; one without a
    /// dot is a code only when a <c>/</c> separates it from the code before, and then it inherits that
    /// code's group, which is how the catalog abbreviates <c>`Apprentice.Disabled` / `PendingQueueFull`</c>.
    /// </summary>
    private static IEnumerable<string> CodesIn(string cell)
    {
        string group = string.Empty;

        int previousEnd = 0;

        foreach (Match match in BacktickedToken.Matches(cell))
        {
            string token = match.Groups["token"].Value;

            string separator = cell[previousEnd..match.Index];

            previousEnd = match.Index + match.Length;

            int dot = token.IndexOf('.', StringComparison.Ordinal);

            if (dot > 0 && !token.Contains(' ', StringComparison.Ordinal))
            {
                group = token[..dot];

                yield return token;

                continue;
            }

            if (group.Length > 0
                && separator.Contains('/', StringComparison.Ordinal)
                && Regex.IsMatch(token, "^[A-Za-z]+$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5)))
            {
                yield return $"{group}.{token}";
            }
        }
    }

    private static string ReadApi() =>
        File
            .ReadAllText(Path.Combine(TestRepositoryPaths.RepositoryRoot(), "docs", "Arcanum.API.md"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string CatalogSection(string api)
    {
        int start = api.IndexOf("### 8.23 Error code catalog and HTTP status mapping", StringComparison.Ordinal);

        Assert.True(start >= 0, "Section 8.23 is missing from the API reference.");

        int end = api.IndexOf("\n### 8.24 ", start, StringComparison.Ordinal);

        Assert.True(end > start, "Section 8.24 is missing from the API reference.");

        return api[start..end];
    }

    /// <summary>The text of the section numbered <paramref name="number"/>, up to the next heading of its level or higher.</summary>
    private static string SectionText(
        string api,
        string number)
    {
        Match heading = Regex.Match(
            api,
            $@"^(?<hashes>#{{2,4}}) {Regex.Escape(number)}[. ].*$",
            RegexOptions.Multiline | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(5));

        Assert.True(heading.Success, $"No section {number} exists in the API reference.");

        int level = heading.Groups["hashes"].Length;

        Match next = Regex.Match(
            api[(heading.Index + heading.Length)..],
            $@"^#{{2,{level.ToString(CultureInfo.InvariantCulture)}}} ",
            RegexOptions.Multiline | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(5));

        int stop = next.Success
            ? heading.Index + heading.Length + next.Index
            : api.Length;

        return api[heading.Index..stop];
    }
}
