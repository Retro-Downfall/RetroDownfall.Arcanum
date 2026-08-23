using System.Text.Json;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Repositories;

namespace RetroDownfall.Arcanum.Tests.Tower;

public sealed class CampaignSettingsTests
{

    [Fact]
    public void Settings_payload_omitting_the_ward_flag_binds_it_on_rather_than_off()
    {

        // A positional member with no constructor default binds to default(T), so an omitted ward flag
        // would arrive as `false` — the opposite of CreateDefault() — and PUT /api/campaigns/{id}
        // persists request.Settings verbatim. Absent has to mean "warded", not "un-warded".
        CampaignSettings? settings = JsonSerializer.Deserialize(
            """{"defaultModel":"gpt-4o"}""",
            ArcanumJsonContext.Default.CampaignSettings);

        Assert.NotNull(settings);

        Assert.True(settings!.RequireWardForForbiddenArts);

        Assert.Equal("gpt-4o", settings.DefaultModel);

    }

    [Fact]
    public void An_update_request_carrying_a_partial_settings_object_still_binds_the_ward_on()
    {

        // The whole PUT body, exactly as an operator hand-composes it. CampaignEndpoints replaces the
        // stored Settings column with this object wholesale, so whatever binds here is what persists.
        UpdateCampaignRequest? request = JsonSerializer.Deserialize(
            """{"settings":{}}""",
            ArcanumJsonContext.Default.UpdateCampaignRequest);

        Assert.NotNull(request);

        Assert.NotNull(request!.Settings);

        Assert.True(request.Settings!.RequireWardForForbiddenArts);

    }

    [Fact]
    public void A_stored_or_imported_settings_object_binds_the_ward_on()
    {

        // The persisted Settings column and every import bundle go through ArcanumCoreJsonContext, so a
        // row or a bundle that never mentions the flag must not re-derive an un-warded campaign.
        CampaignSettings? settings = JsonSerializer.Deserialize(
            "{}",
            ArcanumCoreJsonContext.Default.CampaignSettings);

        Assert.NotNull(settings);

        Assert.True(settings!.RequireWardForForbiddenArts);

    }

    [Fact]
    public void Settings_payload_carrying_the_ward_flag_binds_it_verbatim()
    {

        CampaignSettings? warded = JsonSerializer.Deserialize(
            """{"defaultModel":"gpt-4o","requireWardForForbiddenArts":true}""",
            ArcanumJsonContext.Default.CampaignSettings);

        Assert.NotNull(warded);

        Assert.True(warded!.RequireWardForForbiddenArts);

        // Opting out stays possible; it just has to be said out loud.
        CampaignSettings? unwarded = JsonSerializer.Deserialize(
            """{"defaultModel":"gpt-4o","requireWardForForbiddenArts":false}""",
            ArcanumJsonContext.Default.CampaignSettings);

        Assert.NotNull(unwarded);

        Assert.False(unwarded!.RequireWardForForbiddenArts);

    }

    [Fact]
    public void A_campaign_row_whose_settings_were_never_written_still_reads_as_warded()
    {

        // Campaign.Settings' own default is the last fail-open seam in Core: whatever it holds is what
        // DeserializeSettings sees, and an un-warded campaign must never be derived from an absence.
        CampaignSettings settings = CampaignRepository.DeserializeSettings(new Campaign().Settings);

        Assert.True(settings.RequireWardForForbiddenArts);

    }

    /// <summary>
    /// A Settings column that says nothing readable still reads as warded.
    /// </summary>
    /// <remarks>
    /// The column is declared <c>TEXT NOT NULL</c> and both construction sites write a serialized
    /// default, so these fallbacks are defence in depth rather than a live hole. But the whole point
    /// of the Ward default is that absence means warded: a fallback that hand-builds the record with
    /// <c>RequireWardForForbiddenArts: false</c> reintroduces exactly the fail-open the constructor
    /// default exists to close, on the one path that runs when the stored value cannot be trusted.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    public void A_campaign_row_whose_settings_column_is_empty_or_unparsable_still_reads_as_warded(string stored)
    {

        CampaignSettings settings = CampaignRepository.DeserializeSettings(stored);

        Assert.True(settings.RequireWardForForbiddenArts);

    }

    [Fact]
    public void Default_settings_round_trip_through_the_persisted_json_shape()
    {

        string json = JsonSerializer.Serialize(
            CampaignSettings.CreateDefault(),
            ArcanumCoreJsonContext.Default.CampaignSettings);

        Assert.Contains("requireWardForForbiddenArts", json, StringComparison.Ordinal);

        CampaignSettings? restored = JsonSerializer.Deserialize(json, ArcanumCoreJsonContext.Default.CampaignSettings);

        Assert.NotNull(restored);

        Assert.True(restored!.RequireWardForForbiddenArts);

    }

}
