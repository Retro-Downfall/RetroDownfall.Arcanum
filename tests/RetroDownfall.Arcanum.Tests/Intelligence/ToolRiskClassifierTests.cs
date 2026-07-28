using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class ToolRiskClassifierTests
{
    [Theory]
    [InlineData(ToolRiskClassifier.ExecuteCommandToolName)]
    [InlineData(ToolRiskClassifier.ApplyPatchToolName)]
    [InlineData(ToolRiskClassifier.WorkspaceCheckToolName)]
    [InlineData("EXECUTE_COMMAND")]
    [InlineData("Apply_Patch")]
    [InlineData("Workspace_Check")]
    public void RequiresWard_IntrinsicTool_IgnoresCustomizedForbiddenArtsAndCampaignPolicy(string toolName)
    {
        WardSettings wards = new()
        {
            Enabled = true,
            ForbiddenArts = [],
        };

        Assert.True(ToolRiskClassifier.RequiresWard(toolName, campaignRequiresWard: false, wards));
    }

    [Fact]
    public void RequiresWard_WardsDisabled_DisablesIntrinsicClassification()
    {
        WardSettings wards = new()
        {
            Enabled = false,
            ForbiddenArts = [],
        };

        Assert.False(
            ToolRiskClassifier.RequiresWard(
                ToolRiskClassifier.ApplyPatchToolName,
                campaignRequiresWard: true,
                wards));
    }

    [Fact]
    public void RequiresWard_ConfiguredNonIntrinsicTool_StillRequiresCampaignPolicy()
    {
        WardSettings wards = new()
        {
            Enabled = true,
            ForbiddenArts = ["write_file"],
        };

        Assert.False(ToolRiskClassifier.RequiresWard("write_file", campaignRequiresWard: false, wards));
        Assert.True(ToolRiskClassifier.RequiresWard("write_file", campaignRequiresWard: true, wards));
        Assert.False(ToolRiskClassifier.RequiresWard("read_file", campaignRequiresWard: true, wards));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(ToolRiskClassifier.SearchWorkspaceToolName)]
    [InlineData("read_file")]
    public void IsIntrinsicWardTool_RejectsMissingAndNonIntrinsicNames(string? toolName)
    {
        Assert.False(ToolRiskClassifier.IsIntrinsicWardTool(toolName));
    }

    [Fact]
    public void BuildForbiddenToolNames_NullConfiguration_StillIncludesIntrinsicTools()
    {
        HashSet<string> forbidden = ToolRiskClassifier.BuildForbiddenToolNames(null);

        Assert.Equal(
            ToolRiskClassifier.IntrinsicWardToolNames.Count,
            forbidden.Count);
        Assert.All(
            ToolRiskClassifier.IntrinsicWardToolNames,
            name => Assert.Contains(name, forbidden));
    }

    [Fact]
    public void IntrinsicSets_KeepSearchReadOnlyAndNoForbiddenArtsCanUnionIntrinsicTools()
    {
        HashSet<string> forbidden = ToolRiskClassifier.BuildForbiddenToolNames(["write_file"]);

        Assert.Contains(ToolRiskClassifier.ExecuteCommandToolName, forbidden);
        Assert.Contains(ToolRiskClassifier.ApplyPatchToolName, forbidden);
        Assert.Contains(ToolRiskClassifier.WorkspaceCheckToolName, forbidden);
        Assert.Contains("write_file", forbidden);
        Assert.DoesNotContain(ToolRiskClassifier.SearchWorkspaceToolName, forbidden);
        Assert.True(ToolRiskClassifier.IsReadOnlyCodingTool(ToolRiskClassifier.SearchWorkspaceToolName));
    }

    [Fact]
    public void WardDefaults_ContinueToAdvertiseIntrinsicRiskNames()
    {
        WardSettings defaults = new();

        Assert.Contains(ToolRiskClassifier.ExecuteCommandToolName, defaults.ForbiddenArts);
        Assert.Contains(ToolRiskClassifier.ApplyPatchToolName, defaults.ForbiddenArts);
        Assert.Contains(ToolRiskClassifier.WorkspaceCheckToolName, defaults.ForbiddenArts);
    }

    [Fact]
    public void PublishedIntrinsicAndReservedSetsAreNotMutableHashSets()
    {
        Assert.IsNotType<HashSet<string>>(ToolRiskClassifier.IntrinsicWardToolNames);
        Assert.IsNotType<HashSet<string>>(WorkspaceCheckCatalogDefaults.ReservedProfileIds);
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
    }
}
