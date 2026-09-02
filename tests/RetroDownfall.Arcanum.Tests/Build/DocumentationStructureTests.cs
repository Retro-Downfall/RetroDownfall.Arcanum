using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.NativeSqlCipher;

namespace RetroDownfall.Arcanum.Tests.Build;

/// <summary>
/// Structural contracts for the two documents whose section numbers and route tables are quoted as
/// precedence pointers elsewhere. Prose is reviewed by people; structure is not, and a heading filed
/// under the wrong chapter or a table broken mid-body survives every review because the source still
/// reads correctly line by line.
/// </summary>
[Collection("ApiHost")]
public sealed class DocumentationStructureTests
{

    private static readonly Regex NumberedHeading = new(
        @"^(?<hashes>\#{2,6}) (?<number>\d+(?:\.\d+)*)\.? ",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SeparatorRow = new(
        @"^\|[\s:|-]+\|$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Every outline, table of contents, and anchor generator files a section by the chapter it sits
    /// inside, not by the number it prints. A `### 10.23` placed after `## 11.` is filed under API
    /// security, so a precedence rule that points at "10.10 through 10.24" points into chapter 11.
    /// </summary>
    [Fact]
    public void Every_design_subsection_sits_inside_the_chapter_its_number_claims()
    {

        string[] lines = ReadDocumentLines("Arcanum.DESIGN.md");

        string? chapter = null;

        List<string> offenders = [];

        bool inFence = false;

        for (int index = 0; index < lines.Length; index++)
        {

            string line = lines[index];

            if (line.StartsWith("```", StringComparison.Ordinal))
            {

                inFence = !inFence;

                continue;

            }

            if (inFence)
            {

                continue;

            }

            Match match = NumberedHeading.Match(line);

            if (!match.Success)
            {

                continue;

            }

            string first = match.Groups["number"].Value.Split('.')[0];

            if (match.Groups["hashes"].Value.Length == 2)
            {

                chapter = first;

                continue;

            }

            if (first != chapter)
            {

                offenders.Add($"line {index + 1}: {line.Trim()} is nested under chapter {chapter ?? "(none)"}");

            }

        }

        Assert.True(
            offenders.Count == 0,
            "A DESIGN subsection is filed under a chapter its number does not belong to:"
                + global::System.Environment.NewLine
                + string.Join(global::System.Environment.NewLine, offenders));

    }

    /// <summary>
    /// A blank line ends a markdown table and a paragraph swallows the lines that follow it, so a note
    /// dropped between two rows does not merely interrupt the table — every row after it renders as
    /// literal pipe-delimited text. The route tables are the API contract's index; losing a hundred
    /// rows to a stray paragraph is invisible in the source and total in the rendering.
    /// </summary>
    [Fact]
    public void Every_api_reference_table_row_belongs_to_a_table_with_a_header()
    {

        string[] lines = ReadDocumentLines("Arcanum.API.md");

        List<string> offenders = [];

        bool inFence = false;

        int runStart = -1;

        int runLength = 0;

        for (int index = 0; index <= lines.Length; index++)
        {

            string line = index < lines.Length ? lines[index] : string.Empty;

            bool isFence = index < lines.Length && line.TrimStart().StartsWith("```", StringComparison.Ordinal);

            bool isRow = index < lines.Length && !inFence && !isFence && line.StartsWith("|", StringComparison.Ordinal);

            if (isRow)
            {

                if (runStart < 0)
                {

                    runStart = index;

                }

                runLength++;

                continue;

            }

            if (runStart >= 0)
            {

                bool headed = runLength >= 2 && SeparatorRow.IsMatch(lines[runStart + 1].Trim());

                if (!headed)
                {

                    offenders.Add($"line {runStart + 1}: {runLength} row(s) with no header separator — {lines[runStart].Trim()[..Math.Min(80, lines[runStart].Trim().Length)]}");

                }

                runStart = -1;

                runLength = 0;

            }

            if (isFence)
            {

                inFence = !inFence;

            }

        }

        Assert.True(
            offenders.Count == 0,
            "An API reference table row run has no header separator, so it renders as literal text:"
                + global::System.Environment.NewLine
                + string.Join(global::System.Environment.NewLine, offenders));

    }

    /// <summary>
    /// Every Covenant capacity number Compendium quotes an operator, against the constants.
    /// </summary>
    /// <remarks>
    /// Compendium is where an operator learns what they may store, so a number that drifts there is a
    /// promise the product stops keeping without anyone editing the sentence that made it. The pair
    /// bound is asserted as the sum rather than as a literal, because the reason it is 160 is that it
    /// is the three Sections added together, and a change to any Section that left the sum stale would
    /// be exactly the drift worth catching.
    /// </remarks>
    [Fact]
    public void Compendium_quotes_the_covenant_capacity_the_product_enforces()
    {

        string compendium = string.Join('\n', ReadDocumentLines("Compendium.README.md"));

        Assert.Contains("### What the Covenant can hold", compendium, StringComparison.Ordinal);

        Assert.Contains(
            $"| Global Confirmed | {CovenantLimits.MaxGlobalConfirmedEntries} | {CovenantLimits.MaxGlobalConfirmedRenderedBytes:N0} |",
            compendium,
            StringComparison.Ordinal);

        Assert.Contains(
            $"| Campaign Confirmed | {CovenantLimits.MaxCampaignConfirmedEntries} | {CovenantLimits.MaxCampaignConfirmedRenderedBytes:N0} |",
            compendium,
            StringComparison.Ordinal);

        Assert.Contains(
            $"| Campaign Proposed | {CovenantLimits.MaxCampaignProposedEntries} | {CovenantLimits.MaxCampaignProposedRenderedBytes:N0} |",
            compendium,
            StringComparison.Ordinal);

        Assert.Equal(
            CovenantLimits.MaxActiveSnapshotRows,
            CovenantLimits.MaxGlobalConfirmedEntries
            + CovenantLimits.MaxCampaignConfirmedEntries
            + CovenantLimits.MaxCampaignProposedEntries);

        Assert.Contains(
            $"**{CovenantLimits.MaxActiveSnapshotRows} active entries in the pair a single turn would load.**",
            compendium,
            StringComparison.Ordinal);

    }

    /// <summary>
    /// Every endpoint the host registers has a row in the API reference's route table.
    /// </summary>
    /// <remarks>
    /// <para>The reference calls itself the source of truth for the native HTTP API and heads its
    /// first section "Complete API surface", so a live authenticated route with no row is not an
    /// omission a reader can detect — there is nothing to read. It found exactly that: a registered
    /// <c>POST /api/perception/chronosync</c> that took a body, persisted a baseline, and appeared in
    /// no document in the repository.</para>
    /// <para>Nested inside the structural tests so it runs under the same filter, and filed in the
    /// API host collection because the only exact inventory of what is mapped is the endpoint data
    /// source of a host that ran <c>MapArcanumEndpoints</c>. A source scan cannot stand in for it: the
    /// chronosync registration wraps its <c>MapPost(</c> across two lines and escapes a
    /// single-line pattern.</para>
    /// <para>Its reach is what this configuration registers, which is the limit worth stating: a route
    /// behind a feature flag this host leaves off is invisible to it. Exactly one is — the Scalar UI,
    /// whose flag defaults off — and every other family, Conclave and A2A and Saga and Lexicon
    /// included, is mapped here and therefore checked.</para>
    /// </remarks>
    [Collection("ApiHost")]
    public sealed class ApiReferenceRouteTable(ArcanumWebApplicationFactory factory)
    {

        /// <summary>
        /// The one route deliberately outside the route table, with the reason it is not a row.
        /// </summary>
        /// <remarks>
        /// The OpenAPI document is a documentation surface rather than an application endpoint: it
        /// emits no <c>ApiResponse</c> envelope, carries no error code, and the reference describes it
        /// alongside the Scalar UI in its own "not application `ApiResponse`" line rather than as a
        /// method/path row.
        /// </remarks>
        private static readonly string[] NotRouteTableRows =
        [
            "GET /api/openapi/{documentName}.json",
        ];

        [Fact]
        public void Every_registered_endpoint_has_a_row_in_the_api_reference()
        {

            _ = factory.CreateAuthenticatedClient();

            EndpointDataSource endpoints = factory.Services.GetRequiredService<EndpointDataSource>();

            HashSet<string> documented = ReadDocumentedRoutes();

            List<string> undocumented = [];

            foreach (RouteEndpoint endpoint in endpoints.Endpoints.OfType<RouteEndpoint>())
            {

                string path = NormalizeRoutePattern(endpoint.RoutePattern.RawText ?? string.Empty);

                foreach (string method in endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
                {

                    string registered = $"{method} {path}";

                    if (documented.Contains(registered)
                        || NotRouteTableRows.Contains(registered, StringComparer.Ordinal))
                    {

                        continue;

                    }

                    undocumented.Add(registered);

                }

            }

            Assert.True(
                undocumented.Count == 0,
                "A registered endpoint has no row in docs/Arcanum.API.md:"
                    + global::System.Environment.NewLine
                    + string.Join(
                        global::System.Environment.NewLine,
                        undocumented.Order(StringComparer.Ordinal)));

        }

        /// <summary>
        /// Every method/path pair the reference states as a table row, in either of the two row
        /// shapes it uses: the route table's separate method and path columns, and the Covenant
        /// contract table's single backticked method-and-path cell.
        /// </summary>
        private static HashSet<string> ReadDocumentedRoutes()
        {

            HashSet<string> routes = new(StringComparer.Ordinal);

            foreach (string line in ReadDocumentLines("Arcanum.API.md"))
            {

                Match match = DocumentedRouteRow.Match(line);

                if (!match.Success)
                {

                    match = DocumentedRouteCell.Match(line);

                }

                if (!match.Success)
                {

                    continue;

                }

                foreach (string path in ExpandOptionalSegment(match.Groups["path"].Value))
                {

                    routes.Add($"{match.Groups["method"].Value} {NormalizeRoutePattern(path)}");

                }

            }

            return routes;

        }

        /// <summary>
        /// One documented path with an optional trailing segment stands for two registered routes.
        /// </summary>
        /// <remarks>
        /// The reference writes the pair as <c>/api/memory/explain[/{sessionId}]</c> — one row for one
        /// contract, which is how a reader wants to read it, and two <c>MapGet</c> calls in the host.
        /// </remarks>
        private static IEnumerable<string> ExpandOptionalSegment(string path)
        {

            int open = path.IndexOf('[', StringComparison.Ordinal);

            int close = path.IndexOf(']', StringComparison.Ordinal);

            if (open < 0 || close < open)
            {

                yield return path;

                yield break;

            }

            yield return path[..open] + path[(close + 1)..];

            yield return path[..open] + path[(open + 1)..close] + path[(close + 1)..];

        }

        /// <summary>Drops route constraints and catch-all markers, which the reference does not spell.</summary>
        private static string NormalizeRoutePattern(string pattern) =>
            RouteParameterConstraint
                .Replace(RouteCatchAll.Replace(pattern, "{${name}}"), "{${name}}")
                .TrimEnd('/');

    }

    private static readonly Regex DocumentedRouteRow = new(
        @"^\|\s*(?<method>GET|POST|PUT|DELETE|PATCH|HEAD)\s*\|\s*`(?<path>/[^`]*)`\s*\|",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DocumentedRouteCell = new(
        @"^\|\s*`(?<method>GET|POST|PUT|DELETE|PATCH|HEAD) (?<path>/[^`]*)`\s*\|",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RouteParameterConstraint = new(
        @"\{(?<name>[^{}:?=*]+)[:?=][^{}]*\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RouteCatchAll = new(
        @"\{\*{1,2}(?<name>[^{}]+)\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string[] ReadDocumentLines(string fileName) =>
        File
            .ReadAllText(Path.Combine(NativeSqlCipherTestPaths.RepositoryRoot(), "docs", fileName))
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n');

}
