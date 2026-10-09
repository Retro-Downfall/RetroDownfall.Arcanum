using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Configuration;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

[Collection("ProcessEnvironment")]
public sealed class CampaignRollupRetentionContractTests : IDisposable
{
    private readonly ArcanumTestHomeScope _home = new("arcanum-campaign-rollup-retention");

    [Fact]
    public void Campaign_summary_retention_is_an_appended_independent_disabled_rule()
    {
        Assert.True(DataRetentionDataClassParser.TryParse("campaign-summaries", out RetentionDataClass dataClass));

        Assert.Equal(31, (int)dataClass);

        RetentionSettings settings = new();

        RetentionRuleSettings rule = Assert.IsType<RetentionRuleSettings>(DataRetentionSettingsCatalog.ResolveRule(settings, dataClass));

        Assert.False(rule.Enabled);

        Assert.Equal(365, rule.Days);

        Assert.NotSame(settings.SagaMemories, rule);

        Assert.NotSame(settings.LexiconEntries, rule);

        Assert.Equal(30, (int)RetentionDataClass.MemoryErasureEvidence);
    }

    [Fact]
    public void Campaign_summary_reset_has_an_appended_scope_and_source_generated_wire_contract()
    {
        Assert.Equal("CampaignSummary", Enum.GetName((MemoryResetScope)6));

        Assert.Equal(5, (int)MemoryResetScope.Covenant);

        Guid campaign = Guid.Parse("b43d153c-60bf-4cb9-a3e4-46353abfb9ec");

        MemoryResetRequest request = new((MemoryResetScope)6, CampaignId: campaign);

        string json = JsonSerializer.Serialize(request, ArcanumJsonContext.Default.MemoryResetRequest);

        using JsonDocument document = JsonDocument.Parse(json);

        Assert.Equal("CampaignSummary", document.RootElement.GetProperty("scope").GetString());

        Assert.Equal(campaign, document.RootElement.GetProperty("campaignId").GetGuid());

        Assert.Equal(request, JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.MemoryResetRequest));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(91, 91)]
    [InlineData(9_999, 3_650)]
    public async Task Campaign_summary_policy_updates_persist_clamped_days_and_preserve_other_rules(int requestedDays, int expectedDays)
    {
        ConfigurationWriter writer = new(NullLogger<ConfigurationWriter>.Instance);

        DataRetentionPolicyStore store = new(new TestOptionsMonitor<ArcanumSettings>(new()), writer);

        Result<RetentionSettings> enabled = await store.UpdateRuleAsync(new("campaign-summaries", true, requestedDays));

        Assert.True(enabled.IsSuccess, enabled.Error.Message);

        using JsonDocument persisted = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(ArcanumPaths.GrimoireDirectory, "arcanum.json")));

        JsonElement rule = persisted.RootElement.GetProperty("Arcanum").GetProperty("retention").GetProperty("campaignSummaries");

        Assert.True(rule.GetProperty("enabled").GetBoolean());

        Assert.Equal(expectedDays, rule.GetProperty("days").GetInt32());

        Result<RetentionSettings> disabled = await store.UpdateRuleAsync(new("campaign-summaries", false));

        Assert.True(disabled.IsSuccess, disabled.Error.Message);

        RetentionRuleSettings current = Assert.IsType<RetentionRuleSettings>(DataRetentionSettingsCatalog.ResolveRule(store.Current, (RetentionDataClass)31));

        Assert.False(current.Enabled);

        Assert.Equal(expectedDays, current.Days);

        Assert.False(store.Current.SagaMemories.Enabled);

        Assert.Equal(365, store.Current.SagaMemories.Days);
    }

    public void Dispose() => _home.Dispose();
}
