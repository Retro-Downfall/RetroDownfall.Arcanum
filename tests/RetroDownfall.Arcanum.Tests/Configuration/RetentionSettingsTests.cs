using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.Configuration;

public sealed class RetentionSettingsTests
{

    [Fact]
    public void RetentionSettings_has_safe_non_destructive_defaults()
    {

        RetentionSettings settings = new();

        Assert.False(settings.AutomaticSweepsEnabled);

        Assert.Equal(24, settings.SweepIntervalHours);

        Assert.Equal(365, settings.AccountingMinimumDays);

        Assert.False(settings.ActiveSessions.Enabled);

        Assert.False(settings.ArchivedSessions.Enabled);

    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(30, 30)]
    [InlineData(365, 365)]
    [InlineData(3_650, 3_650)]
    [InlineData(3_651, 3_650)]
    public void RetentionAccountingMinimumDays_clamps_to_accounting_bounds(
        int value,
        int expected)
    {

        Assert.Equal(
            expected,
            ArcanumSettingClamps.RetentionAccountingMinimumDays(value));

    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(90, 90)]
    [InlineData(3_650, 3_650)]
    [InlineData(3_651, 3_650)]
    public void RetentionRuleDays_clamps_to_policy_bounds(int value, int expected)
    {

        Assert.Equal(expected, ArcanumSettingClamps.RetentionRuleDays(value));

    }

    [Fact]
    public void Configure_binds_retention_settings_via_source_generator()
    {

        const string json =
            """
            {
              "Arcanum": {
                "retention": {
                  "automaticSweepsEnabled": true,
                  "sweepIntervalHours": 12,
                  "accountingMinimumDays": 730,
                  "activeSessions": {
                    "enabled": true,
                    "days": 60
                  },
                  "archivedSessions": {
                    "enabled": true,
                    "days": 30
                  }
                }
              }
            }
            """;

        using MemoryStream stream = new(System.Text.Encoding.UTF8.GetBytes(json));

        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddJsonStream(stream)
            .Build();

        ServiceCollection services = new();

        services.Configure<ArcanumSettings>(configuration.GetSection("Arcanum"));

        using ServiceProvider provider = services.BuildServiceProvider();

        RetentionSettings settings = provider
            .GetRequiredService<IOptions<ArcanumSettings>>()
            .Value
            .Retention;

        Assert.True(settings.AutomaticSweepsEnabled);

        Assert.Equal(12, settings.SweepIntervalHours);

        Assert.Equal(730, settings.AccountingMinimumDays);

        Assert.Equal(new RetentionRuleSettings { Enabled = true, Days = 60 }, settings.ActiveSessions);

        Assert.Equal(new RetentionRuleSettings { Enabled = true, Days = 30 }, settings.ArchivedSessions);

    }

    [Fact]
    public void ArcanumSettings_record_copy_can_replace_retention_without_mutating_source()
    {

        ArcanumSettings source = new();

        ArcanumSettings copy = source with
        {
            Retention = source.Retention with
            {
                AutomaticSweepsEnabled = true,
            },
        };

        Assert.False(source.Retention.AutomaticSweepsEnabled);

        Assert.True(copy.Retention.AutomaticSweepsEnabled);

    }

}
