using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// The retained auto-approval policy serves the separate Covenant retirement path only. It must
/// remain off by default and must not restore ordinary Ward classification or widen advertisement.
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
                CovenantToolNames.RetireCovenant,
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
                CovenantToolNames.RetireCovenant,
                wards));

    }

    [Fact]
    public void A_listed_tool_is_auto_approved_regardless_of_name_casing()
    {

        WardSettings wards = ArcanumRuntimeDefaults.Ward with
        {
            AutoApproveEnabled = true,
            AutoApproveTools = [CovenantToolNames.RetireCovenant],
        };

        Assert.True(ToolRiskClassifier.IsAutoApproved("retire_covenant", wards));

        Assert.True(ToolRiskClassifier.IsAutoApproved("RETIRE_COVENANT", wards));

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
            AutoApproveTools = [CovenantToolNames.RetireCovenant],
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
            AutoApproveTools = [CovenantToolNames.RetireCovenant],
        };

        Assert.False(
            ToolRiskClassifier.IsAutoApproved(
                CovenantToolNames.RetireCovenant,
                wards));

    }

    [Fact]
    public void Retained_auto_approval_does_not_restore_ordinary_gating_or_widen_advertisement()
    {

        WardSettings wards = ArcanumRuntimeDefaults.Ward with
        {
            Enabled = true,
            ForbiddenArts = ["write_file"],
            AutoApproveEnabled = true,
            AutoApproveTools = [CovenantToolNames.RetireCovenant],
        };

        Assert.False(ToolRiskClassifier.RequiresWard("write_file", campaignRequiresWard: false, wards));

        Assert.False(ToolRiskClassifier.RequiresWard("write_file", campaignRequiresWard: true, wards));

        HashSet<string> forbidden = ToolRiskClassifier.BuildForbiddenToolNames(wards.ForbiddenArts);

        Assert.Equal(["write_file"], forbidden);

        Assert.DoesNotContain(CovenantToolNames.RetireCovenant, forbidden);

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
