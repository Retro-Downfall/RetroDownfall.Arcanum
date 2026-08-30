using RetroDownfall.Arcanum.Core.Configuration.Presets;

namespace RetroDownfall.Arcanum.Tests.Configuration;

public sealed class ConfigurationPresetCatalogTests
{

    [Fact]

    public void All_exposes_the_six_current_presets_in_stable_order()
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

        int[] expectedVersions = [2, 2, 2, 2, 2, 1];

        Assert.Equal(
            expectedVersions,
            ConfigurationPresetCatalog.All.Select(static preset => preset.Version));

    }

    [Fact]

    public void Versions_preserve_the_hard_coded_pre_issue_219_version_one_goldens()
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

        ConfigurationPresetDefinition[] versionOne =
        [
            .. ConfigurationPresetCatalog.Versions.Where(static preset => preset.Version == 1),
        ];

        Assert.Equal(expected.Count, versionOne.Length);

        foreach (ConfigurationPresetDefinition preset in versionOne)
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

        ConfigurationPresetDefinition general = versionOne.Single(
            static preset => preset.Id == "general-assistant");

        Assert.Equal(
            "Ordinary tools do not pause for Ward approval; Covenant retirement remains separately authorized, and unsandboxed child processes remain disabled.",
            general.Disclosure.SecurityImplications);

        ConfigurationPresetDefinition automation = versionOne.Single(
            static preset => preset.Id == "automation");

        Assert.Equal(
            "Ordinary calls do not pause for approval; Covenant retirement keeps its independent authorization policy and existing tool permissions still apply.",
            automation.Disclosure.SecurityImplications);

    }

    [Fact]

    public void Current_version_two_presets_subtract_only_the_retired_Ward_paths()
    {

        string[] changedIds =
        [
            "general-assistant",
            "coding-workspace",
            "research",
            "private-offline",
            "automation",
        ];

        string[] retiredPaths =
        [
            "security.ward.enabled",
            "security.ward.autoDenyInUnattendedMode",
        ];

        foreach (string id in changedIds)
        {

            ConfigurationPresetDefinition versionOne =
                ConfigurationPresetCatalog.FindVersion(id, 1)!;

            ConfigurationPresetDefinition versionTwo =
                ConfigurationPresetCatalog.FindVersion(id, 2)!;

            ConfigurationPresetOwnedSetting[] expectedSurvivors =
            [
                .. versionOne.OwnedSettings.Where(setting =>
                    !retiredPaths.Contains(setting.Path, StringComparer.Ordinal)),
            ];

            Assert.Equal(expectedSurvivors.Length, versionTwo.OwnedSettings.Length);

            for (int index = 0; index < expectedSurvivors.Length; index++)
            {

                ConfigurationPresetOwnedSetting expected = expectedSurvivors[index];

                ConfigurationPresetOwnedSetting actual = versionTwo.OwnedSettings[index];

                Assert.Equal(expected.Path, actual.Path);

                Assert.Equal(expected.CanonicalJson, actual.CanonicalJson);

                Assert.Equal(expected.RequiresRestart, actual.RequiresRestart);

                Assert.Equal(expected.IsSafetyBoundary, actual.IsSafetyBoundary);

                Assert.Equal(
                    expected.PrerequisiteIds.ToArray(),
                    actual.PrerequisiteIds.ToArray());

            }

            Assert.DoesNotContain(
                versionTwo.OwnedSettings,
                setting => retiredPaths.Contains(setting.Path, StringComparer.Ordinal));

            Assert.Equal(versionOne.DisplayName, versionTwo.DisplayName);

            Assert.Equal(versionOne.Purpose, versionTwo.Purpose);

            Assert.Equal(versionOne.Disclosure.Enables, versionTwo.Disclosure.Enables);

            Assert.Equal(versionOne.Disclosure.Disables, versionTwo.Disclosure.Disables);

            Assert.Equal(
                versionOne.Disclosure.ProviderRequirements,
                versionTwo.Disclosure.ProviderRequirements);

            Assert.Equal(
                versionOne.Disclosure.ResourceAndCostBehavior,
                versionTwo.Disclosure.ResourceAndCostBehavior);

            if (id is not "general-assistant" and not "automation")
            {

                Assert.Equal(
                    versionOne.Disclosure.SecurityImplications,
                    versionTwo.Disclosure.SecurityImplications);

            }

            Assert.Equal(
                versionOne.Prerequisites.Select(static prerequisite =>
                    $"{prerequisite.Id}|{prerequisite.Description}|{prerequisite.ResolutionCommand}|{prerequisite.Required}"),
                versionTwo.Prerequisites.Select(static prerequisite =>
                    $"{prerequisite.Id}|{prerequisite.Description}|{prerequisite.ResolutionCommand}|{prerequisite.Required}"));

            Assert.Equal(
                versionOne.Recommendations.Select(static recommendation =>
                    $"{recommendation.Description}|{recommendation.Command}|{recommendation.IsAdvancedFeature}"),
                versionTwo.Recommendations.Select(static recommendation =>
                    $"{recommendation.Description}|{recommendation.Command}|{recommendation.IsAdvancedFeature}"));

            Assert.Equal(
                versionOne.ProgressiveDisclosure.EssentialChoice,
                versionTwo.ProgressiveDisclosure.EssentialChoice);

            Assert.Equal(
                versionOne.ProgressiveDisclosure.DeferredFeatures.ToArray(),
                versionTwo.ProgressiveDisclosure.DeferredFeatures.ToArray());

            Assert.Equal(
                versionOne.ProgressiveDisclosure.FirstSuccessRecommendation,
                versionTwo.ProgressiveDisclosure.FirstSuccessRecommendation);

        }

        ConfigurationPresetDefinition automation =
            ConfigurationPresetCatalog.FindVersion("automation", 2)!;

        Assert.Contains(
            automation.OwnedSettings,
            static setting => setting.Path == "security.ward.unattendedMode"
                && setting.CanonicalJson == "true");

    }

    [Fact]

    public void Historical_general_assistant_hash_matches_the_design_audit_golden()
    {

        ConfigurationPresetBaselineValue[] appliedValues =
        [
            new("features.attachments", "true"),
            new("features.saga", "false"),
            new("features.sagaExtraction", "false"),
            new("features.memoryManagement", "false"),
            new("security.ward.enabled", "true"),
            new("security.ward.autoDenyInUnattendedMode", "true"),
            new("security.allowUnsandboxedToolChildren", "false"),
        ];

        Assert.Equal(
            "a6240807df3e3e86bc649e5a790826a374c921de839f71790b03d7688616f522",
            ConfigurationPresetHash.ComputeCanonicalValues(appliedValues));

    }

    [Fact]

    public void FindVersion_selects_only_the_requested_catalog_id_and_version()
    {

        ConfigurationPresetDefinition expected = ConfigurationPresetCatalog.Versions.Single(static preset =>
            preset.Id == "general-assistant" && preset.Version == 2);

        Assert.Same(expected, ConfigurationPresetCatalog.FindVersion("  GENERAL-ASSISTANT  ", 2));

        Assert.NotNull(ConfigurationPresetCatalog.FindVersion("general-assistant", 1));

        Assert.Null(ConfigurationPresetCatalog.FindVersion("General Assistant", 1));

        Assert.Null(ConfigurationPresetCatalog.FindVersion("general-assistant", 3));

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
            "Ward records are informational; Covenant retirement keeps its independent authorization policy and existing tool permissions still apply.",
            automation.Disclosure.SecurityImplications);

    }

}
