using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.Configuration;

public sealed class CoreConfigurationEnvironmentOverridesTests
{

    [Fact]

    public void Resolve_returns_structured_provenance_without_exposing_raw_values()
    {

        ArcanumSettings persisted = new();

        ConfigurationEnvironmentSnapshot snapshot = ConfigurationEnvironmentResolver.Resolve(
            persisted,
            new Dictionary<string, string?>
            {

                ["ARCANUM_Arcanum__Features__WebBrowsing"] = "true",

            });

        Assert.True(snapshot.EffectiveSettings.Features.WebBrowsing);

        ConfigurationEnvironmentOverride item = Assert.Single(snapshot.Overrides);

        Assert.Equal("features.webBrowsing", item.Path);

        Assert.Equal("ARCANUM_Arcanum__Features__WebBrowsing", item.VariableName);

        Assert.True(item.IsPresent);

        Assert.True(item.IsEffective);

        Assert.Null(item.Error);

        Assert.DoesNotContain(
            item.GetType().GetProperties(),
            static property => property.Name.Contains("value", StringComparison.OrdinalIgnoreCase));

    }

    [Fact]

    public void Resolve_records_invalid_and_unknown_variables_without_mutating_persisted_settings()
    {

        ArcanumSettings persisted = new();

        ConfigurationEnvironmentSnapshot snapshot = ConfigurationEnvironmentResolver.Resolve(
            persisted,
            new Dictionary<string, string?>
            {

                ["ARCANUM_Arcanum__Host__Port"] = "not-a-port",

                ["ARCANUM_Arcanum__Removed__Value"] = "secret-value",

            });

        Assert.Equal(5001, persisted.Host.Port);

        Assert.Equal(5001, snapshot.EffectiveSettings.Host.Port);

        Assert.All(snapshot.Overrides, static item => Assert.False(item.IsEffective));

        Assert.All(snapshot.Overrides, static item => Assert.False(string.IsNullOrWhiteSpace(item.Error)));

        Assert.Equal(
            "ARCANUM_Arcanum__Host__Port",
            snapshot.Find("host.port")!.VariableName);

        Assert.Null(snapshot.FindEffective("host.port"));

        Assert.DoesNotContain(
            snapshot.Overrides,
            static item => item.ToString().Contains("secret-value", StringComparison.Ordinal));

    }

    [Fact]

    public void Special_override_wins_over_general_override_for_the_same_path()
    {

        ConfigurationEnvironmentSnapshot snapshot = ConfigurationEnvironmentResolver.Resolve(
            new ArcanumSettings(),
            new Dictionary<string, string?>
            {

                ["ARCANUM_Arcanum__Host__ListenAny"] = "false",

                ["ARCANUM_HOST_ANY"] = "true",

            });

        Assert.True(snapshot.EffectiveSettings.Host.ListenAny);

        ConfigurationEnvironmentOverride effective = Assert.Single(
            snapshot.Overrides,
            static item => item.VariableName == "ARCANUM_HOST_ANY");

        Assert.True(effective.IsEffective);

        Assert.Equal(
            "ARCANUM_HOST_ANY",
            snapshot.Find("host.listenAny")!.VariableName);

    }

    [Fact]

    public void Edition_special_override_accepts_dev_alias_and_reports_invalid_values()
    {

        ConfigurationEnvironmentSnapshot development = ConfigurationEnvironmentResolver.Resolve(
            new ArcanumSettings(),
            new Dictionary<string, string?>
            {

                ["ARCANUM_EDITION"] = "dev",

            });

        Assert.Equal(ArcanumEdition.Development, development.EffectiveSettings.Edition);

        Assert.True(Assert.Single(development.Overrides).IsEffective);

        ConfigurationEnvironmentSnapshot invalid = ConfigurationEnvironmentResolver.Resolve(
            new ArcanumSettings(),
            new Dictionary<string, string?>
            {

                ["ARCANUM_EDITION"] = "unsupported",

            });

        ConfigurationEnvironmentOverride item = Assert.Single(invalid.Overrides);

        Assert.Equal(ArcanumEdition.Local, invalid.EffectiveSettings.Edition);

        Assert.False(item.IsEffective);

        Assert.False(string.IsNullOrWhiteSpace(item.Error));

    }

    [Fact]

    public void Canonical_path_access_round_trips_scalars_strings_and_arrays()
    {

        ArcanumSettings settings = new();

        ConfigurationPathUpdate boolean = ConfigurationPathAccessor.SetCanonicalValue(
            settings,
            "features.webBrowsing",
            "true");

        Assert.True(boolean.IsSuccess, boolean.Error);

        ConfigurationPathUpdate text = ConfigurationPathAccessor.SetCanonicalValue(
            boolean.Settings!,
            "workspaces.defaultRoot",
            "\"/tmp/project\"");

        Assert.True(text.IsSuccess, text.Error);

        ConfigurationPathUpdate array = ConfigurationPathAccessor.SetCanonicalValue(
            text.Settings!,
            "security.allowedUploadMimeTypes",
            "[\"text/plain\",\"application/json\"]");

        Assert.True(array.IsSuccess, array.Error);

        Assert.Equal(
            "true",
            ConfigurationPathAccessor.GetCanonicalValue(array.Settings!, "features.webBrowsing"));

        Assert.Equal(
            "\"/tmp/project\"",
            ConfigurationPathAccessor.GetCanonicalValue(array.Settings!, "workspaces.defaultRoot"));

        Assert.Equal(
            "[\"text/plain\",\"application/json\"]",
            ConfigurationPathAccessor.GetCanonicalValue(
                array.Settings!,
                "security.allowedUploadMimeTypes"));

    }

}
