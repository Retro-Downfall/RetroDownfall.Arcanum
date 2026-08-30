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

        ExpectedPreset[] expected = VersionOneGoldens();

        ConfigurationPresetDefinition[] versionOne =
        [
            .. ConfigurationPresetCatalog.Versions.Where(static preset => preset.Version == 1),
        ];

        Assert.Equal(expected.Length, versionOne.Length);

        foreach (ConfigurationPresetDefinition preset in versionOne)
        {

            ExpectedPreset? expectedPreset = expected.SingleOrDefault(candidate =>
                candidate.Id == preset.Id && candidate.Version == preset.Version);

            Assert.NotNull(expectedPreset);

            AssertPresetMatches(expectedPreset, preset);

        }

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

            ExpectedPreset expectedVersionOne = VersionOneGoldens().Single(expected =>
                expected.Id == id);

            ConfigurationPresetDefinition versionTwo =
                ConfigurationPresetCatalog.FindVersion(id, 2)!;

            ExpectedOwnedSetting[] expectedSurvivors =
            [
                .. expectedVersionOne.OwnedSettings.Where(setting =>
                    !retiredPaths.Contains(setting.Path, StringComparer.Ordinal)),
            ];

            Assert.Equal(expectedSurvivors.Length, versionTwo.OwnedSettings.Length);

            for (int index = 0; index < expectedSurvivors.Length; index++)
            {

                ExpectedOwnedSetting expected = expectedSurvivors[index];

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

            Assert.Equal(expectedVersionOne.DisplayName, versionTwo.DisplayName);

            Assert.Equal(expectedVersionOne.Purpose, versionTwo.Purpose);

            Assert.Equal(expectedVersionOne.Disclosure.Enables, versionTwo.Disclosure.Enables);

            Assert.Equal(expectedVersionOne.Disclosure.Disables, versionTwo.Disclosure.Disables);

            Assert.Equal(
                expectedVersionOne.Disclosure.ProviderRequirements,
                versionTwo.Disclosure.ProviderRequirements);

            Assert.Equal(
                expectedVersionOne.Disclosure.ResourceAndCostBehavior,
                versionTwo.Disclosure.ResourceAndCostBehavior);

            if (id is not "general-assistant" and not "automation")
            {

                Assert.Equal(
                    expectedVersionOne.Disclosure.SecurityImplications,
                    versionTwo.Disclosure.SecurityImplications);

            }

            AssertPrerequisitesMatch(expectedVersionOne.Prerequisites, versionTwo.Prerequisites);

            AssertRecommendationsMatch(expectedVersionOne.Recommendations, versionTwo.Recommendations);

            Assert.Equal(
                expectedVersionOne.ProgressiveDisclosure.EssentialChoice,
                versionTwo.ProgressiveDisclosure.EssentialChoice);

            Assert.Equal(
                expectedVersionOne.ProgressiveDisclosure.DeferredFeatures,
                versionTwo.ProgressiveDisclosure.DeferredFeatures.ToArray());

            Assert.Equal(
                expectedVersionOne.ProgressiveDisclosure.FirstSuccessRecommendation,
                versionTwo.ProgressiveDisclosure.FirstSuccessRecommendation);

        }

        ConfigurationPresetDefinition automation =
            ConfigurationPresetCatalog.FindVersion("automation", 2)!;

        Assert.Contains(
            automation.OwnedSettings,
            static setting => setting.Path == "security.ward.unattendedMode"
                && setting.CanonicalJson == "true");

        Assert.Equal(
            "Ward records are informational; Covenant retirement remains separately authorized, and unsandboxed child processes remain disabled.",
            ConfigurationPresetCatalog.FindVersion("general-assistant", 2)!
                .Disclosure.SecurityImplications);

        Assert.Equal(
            "Ward records are informational; Covenant retirement keeps its independent authorization policy and existing tool permissions still apply.",
            automation.Disclosure.SecurityImplications);

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

    private static ExpectedPreset[] VersionOneGoldens() =>
    [
        new(
            "general-assistant",
            1,
            "General Assistant",
            "A balanced conversational setup with attachments and conservative memory defaults.",
            [
                new("features.attachments", "true", true, [], false),
                new("features.saga", "false", true, [], true),
                new("features.sagaExtraction", "false", true, [], true),
                new("features.memoryManagement", "false", true, [], true),
                new("security.ward.enabled", "true", true, [], true),
                new("security.ward.autoDenyInUnattendedMode", "true", true, [], true),
                new("security.allowUnsandboxedToolChildren", "false", true, [], true),
            ],
            new ExpectedDisclosure(
                "Attachments and ordinary tool use with per-call Ward records.",
                "Automatic long-term-memory extraction and destructive memory management.",
                "Ordinary tools do not pause for Ward approval; Covenant retirement remains separately authorized, and unsandboxed child processes remain disabled.",
                "A configured inference provider and model are required for first success.",
                "No budget or concurrency limit is changed."),
            [
                new(
                    "provider-model",
                    "A configured provider must advertise the selected model.",
                    "arcanum config open",
                    true),
            ],
            [
                new(
                    "Verify the selected model can answer a simple request.",
                    "arcanum run \"Hello\"",
                    false),
            ],
            new ExpectedProgressiveDisclosure(
                "Choose the provider and model you intend to use.",
                ["Saga, semantic retrieval, MCP servers, and automation remain optional."],
                "Run arcanum run \"Hello\".")),
        new(
            "coding-workspace",
            1,
            "Coding Workspace",
            "Enables workspace checks and file editing under the configured default workspace root.",
            [
                new("features.workspaceChecks", "true", true, [], false),
                new("workspaces.enableFileWrite", "true", true, [], false),
                new("security.ward.enabled", "true", true, [], true),
                new("security.ward.autoDenyInUnattendedMode", "true", true, [], true),
                new("security.allowUnsandboxedToolChildren", "false", true, [], true),
            ],
            new ExpectedDisclosure(
                "Workspace validation and workspace-scoped file writes.",
                "Nothing outside the owned values; indexing remains an explicit later choice.",
                "File changes can modify project data; Ward records each call and Sanctum path boundaries remain active.",
                "A configured inference provider/model and a workspace are required.",
                "No research, apprentice, retry, timeout, or indexing limit is changed."),
            [
                new(
                    "provider-model",
                    "A configured provider must advertise the selected model.",
                    "arcanum config open",
                    true),
                new(
                    "workspace",
                    "A default workspace root must be configured.",
                    "arcanum config set workspaces.defaultRoot .",
                    true),
            ],
            [
                new(
                    "Run a first workspace-scoped coding request.",
                    "arcanum run --workspace . \"Inspect this workspace and summarize it.\"",
                    false),
                new(
                    "Add semantic code retrieval only when the workspace benefits from it.",
                    "arcanum workspace index .",
                    true),
            ],
            new ExpectedProgressiveDisclosure(
                "Configure the default workspace root that Arcanum may edit.",
                ["Codebase indexing, apprentices, and custom workspace checks remain optional."],
                "Run arcanum run --workspace . \"Inspect this workspace and summarize it.\"")),
        new(
            "research",
            1,
            "Research",
            "Enables native web research while preserving explicit provider and credential setup.",
            [
                new("features.webBrowsing", "true", true, ["research-credential"], false),
                new("security.ward.enabled", "true", true, [], true),
                new("security.ward.autoDenyInUnattendedMode", "true", true, [], true),
                new("security.allowUnsandboxedToolChildren", "false", true, [], true),
            ],
            new ExpectedDisclosure(
                "Native web search and URL reading.",
                "No local memory, citation, retry, hop, timeout, or loop setting is changed.",
                "Research sends queries and selected page requests to external services.",
                "An inference provider/model and a securely stored Perplexity credential are required.",
                "External research can incur provider cost; existing explicit budgets remain unchanged."),
            [
                new(
                    "provider-model",
                    "A configured provider must advertise the selected model.",
                    "arcanum config open",
                    true),
                new(
                    "research-credential",
                    "The native research provider credential must already be stored securely.",
                    "arcanum key provider set perplexity",
                    true),
            ],
            [
                new(
                    "Run one cited research request.",
                    "arcanum run --research \"What changed?\"",
                    false),
            ],
            new ExpectedProgressiveDisclosure(
                "Store the research credential and review the external-data disclosure.",
                ["Semantic memory and custom research workflows remain optional."],
                "Run arcanum run --research \"What changed?\".")),
        new(
            "private-offline",
            1,
            "Private/Offline",
            "Keeps the primary runtime loopback-only and turns off built-in external research and telemetry.",
            [
                new("host.listenAny", "false", true, [], true),
                new("features.webBrowsing", "false", true, [], true),
                new("features.enterpriseTelemetry", "false", true, [], true),
                new("security.ward.enabled", "true", true, [], true),
                new("security.ward.autoDenyInUnattendedMode", "true", true, [], true),
                new("security.allowUnsandboxedToolChildren", "false", true, [], true),
            ],
            new ExpectedDisclosure(
                "Loopback inference with local attachments and per-call tool records.",
                "Built-in external web research, enterprise telemetry, and non-loopback host binding.",
                "Configured third-party integrations are not erased; inspect them before assuming fully offline operation.",
                "The selected inference provider endpoint must be loopback.",
                "No budget, storage-retention, or concurrency value is changed."),
            [
                new(
                    "loopback-provider",
                    "The selected provider endpoint must resolve to this computer's loopback interface.",
                    "arcanum config open",
                    true),
            ],
            [
                new(
                    "Verify the local provider answers without external research.",
                    "arcanum run \"Hello\"",
                    false),
            ],
            new ExpectedProgressiveDisclosure(
                "Choose a loopback provider and model.",
                ["Review MCP and other authored integration allowlists separately if strict offline operation is required."],
                "Run arcanum run \"Hello\".")),
        new(
            "automation",
            1,
            "Automation",
            "Enables unattended execution only after an operator supplies a positive explicit budget.",
            [
                new("security.ward.enabled", "true", true, [], true),
                new("security.ward.autoDenyInUnattendedMode", "true", true, [], true),
                new("security.ward.unattendedMode", "true", true, [], false),
                new("security.allowUnsandboxedToolChildren", "false", true, [], true),
            ],
            new ExpectedDisclosure(
                "Unattended ordinary tool execution with per-call Ward records.",
                "No unsandboxed child process, destructive memory action, or untrusted MCP server.",
                "Ordinary calls do not pause for approval; Covenant retirement keeps its independent authorization policy and existing tool permissions still apply.",
                "A configured inference provider/model is required.",
                "An already enabled, positive daily budget is required and is never invented or enlarged."),
            [
                new(
                    "provider-model",
                    "A configured provider must advertise the selected model.",
                    "arcanum config open",
                    true),
                new(
                    "positive-budget",
                    "Daily budget enforcement must already be enabled with a value greater than zero.",
                    "arcanum config open",
                    true),
            ],
            [
                new(
                    "Inspect daemon state before scheduling unattended work.",
                    "arcanum daemon status",
                    false),
            ],
            new ExpectedProgressiveDisclosure(
                "Set and review an explicit positive daily budget.",
                ["Jobs, schedules, apprentices, and integration-specific permissions remain separate choices."],
                "Run arcanum daemon status.")),
        new(
            "advanced-custom",
            1,
            "Advanced/Custom",
            "Leaves every configuration value operator-owned while exposing the same inspection tools.",
            [],
            new ExpectedDisclosure(
                "No capability automatically.",
                "Nothing automatically.",
                "The operator is responsible for reviewing every enabled capability and boundary.",
                "No provider is selected or modified.",
                "No budget, concurrency, retry, timeout, or loop value is changed."),
            [],
            [
                new(
                    "Inspect and validate the effective configuration.",
                    "arcanum config show",
                    false),
            ],
            new ExpectedProgressiveDisclosure(
                "Review the effective configuration before enabling advanced features.",
                ["All features remain individually configurable."],
                "Run arcanum config show.")),
    ];

    private static void AssertPresetMatches(
        ExpectedPreset expected,
        ConfigurationPresetDefinition actual)
    {

        Assert.Equal(expected.Id, actual.Id);

        Assert.Equal(expected.Version, actual.Version);

        Assert.Equal(expected.DisplayName, actual.DisplayName);

        Assert.Equal(expected.Purpose, actual.Purpose);

        Assert.Equal(expected.OwnedSettings.Length, actual.OwnedSettings.Length);

        for (int index = 0; index < expected.OwnedSettings.Length; index++)
        {

            ExpectedOwnedSetting expectedSetting = expected.OwnedSettings[index];

            ConfigurationPresetOwnedSetting actualSetting = actual.OwnedSettings[index];

            Assert.Equal(expectedSetting.Path, actualSetting.Path);

            Assert.Equal(expectedSetting.CanonicalJson, actualSetting.CanonicalJson);

            Assert.Equal(expectedSetting.RequiresRestart, actualSetting.RequiresRestart);

            Assert.Equal(expectedSetting.PrerequisiteIds, actualSetting.PrerequisiteIds.ToArray());

            Assert.Equal(expectedSetting.IsSafetyBoundary, actualSetting.IsSafetyBoundary);

        }

        Assert.Equal(expected.Disclosure.Enables, actual.Disclosure.Enables);

        Assert.Equal(expected.Disclosure.Disables, actual.Disclosure.Disables);

        Assert.Equal(
            expected.Disclosure.SecurityImplications,
            actual.Disclosure.SecurityImplications);

        Assert.Equal(
            expected.Disclosure.ProviderRequirements,
            actual.Disclosure.ProviderRequirements);

        Assert.Equal(
            expected.Disclosure.ResourceAndCostBehavior,
            actual.Disclosure.ResourceAndCostBehavior);

        AssertPrerequisitesMatch(expected.Prerequisites, actual.Prerequisites);

        AssertRecommendationsMatch(expected.Recommendations, actual.Recommendations);

        Assert.Equal(
            expected.ProgressiveDisclosure.EssentialChoice,
            actual.ProgressiveDisclosure.EssentialChoice);

        Assert.Equal(
            expected.ProgressiveDisclosure.DeferredFeatures,
            actual.ProgressiveDisclosure.DeferredFeatures.ToArray());

        Assert.Equal(
            expected.ProgressiveDisclosure.FirstSuccessRecommendation,
            actual.ProgressiveDisclosure.FirstSuccessRecommendation);

    }

    private static void AssertPrerequisitesMatch(
        ExpectedPrerequisite[] expected,
        IEnumerable<ConfigurationPresetPrerequisite> actual)
    {

        ConfigurationPresetPrerequisite[] actualArray = [.. actual];

        Assert.Equal(expected.Length, actualArray.Length);

        for (int index = 0; index < expected.Length; index++)
        {

            Assert.Equal(expected[index].Id, actualArray[index].Id);

            Assert.Equal(expected[index].Description, actualArray[index].Description);

            Assert.Equal(expected[index].ResolutionCommand, actualArray[index].ResolutionCommand);

            Assert.Equal(expected[index].Required, actualArray[index].Required);

        }

    }

    private static void AssertRecommendationsMatch(
        ExpectedRecommendation[] expected,
        IEnumerable<ConfigurationPresetRecommendation> actual)
    {

        ConfigurationPresetRecommendation[] actualArray = [.. actual];

        Assert.Equal(expected.Length, actualArray.Length);

        for (int index = 0; index < expected.Length; index++)
        {

            Assert.Equal(expected[index].Description, actualArray[index].Description);

            Assert.Equal(expected[index].Command, actualArray[index].Command);

            Assert.Equal(expected[index].IsAdvancedFeature, actualArray[index].IsAdvancedFeature);

        }

    }

    private sealed record ExpectedPreset(
        string Id,
        int Version,
        string DisplayName,
        string Purpose,
        ExpectedOwnedSetting[] OwnedSettings,
        ExpectedDisclosure Disclosure,
        ExpectedPrerequisite[] Prerequisites,
        ExpectedRecommendation[] Recommendations,
        ExpectedProgressiveDisclosure ProgressiveDisclosure);

    private sealed record ExpectedOwnedSetting(
        string Path,
        string CanonicalJson,
        bool RequiresRestart,
        string[] PrerequisiteIds,
        bool IsSafetyBoundary);

    private sealed record ExpectedDisclosure(
        string Enables,
        string Disables,
        string SecurityImplications,
        string ProviderRequirements,
        string ResourceAndCostBehavior);

    private sealed record ExpectedPrerequisite(
        string Id,
        string Description,
        string ResolutionCommand,
        bool Required);

    private sealed record ExpectedRecommendation(
        string Description,
        string Command,
        bool IsAdvancedFeature);

    private sealed record ExpectedProgressiveDisclosure(
        string EssentialChoice,
        string[] DeferredFeatures,
        string FirstSuccessRecommendation);

}
