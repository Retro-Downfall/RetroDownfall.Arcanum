using System.Text.RegularExpressions;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Tests.NativeSqlCipher;

namespace RetroDownfall.Arcanum.Tests.Build;

/// <summary>
/// Structural contracts for the two documents whose section numbers and route tables are quoted as
/// precedence pointers elsewhere. Prose is reviewed by people; structure is not, and a heading filed
/// under the wrong chapter or a table broken mid-body survives every review because the source still
/// reads correctly line by line.
/// </summary>
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

    private static string[] ReadDocumentLines(string fileName) =>
        File
            .ReadAllText(Path.Combine(NativeSqlCipherTestPaths.RepositoryRoot(), "docs", fileName))
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n');

}
