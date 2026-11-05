using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.DataLifecycle;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed class GuildRetentionClassTests
{
    [Theory]
    [InlineData("guild-content", 32)]
    [InlineData("guild-records", 33)]
    [InlineData("guild-evidence", 34)]
    public void Guild_selectors_decode_their_persisted_codes(string selector, int code)
    {
        Assert.True(DataRetentionDataClassParser.TryParse(selector, out RetentionDataClass parsed));

        Assert.Equal(code, (int)parsed);

        Assert.Equal(selector, DataRetentionDataClassParser.Format(parsed));
    }

    [Theory]
    [InlineData("guild-content")]
    [InlineData("guild-records")]
    public void Content_and_record_rules_are_configurable_but_default_disabled(string selector)
    {
        var parsed = DataRetentionSettingsCatalog.ParseConfigurableClass(selector);

        Assert.True(parsed.IsSuccess);

        RetentionRuleSettings? rule = DataRetentionSettingsCatalog.ResolveRule(new RetentionSettings(), parsed.Value);

        Assert.NotNull(rule);

        Assert.False(rule.Enabled);
    }

    [Fact]
    public void Evidence_cannot_be_configured_or_selected_by_a_timer()
    {
        Assert.True(DataRetentionDataClassParser.TryParse("guild-evidence", out RetentionDataClass evidence));

        Assert.Null(DataRetentionSettingsCatalog.ResolveRule(new RetentionSettings(), evidence));

        Assert.Empty(DataRetentionSettingsCatalog.GetAffectedClasses(evidence));

        Assert.True(DataRetentionSettingsCatalog.ParseConfigurableClass("guild-evidence").IsFailure);
    }
}
