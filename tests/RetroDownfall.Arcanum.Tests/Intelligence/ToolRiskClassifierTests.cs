using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class ToolRiskClassifierTests
{

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
            Enabled = true,
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
