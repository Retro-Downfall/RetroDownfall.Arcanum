using RetroDownfall.Arcanum.Core.Configuration.Presets;

namespace RetroDownfall.Arcanum.Tests.Configuration;

public sealed class ConfigurationPresetCatalogTests
{

    [Fact]

    public void All_exposes_the_six_versioned_presets_in_stable_order()
    {

        string[] expectedIds =
        [
            "general-assistant",
            "coding-workspace",
            "research",
            "private-offline",
            "automation",
            "advanced-custom",
        ];

        Assert.Equal(expectedIds, ConfigurationPresetCatalog.All.Select(static preset => preset.Id));

        Assert.All(ConfigurationPresetCatalog.All, static preset => Assert.Equal(1, preset.Version));

    }

    [Fact]

    public void Versions_match_the_exact_version_one_ownership_and_safety_goldens()
    {

        Dictionary<string, string[]> expected = new(StringComparer.Ordinal)
        {

            ["general-assistant@1"] =
            [
                "features.attachments=true|safety=false",
                "features.saga=false|safety=true",
                "features.sagaExtraction=false|safety=true",
                "features.memoryManagement=false|safety=true",
                "security.ward.enabled=true|safety=true",
                "security.ward.autoDenyInUnattendedMode=true|safety=true",
                "security.allowUnsandboxedToolChildren=false|safety=true",
            ],

            ["coding-workspace@1"] =
            [
                "features.workspaceChecks=true|safety=false",
                "workspaces.enableFileWrite=true|safety=false",
                "security.ward.enabled=true|safety=true",
                "security.ward.autoDenyInUnattendedMode=true|safety=true",
                "security.allowUnsandboxedToolChildren=false|safety=true",
            ],

            ["research@1"] =
            [
                "features.webBrowsing=true|safety=false",
                "security.ward.enabled=true|safety=true",
                "security.ward.autoDenyInUnattendedMode=true|safety=true",
                "security.allowUnsandboxedToolChildren=false|safety=true",
            ],

            ["private-offline@1"] =
            [
                "host.listenAny=false|safety=true",
                "features.webBrowsing=false|safety=true",
                "features.enterpriseTelemetry=false|safety=true",
                "security.ward.enabled=true|safety=true",
                "security.ward.autoDenyInUnattendedMode=true|safety=true",
                "security.allowUnsandboxedToolChildren=false|safety=true",
            ],

            ["automation@1"] =
            [
                "security.ward.enabled=true|safety=true",
                "security.ward.autoDenyInUnattendedMode=true|safety=true",
                "security.ward.unattendedMode=true|safety=false",
                "security.allowUnsandboxedToolChildren=false|safety=true",
            ],

            ["advanced-custom@1"] = [],

        };

        Assert.Equal(expected.Count, ConfigurationPresetCatalog.Versions.Length);

        foreach (ConfigurationPresetDefinition preset in ConfigurationPresetCatalog.Versions)
        {

            string key = $"{preset.Id}@{preset.Version}";

            Assert.True(expected.TryGetValue(key, out string[]? expectedSettings), $"Unexpected preset version '{key}'.");

            string[] actualSettings =
            [
                .. preset.OwnedSettings.Select(static setting =>
                    $"{setting.Path}={setting.CanonicalJson}|safety={setting.IsSafetyBoundary.ToString().ToLowerInvariant()}"),
            ];

            Assert.Equal(expectedSettings, actualSettings);

        }

    }

    [Fact]

    public void FindVersion_selects_only_the_requested_catalog_id_and_version()
    {

        ConfigurationPresetDefinition expected = ConfigurationPresetCatalog.Versions.Single(static preset =>
            preset.Id == "general-assistant" && preset.Version == 1);

        Assert.Same(expected, ConfigurationPresetCatalog.FindVersion("  GENERAL-ASSISTANT  ", 1));

        Assert.Null(ConfigurationPresetCatalog.FindVersion("General Assistant", 1));

        Assert.Null(ConfigurationPresetCatalog.FindVersion("general-assistant", 2));

        Assert.Null(ConfigurationPresetCatalog.FindVersion("general-assistant", 0));

        Assert.Null(ConfigurationPresetCatalog.FindVersion(null, 1));

    }

    [Fact]

    public void Coding_workspace_first_success_command_includes_the_required_prompt()
    {

        const string expectedCommand =
            "arcanum run --workspace . \"Inspect this workspace and summarize it.\"";

        ConfigurationPresetDefinition preset = ConfigurationPresetCatalog.Find("coding-workspace")!;

        ConfigurationPresetRecommendation recommendation = Assert.Single(
            preset.Recommendations,
            static recommendation => !recommendation.IsAdvancedFeature);

        Assert.Equal(expectedCommand, recommendation.Command);

        Assert.Equal($"Run {expectedCommand}", preset.ProgressiveDisclosure.FirstSuccessRecommendation);

    }

    [Theory]

    [InlineData("GENERAL-ASSISTANT", "general-assistant")]

    [InlineData("  Coding Workspace  ", "coding-workspace")]

    [InlineData("Private/Offline", "private-offline")]

    public void Find_accepts_an_exact_id_or_display_name_without_case_sensitivity(
        string query,
        string expectedId)
    {

        ConfigurationPresetDefinition? preset = ConfigurationPresetCatalog.Find(query);

        Assert.NotNull(preset);

        Assert.Equal(expectedId, preset.Id);

    }

    [Fact]

    public void Find_does_not_guess_from_an_ambiguous_or_partial_name()
    {

        Assert.Null(ConfigurationPresetCatalog.Find("general"));

        Assert.Null(ConfigurationPresetCatalog.Find("customized"));

    }

    [Fact]

    public void Catalog_never_owns_secrets_endpoints_internal_mechanics_or_unlimited_budget_values()
    {

        string[] prohibitedPathFragments =
        [
            "credential",
            "apiKey",
            "endpoint",
            "password",
            "retry",
            "timeout",
            "loop",
            "maxConcurrentApprentices",
            "dailyLimitUsd",
            "forbiddenArts",
            "allowedHttpHosts",
        ];

        foreach (ConfigurationPresetOwnedSetting setting in
                 ConfigurationPresetCatalog.All.SelectMany(static preset => preset.OwnedSettings))
        {

            Assert.DoesNotContain(
                prohibitedPathFragments,
                fragment => setting.Path.Contains(fragment, StringComparison.OrdinalIgnoreCase));

        }

        Assert.DoesNotContain(
            ConfigurationPresetCatalog.All.SelectMany(static preset => preset.OwnedSettings),
            static setting =>
                setting.Path.Equals("host.listenAny", StringComparison.OrdinalIgnoreCase)
                && setting.CanonicalJson.Equals("true", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(
            ConfigurationPresetCatalog.All.SelectMany(static preset => preset.OwnedSettings),
            static setting =>
                setting.Path.Equals(
                    "security.allowUnsandboxedToolChildren",
                    StringComparison.OrdinalIgnoreCase)
                && setting.CanonicalJson.Equals("true", StringComparison.OrdinalIgnoreCase));

    }

    [Fact]

    public void Automation_requires_an_existing_positive_budget_without_owning_the_budget()
    {

        ConfigurationPresetDefinition automation = ConfigurationPresetCatalog.Find("automation")!;

        Assert.Contains(
            automation.Prerequisites,
            static prerequisite => prerequisite.Id == "positive-budget" && prerequisite.Required);

        Assert.DoesNotContain(
            automation.OwnedSettings,
            static setting => setting.Path.StartsWith("cost.budget", StringComparison.OrdinalIgnoreCase));

    }

    [Fact]

    public void Glossary_explains_product_terms_in_shared_plain_language()
    {

        string[] expectedTerms = ["Ward", "Sanctum", "Weave", "Saga", "Lexicon"];

        Assert.Equal(expectedTerms, ConfigurationPresetCatalog.Glossary.Select(static entry => entry.Term));

        Assert.All(
            ConfigurationPresetCatalog.Glossary,
            static entry => Assert.False(string.IsNullOrWhiteSpace(entry.PlainLanguageMeaning)));

    }

    [Fact]

    public void Ward_disclosures_describe_the_interim_record_only_tool_policy()
    {
        ConfigurationPresetGlossaryEntry ward = Assert.Single(
            ConfigurationPresetCatalog.Glossary,
            static entry => entry.Term == "Ward");

        Assert.Equal(
            "A per-tool audit record; Covenant retirement keeps its separate approval policy.",
            ward.PlainLanguageMeaning);

        ConfigurationPresetDefinition coding = ConfigurationPresetCatalog.Find("coding-workspace")!;

        Assert.Equal(
            "File changes can modify project data; Ward records each call and Sanctum path boundaries remain active.",
            coding.Disclosure.SecurityImplications);

        ConfigurationPresetDefinition automation = ConfigurationPresetCatalog.Find("automation")!;

        Assert.Equal(
            "Enables unattended execution only after an operator supplies a positive explicit budget.",
            automation.Purpose);

        Assert.Equal(
            "Unattended ordinary tool execution with per-call Ward records.",
            automation.Disclosure.Enables);

        Assert.Equal(
            "Ordinary calls do not pause for approval; Covenant retirement keeps its independent authorization policy and existing tool permissions still apply.",
            automation.Disclosure.SecurityImplications);

    }

}
