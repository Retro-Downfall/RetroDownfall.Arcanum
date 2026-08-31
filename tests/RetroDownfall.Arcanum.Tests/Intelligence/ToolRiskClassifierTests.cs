using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Tests.NativeSqlCipher;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class ToolRiskClassifierTests
{

    private const string WardToolInventoryStart = "<!-- ward-tool-inventory:start -->";

    private const string WardToolInventoryEnd = "<!-- ward-tool-inventory:end -->";

    private sealed record WardToolInventoryRow(
        string Group,
        string Name,
        string CatalogStatus,
        string WardDecision,
        string IndependentBoundary);

    private static IReadOnlyList<WardToolInventoryRow> ReadWardToolInventory(string design)
    {

        int sectionStart = design.IndexOf(
            "### 11.14 Wards (record-only server tool calls and retained compatibility engine)",
            StringComparison.Ordinal);

        Assert.True(sectionStart >= 0, "DESIGN §11.14 is missing.");

        int nextSection = design.IndexOf("\n### 11.15 ", sectionStart, StringComparison.Ordinal);

        Assert.True(nextSection > sectionStart, "DESIGN §11.15 must bound the Ward section.");

        int inventoryStart = design.IndexOf(WardToolInventoryStart, StringComparison.Ordinal);

        int inventoryEnd = design.IndexOf(WardToolInventoryEnd, StringComparison.Ordinal);

        Assert.True(inventoryStart > sectionStart && inventoryEnd > inventoryStart && inventoryEnd < nextSection);

        Assert.Equal(inventoryStart, design.LastIndexOf(WardToolInventoryStart, StringComparison.Ordinal));

        Assert.Equal(inventoryEnd, design.LastIndexOf(WardToolInventoryEnd, StringComparison.Ordinal));

        string table = design[(inventoryStart + WardToolInventoryStart.Length)..inventoryEnd];

        string[] lines = table.Split(
            '\n',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        Assert.True(lines.Length >= 2, "The Ward inventory table is empty.");

        Assert.Equal(
            "| Group | Tool | Catalog status | Ward decision | Independent boundary |",
            lines[0]);

        Assert.Equal("|---|---|---|---|---|", lines[1]);

        List<WardToolInventoryRow> rows = [];

        foreach (string line in lines.Skip(2))
        {

            string[] cells = line.Trim('|').Split('|', StringSplitOptions.TrimEntries);

            Assert.Equal(5, cells.Length);

            rows.Add(new WardToolInventoryRow(
                cells[0],
                cells[1].Trim('`'),
                cells[2],
                cells[3],
                cells[4]));

        }

        return rows;

    }

    private static readonly string[] KnownToolNames =
    [
        "apply_patch",
        "workspace_check",
        "write_file",
        "replace_text_block",
        "search_workspace",
        "read_file_chunk",
        "list_directory",
        "execute_command",
        "run_spell_script",
        "read_command_output",
        "delete_lexicon",
        "scribe_lexicon",
        "read_saga",
        "search_archives",
        "retire_covenant",
        "propose_covenant",
        "attach_session_file",
        "refresh_session_file",
        "delegate_task",
        "petition_dungeon_master",
        "ask_human",
        "adjust_initiative",
        "cast_sending",
        "continue_sending",
        "dispatch_sending",
        "send_commlink_alert",
        "web_search",
        "read_url",
        "browse_web",
        "get_local_system_time",
        "get_arcanum_system_info",
    ];

    [Theory]
    [MemberData(nameof(KnownToolAndCampaignSettings))]
    public void RequiresWard_returns_false_for_every_known_tool_under_either_campaign_setting(
        string toolName,
        bool campaignRequiresWard)
    {
        WardSettings wards = ArcanumRuntimeDefaults.Ward with
        {
            ForbiddenArts = [toolName],
        };

        Assert.False(ToolRiskClassifier.RequiresWard(toolName, campaignRequiresWard, wards));
    }

    [Fact]
    public void Intrinsic_ward_inventory_is_empty()
    {
        Assert.Empty(ToolRiskClassifier.IntrinsicWardToolNames);
    }

    [Fact]
    public void Ward_defaults_name_no_forbidden_arts()
    {
        Assert.Empty(ArcanumRuntimeDefaults.Ward.ForbiddenArts);
    }

    [Fact]
    public void Known_tool_inventory_contains_31_distinct_names()
    {

        Assert.Equal(31, KnownToolNames.Length);

        Assert.Equal(31, KnownToolNames.Distinct(StringComparer.Ordinal).Count());

    }

    [Fact]
    public void Design_ward_inventory_matches_every_known_tool_and_names_no_ward_decision()
    {

        string root = NativeSqlCipherTestPaths.RepositoryRoot();

        string design = File.ReadAllText(Path.Combine(root, "docs", "Arcanum.DESIGN.md"));

        IReadOnlyList<WardToolInventoryRow> rows = ReadWardToolInventory(design);

        Assert.Equal(31, rows.Count);

        Assert.Equal(31, rows.Select(static row => row.Name).Distinct(StringComparer.Ordinal).Count());

        Assert.Equal(
            KnownToolNames.OrderBy(static name => name, StringComparer.Ordinal).ToArray(),
            rows.Select(static row => row.Name).OrderBy(static name => name, StringComparer.Ordinal).ToArray());

        Assert.All(rows, static row => Assert.Equal("None", row.WardDecision));

        Assert.All(rows, static row => Assert.False(string.IsNullOrWhiteSpace(row.Group)));

        Assert.All(rows, static row => Assert.False(string.IsNullOrWhiteSpace(row.IndependentBoundary)));

        Assert.All(
            rows,
            static row => Assert.Contains(
                row.CatalogStatus,
                new[]
                {
                    "normally advertised",
                    "conditionally advertised",
                    "recognized compatibility alias",
                }));

        WardToolInventoryRow browseWeb = Assert.Single(
            rows,
            static row => row.Name == ArcanumBuiltInToolNames.BrowseWeb);

        Assert.Equal("recognized compatibility alias", browseWeb.CatalogStatus);

    }

    [Fact]
    public void BuildForbiddenToolNames_preserves_only_operator_configured_names()
    {
        HashSet<string> forbidden = ToolRiskClassifier.BuildForbiddenToolNames(["write_file"]);

        Assert.Equal(["write_file"], forbidden);
    }

    [Fact]
    public void PublishedIntrinsicAndReservedSetsAreNotMutableHashSets()
    {
        Assert.IsNotType<HashSet<string>>(ToolRiskClassifier.IntrinsicWardToolNames);
        Assert.IsNotType<HashSet<string>>(WorkspaceCheckCatalogDefaults.ReservedProfileIds);
    }

    public static IEnumerable<object[]> KnownToolAndCampaignSettings()
    {
        foreach (string toolName in KnownToolNames)
        {
            yield return [toolName, false];
            yield return [toolName, true];
        }
    }

    [Fact]
    public void Workspace_check_ward_disclosure_names_code_execution_and_residual_risks()
    {

        string disclosure = ToolRiskClassifier.GetWardDisclosure(
            ToolRiskClassifier.WorkspaceCheckToolName);

        Assert.Contains(
            "workspace-authored code",
            disclosure,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "source",
            disclosure,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "read-only",
            disclosure,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "writable build",
            disclosure,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "network",
            disclosure,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "exfiltrat",
            disclosure,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "detached descendant",
            disclosure,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "best effort",
            disclosure,
            StringComparison.OrdinalIgnoreCase);

        Assert.Contains(
            "Do not run",
            disclosure,
            StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(
            "approve",
            disclosure,
            StringComparison.OrdinalIgnoreCase);
    }
}
