using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// Issue #53: the operator auto-approval allowlist supplies the human consent step and nothing else.
/// It must never change Ward classification, never widen the advertised tool set, and stay a no-op
/// until an operator both enables it and names a tool.
/// </summary>
public sealed class WardAutoApprovalPolicyTests
{

    [Fact]
    public void AutoApproval_is_off_by_default()
    {

        WardSettings wards = ArcanumRuntimeDefaults.Ward;

        Assert.False(wards.AutoApproveEnabled);

        Assert.Empty(wards.AutoApproveTools);

        Assert.False(
            ToolRiskClassifier.IsAutoApproved(
                ToolRiskClassifier.ApplyPatchToolName,
                wards));

    }

    [Fact]
    public void Enabled_with_an_empty_allowlist_is_a_no_op()
    {

        WardSettings wards = ArcanumRuntimeDefaults.Ward with
        {
            AutoApproveEnabled = true,
            AutoApproveTools = [],
        };

        Assert.False(
            ToolRiskClassifier.IsAutoApproved(
                ToolRiskClassifier.ApplyPatchToolName,
                wards));

    }

    [Fact]
    public void A_listed_tool_is_auto_approved_regardless_of_name_casing()
    {

        WardSettings wards = ArcanumRuntimeDefaults.Ward with
        {
            AutoApproveEnabled = true,
            AutoApproveTools = [ToolRiskClassifier.ApplyPatchToolName],
        };

        Assert.True(ToolRiskClassifier.IsAutoApproved("apply_patch", wards));

        Assert.True(ToolRiskClassifier.IsAutoApproved("APPLY_PATCH", wards));

    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("execute_command")]
    public void An_unlisted_or_missing_tool_name_is_never_auto_approved(string? toolName)
    {

        WardSettings wards = ArcanumRuntimeDefaults.Ward with
        {
            AutoApproveEnabled = true,
            AutoApproveTools = [ToolRiskClassifier.ApplyPatchToolName],
        };

        Assert.False(ToolRiskClassifier.IsAutoApproved(toolName, wards));

    }

    [Fact]
    public void Auto_approval_requires_wards_to_be_enabled()
    {

        WardSettings wards = ArcanumRuntimeDefaults.Ward with
        {
            Enabled = false,
            AutoApproveEnabled = true,
            AutoApproveTools = [ToolRiskClassifier.ApplyPatchToolName],
        };

        Assert.False(
            ToolRiskClassifier.IsAutoApproved(
                ToolRiskClassifier.ApplyPatchToolName,
                wards));

    }

    [Fact]
    public void Auto_approval_does_not_change_ward_classification_or_advertisement()
    {

        WardSettings wards = ArcanumRuntimeDefaults.Ward with
        {
            Enabled = true,
            ForbiddenArts = [],
            AutoApproveEnabled = true,
            AutoApproveTools =
            [
                ToolRiskClassifier.ApplyPatchToolName,
                ToolRiskClassifier.ExecuteCommandToolName,
                ToolRiskClassifier.WorkspaceCheckToolName,
            ],
        };

        foreach (string intrinsic in ToolRiskClassifier.IntrinsicWardToolNames)
        {

            Assert.True(
                ToolRiskClassifier.RequiresWard(intrinsic, campaignRequiresWard: false, wards));

        }

        // An auto-approvable name is still a Forbidden Art for advertisement purposes, and a name
        // that was never advertised gains nothing by being listed.
        HashSet<string> forbidden = ToolRiskClassifier.BuildForbiddenToolNames(wards.AutoApproveTools);

        Assert.Contains(ToolRiskClassifier.ApplyPatchToolName, forbidden);

        Assert.False(
            ToolRiskClassifier.RequiresWard("never_registered_tool", campaignRequiresWard: true, wards));

    }

    [Fact]
    public void Resolved_ward_policy_normalizes_blank_padded_and_duplicate_tool_names()
    {

        ArcanumSettings settings = new()
        {
            Security = new SecuritySettings
            {
                Ward = new WardPolicySettings
                {
                    AutoApprove = new WardAutoApprovePolicySettings
                    {
                        Enabled = true,
                        Tools = ["  apply_patch  ", "APPLY_PATCH", "", "   ", "workspace_check"],
                    },
                },
            },
        };

        WardSettings resolved = settings.ResolveWard();

        Assert.True(resolved.AutoApproveEnabled);

        Assert.Equal(
            ["apply_patch", "workspace_check"],
            resolved.AutoApproveTools);

    }

    [Fact]
    public void Resolved_ward_policy_defaults_to_off_when_no_policy_block_exists()
    {

        WardSettings resolved = new ArcanumSettings().ResolveWard();

        Assert.False(resolved.AutoApproveEnabled);

        Assert.Empty(resolved.AutoApproveTools);

    }

}
