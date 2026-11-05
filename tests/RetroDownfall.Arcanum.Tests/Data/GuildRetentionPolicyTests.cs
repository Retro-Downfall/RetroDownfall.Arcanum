using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

[Collection("ProcessEnvironment")]
public sealed class GuildRetentionPolicyTests : IDisposable
{
    private readonly ArcanumTestHomeScope _home = new("arcanum-guild-retention");

    [Theory]
    [InlineData("guild-content")]
    [InlineData("guild-records")]
    public async Task Enabling_a_rule_persists_its_clamped_age_and_preserves_other_rule(string selector)
    {
        ConfigurationWriter writer = new(NullLogger<ConfigurationWriter>.Instance);

        DataRetentionPolicyStore store = new(new TestOptionsMonitor<ArcanumSettings>(new()), writer);

        var updated = await store.UpdateRuleAsync(new(selector, true, 0));

        Assert.True(updated.IsSuccess, updated.Error.Message);

        var parsed = RetroDownfall.Arcanum.Core.DataLifecycle.DataRetentionSettingsCatalog.ParseConfigurableClass(selector);

        var rule = RetroDownfall.Arcanum.Core.DataLifecycle.DataRetentionSettingsCatalog.ResolveRule(store.Current, parsed.Value);

        Assert.NotNull(rule);

        Assert.True(rule.Enabled);

        Assert.Equal(1, rule.Days);

        Assert.False(store.Current.SagaMemories.Enabled);

        using JsonDocument persisted = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(ArcanumPaths.GrimoireDirectory, "arcanum.json")));

        JsonElement savedRule = persisted.RootElement.GetProperty("Arcanum").GetProperty("retention")
            .GetProperty(selector == "guild-content" ? "guildContent" : "guildRecords");

        Assert.True(savedRule.GetProperty("enabled").GetBoolean());

        Assert.Equal(1, savedRule.GetProperty("days").GetInt32());
    }

    public void Dispose() => _home.Dispose();
}
