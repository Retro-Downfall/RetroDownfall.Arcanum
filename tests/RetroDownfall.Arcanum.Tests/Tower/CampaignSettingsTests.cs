using System.Text.Json;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Repositories;

namespace RetroDownfall.Arcanum.Tests.Tower;

public sealed class CampaignSettingsTests
{

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Legacy_ward_setting_is_accepted_but_not_re_emitted(bool legacyValue)
    {

        string legacyJson =
            $$"""{"defaultModel":"gpt-4o","requireWardForForbiddenArts":{{(legacyValue ? "true" : "false")}}}""";

        CampaignSettings? apiSettings = JsonSerializer.Deserialize(
            legacyJson,
            ArcanumJsonContext.Default.CampaignSettings);

        CampaignSettings? storedSettings = JsonSerializer.Deserialize(
            legacyJson,
            ArcanumCoreJsonContext.Default.CampaignSettings);

        Assert.NotNull(apiSettings);

        Assert.NotNull(storedSettings);

        Assert.Equal("gpt-4o", apiSettings!.DefaultModel);

        Assert.Equal("gpt-4o", storedSettings!.DefaultModel);

        Assert.DoesNotContain(
            "requireWardForForbiddenArts",
            JsonSerializer.Serialize(apiSettings, ArcanumJsonContext.Default.CampaignSettings),
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            "requireWardForForbiddenArts",
            JsonSerializer.Serialize(storedSettings, ArcanumCoreJsonContext.Default.CampaignSettings),
            StringComparison.Ordinal);

    }

    [Fact]
    public void Empty_settings_remain_valid_without_a_ward_control_field()
    {

        CampaignSettings? settings = JsonSerializer.Deserialize(
            "{}",
            ArcanumCoreJsonContext.Default.CampaignSettings);

        Assert.NotNull(settings);

        Assert.DoesNotContain(
            "requireWardForForbiddenArts",
            JsonSerializer.Serialize(settings, ArcanumCoreJsonContext.Default.CampaignSettings),
            StringComparison.Ordinal);

    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    public void Stored_settings_absence_fallback_cannot_restore_a_tool_gate(string stored)
    {

        CampaignSettings settings = CampaignRepository.DeserializeSettings(stored);

        Assert.DoesNotContain(
            "requireWardForForbiddenArts",
            CampaignRepository.SerializeSettings(settings),
            StringComparison.Ordinal);

        WardSettings restrictiveWards = ArcanumRuntimeDefaults.Ward with
        {
            Enabled = true,
            ForbiddenArts = ["write_file"],
        };

        Assert.False(
            ToolRiskClassifier.RequiresWard(
                "write_file",
                campaignRequiresWard: true,
                restrictiveWards));

    }

}
