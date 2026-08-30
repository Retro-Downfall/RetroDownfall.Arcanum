using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Configuration.Presets;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Configuration;

public sealed class ConfigurationPresetPlannerTests
{

    [Fact]

    public void Plan_changes_only_owned_values_and_preserves_custom_configuration()
    {

        ArcanumSettings settings = ValidSettings();

        settings.Cli.ShowManaBar = false;

        settings.Features.Conclave = true;

        ConfigurationPresetSnapshot snapshot = Snapshot(settings);

        ConfigurationPresetDefinition preset = ConfigurationPresetCatalog.Find("coding-workspace")!;

        Result<ConfigurationPresetPlanningResult> result = new ConfigurationPresetPlanner().Plan(
            preset,
            snapshot,
            new ConfigurationPresetPlanningContext(Workspace: "/workspace"));

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.True(result.Value.Plan.IsApplicable);

        Assert.True(result.Value.CandidateSettings.Workspaces.EnableFileWrite);

        Assert.False(result.Value.CandidateSettings.Cli.ShowManaBar);

        Assert.True(result.Value.CandidateSettings.Features.Conclave);

        Assert.All(result.Value.Plan.Diff, static row => Assert.True(row.OwnedByPreset));

    }

    [Fact]

    public void Plan_shows_persisted_effective_and_proposed_values_when_environment_masks_change()
    {

        ArcanumSettings settings = ValidSettings();

        settings.Features.WebBrowsing = false;

        ConfigurationEnvironmentSnapshot environment = ConfigurationEnvironmentResolver.Resolve(
            settings,
            new Dictionary<string, string?>
            {

                ["ARCANUM_Arcanum__Features__WebBrowsing"] = "false",

            });

        ConfigurationPresetSnapshot snapshot = new(settings, environment, null);

        ConfigurationPresetDefinition preset = ConfigurationPresetCatalog.Find("research")!;

        Result<ConfigurationPresetPlanningResult> result = new ConfigurationPresetPlanner().Plan(
            preset,
            snapshot,
            new ConfigurationPresetPlanningContext(ResearchCredentialAvailable: true));

        Assert.True(result.IsSuccess, result.Error.Message);

        ConfigurationPresetDiffRow row = Assert.Single(
            result.Value.Plan.Diff,
            static candidate => candidate.Path == "features.webBrowsing");

        Assert.Equal("false", row.PersistedValue);

        Assert.Equal("false", row.EffectiveValue);

        Assert.Equal("true", row.ProposedPersistedValue);

        Assert.Equal(ConfigurationPresetValueSource.EnvironmentOverride, row.CurrentSource);

        Assert.Equal("ARCANUM_Arcanum__Features__WebBrowsing", row.EnvironmentVariable);

        Assert.True(row.EnvironmentOverrideIsEffective);

        Assert.True(row.PersistedValueChanges);

        Assert.False(row.EffectiveValueChanges);

    }

    [Fact]

    public void Plan_is_blocked_by_actionable_research_prerequisite_without_hiding_the_proposal()
    {

        ArcanumSettings settings = ValidSettings();

        ConfigurationPresetDefinition preset = ConfigurationPresetCatalog.Find("research")!;

        Result<ConfigurationPresetPlanningResult> result = new ConfigurationPresetPlanner().Plan(
            preset,
            Snapshot(settings),
            new ConfigurationPresetPlanningContext(ResearchCredentialAvailable: false));

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.False(result.Value.Plan.IsApplicable);

        ConfigurationPresetPrerequisiteStatus status = Assert.Single(
            result.Value.Plan.Prerequisites,
            static candidate => candidate.Prerequisite.Id == "research-credential");

        Assert.False(status.IsSatisfied);

        Assert.Equal("arcanum key provider set perplexity", status.Prerequisite.ResolutionCommand);

        Assert.Contains(
            result.Value.Plan.Diff,
            static row => row.Path == "features.webBrowsing" && row.ProposedPersistedValue == "true");

    }

    [Fact]

    public void Private_offline_requires_the_selected_provider_to_be_loopback()
    {

        ArcanumSettings settings = ValidSettings("https://api.example.com/v1");

        ConfigurationPresetDefinition preset = ConfigurationPresetCatalog.Find("private-offline")!;

        Result<ConfigurationPresetPlanningResult> result = new ConfigurationPresetPlanner().Plan(
            preset,
            Snapshot(settings));

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.False(result.Value.Plan.IsApplicable);

        Assert.Contains(
            result.Value.Plan.Prerequisites,
            static status =>
                status.Prerequisite.Id == "loopback-provider"
                && !status.IsSatisfied
                && status.Prerequisite.ResolutionCommand == "arcanum config open");

    }

    [Fact]

    public void Automation_requires_existing_enabled_positive_budget_and_does_not_invent_one()
    {

        ArcanumSettings settings = ValidSettings();

        settings.Cost.Budget.Enabled = true;

        settings.Cost.Budget.DailyLimitUsd = 0m;

        ConfigurationPresetDefinition preset = ConfigurationPresetCatalog.Find("automation")!;

        Result<ConfigurationPresetPlanningResult> result = new ConfigurationPresetPlanner().Plan(
            preset,
            Snapshot(settings));

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.False(result.Value.Plan.IsApplicable);

        Assert.Equal(0m, result.Value.CandidateSettings.Cost.Budget.DailyLimitUsd);

        Assert.Contains(
            result.Value.Plan.Prerequisites,
            static status => status.Prerequisite.Id == "positive-budget" && !status.IsSatisfied);

    }

    [Fact]

    public void Reapplying_the_same_version_and_owned_values_is_idempotent()
    {

        ArcanumSettings settings = ValidSettings();

        ConfigurationPresetDefinition preset = ConfigurationPresetCatalog.Find("general-assistant")!;

        ConfigurationPresetPlanner planner = new();

        Result<ConfigurationPresetPlanningResult> first = planner.Plan(preset, Snapshot(settings));

        Assert.True(first.IsSuccess, first.Error.Message);

        ConfigurationPresetProvenance provenance = new(
            preset.Id,
            preset.Version,
            DateTimeOffset.Parse("2026-08-03T12:00:00Z"),
            first.Value.Plan.OwnedValuesHash,
            first.Value.BaselineValues,
            first.Value.AppliedValues);

        ConfigurationPresetSnapshot applied = Snapshot(
            first.Value.CandidateSettings,
            provenance);

        Result<ConfigurationPresetPlanningResult> second = planner.Plan(preset, applied);

        Assert.True(second.IsSuccess, second.Error.Message);

        Assert.Equal(ConfigurationPresetEffectiveState.Active, second.Value.Plan.State);

        Assert.True(second.Value.Plan.IsIdempotent);

        Assert.DoesNotContain(second.Value.Plan.Diff, static row => row.PersistedValueChanges);

    }

    [Fact]

    public void Planning_a_different_preset_does_not_label_the_target_as_active()
    {

        ArcanumSettings settings = ValidSettings();

        ConfigurationPresetPlanner planner = new();

        ConfigurationPresetDefinition general =
            ConfigurationPresetCatalog.Find("general-assistant")!;

        ConfigurationPresetPlanningResult generalPlan = planner
            .Plan(general, Snapshot(settings))
            .Value;

        ConfigurationPresetProvenance provenance = new(
            general.Id,
            general.Version,
            DateTimeOffset.Parse("2026-08-03T12:00:00Z"),
            generalPlan.Plan.OwnedValuesHash,
            generalPlan.BaselineValues,
            generalPlan.AppliedValues);

        ConfigurationPresetDefinition research =
            ConfigurationPresetCatalog.Find("research")!;

        Result<ConfigurationPresetPlanningResult> result = planner.Plan(
            research,
            Snapshot(generalPlan.CandidateSettings, provenance),
            new ConfigurationPresetPlanningContext(ResearchCredentialAvailable: true));

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(ConfigurationPresetEffectiveState.Custom, result.Value.Plan.State);

    }

    [Fact]

    public void Inspect_reports_drift_when_an_owned_value_changes_after_apply()
    {

        ArcanumSettings settings = ValidSettings();

        ConfigurationPresetDefinition preset = ConfigurationPresetCatalog.Find("general-assistant")!;

        ConfigurationPresetPlanner planner = new();

        ConfigurationPresetPlanningResult first = planner.Plan(preset, Snapshot(settings)).Value;

        ConfigurationPresetProvenance provenance = new(
            preset.Id,
            preset.Version,
            DateTimeOffset.Parse("2026-08-03T12:00:00Z"),
            first.Plan.OwnedValuesHash,
            first.BaselineValues,
            first.AppliedValues);

        first.CandidateSettings.Security.AllowUnsandboxedToolChildren = true;

        ConfigurationPresetInspection inspection = planner.Inspect(
            Snapshot(first.CandidateSettings, provenance));

        Assert.Equal(ConfigurationPresetEffectiveState.Drifted, inspection.State);

        Assert.Contains(
            inspection.Drift,
            static row => row.Path == "security.allowUnsandboxedToolChildren"
                && row.PersistedValueChanges);

    }

    [Fact]

    public void Invalid_embedding_configuration_is_an_actionable_candidate_prerequisite()
    {

        ArcanumSettings settings = ValidSettings();

        settings.Features.Embeddings = true;

        settings.Integrations.Embeddings.Provider = "missing-provider";

        settings.Integrations.Embeddings.Model = "embedding-model";

        ConfigurationPresetDefinition preset = ConfigurationPresetCatalog.Find("general-assistant")!;

        Result<ConfigurationPresetPlanningResult> result = new ConfigurationPresetPlanner().Plan(
            preset,
            Snapshot(settings));

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.False(result.Value.Plan.IsApplicable);

        Assert.Contains(
            result.Value.Plan.Prerequisites,
            static status =>
                status.Prerequisite.Id == "valid-configuration"
                && !status.IsSatisfied
                && status.Prerequisite.ResolutionCommand == "arcanum config validate");

    }

    [Fact]

    public void Automation_uses_the_effective_environment_budget_for_prerequisite_readiness()
    {

        ArcanumSettings settings = ValidSettings();

        settings.Cost.Budget.Enabled = true;

        settings.Cost.Budget.DailyLimitUsd = 10m;

        ConfigurationEnvironmentSnapshot environment = ConfigurationEnvironmentResolver.Resolve(
            settings,
            new Dictionary<string, string?>
            {

                ["ARCANUM_Arcanum__Cost__Budget__DailyLimitUsd"] = "0",

            });

        ConfigurationPresetDefinition preset = ConfigurationPresetCatalog.Find("automation")!;

        Result<ConfigurationPresetPlanningResult> result = new ConfigurationPresetPlanner().Plan(
            preset,
            new ConfigurationPresetSnapshot(settings, environment, null));

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.False(result.Value.Plan.IsApplicable);

        Assert.Contains(
            result.Value.Plan.Prerequisites,
            static status =>
                status.Prerequisite.Id == "positive-budget"
                && !status.IsSatisfied);

    }

    [Fact]

    public void Automation_blocks_when_environment_masks_an_owned_safety_value()
    {

        ArcanumSettings settings = ValidSettings();

        settings.Cost.Budget.Enabled = true;

        settings.Cost.Budget.DailyLimitUsd = 10m;

        ConfigurationEnvironmentSnapshot environment = ConfigurationEnvironmentResolver.Resolve(
            settings,
            new Dictionary<string, string?>
            {

                ["ARCANUM_Arcanum__Security__AllowUnsandboxedToolChildren"] = "true",

            });

        ConfigurationPresetDefinition preset = ConfigurationPresetCatalog.Find("automation")!;

        Result<ConfigurationPresetPlanningResult> result = new ConfigurationPresetPlanner().Plan(
            preset,
            new ConfigurationPresetSnapshot(settings, environment, null));

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.False(result.Value.Plan.IsApplicable);

        ConfigurationPresetPrerequisiteStatus masked = Assert.Single(
            result.Value.Plan.Prerequisites,
            static status => status.Prerequisite.Id == "environment-override");

        Assert.False(masked.IsSatisfied);

        Assert.Contains(
            "ARCANUM_Arcanum__Security__AllowUnsandboxedToolChildren",
            masked.Detail,
            StringComparison.Ordinal);

    }

    [Fact]

    public void Coding_workspace_allows_a_benign_environment_mask_and_reports_effective_drift()
    {

        ArcanumSettings settings = ValidSettings();

        settings.Workspaces.EnableFileWrite = false;

        ConfigurationEnvironmentSnapshot environment = ConfigurationEnvironmentResolver.Resolve(
            settings,
            new Dictionary<string, string?>
            {

                ["ARCANUM_Arcanum__Workspaces__EnableFileWrite"] = "false",

            });

        ConfigurationPresetDefinition preset = ConfigurationPresetCatalog.Find("coding-workspace")!;

        Result<ConfigurationPresetPlanningResult> result = new ConfigurationPresetPlanner().Plan(
            preset,
            new ConfigurationPresetSnapshot(settings, environment, null),
            new ConfigurationPresetPlanningContext(Workspace: "/workspace"));

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.True(result.Value.Plan.IsApplicable);

        Assert.DoesNotContain(
            result.Value.Plan.Prerequisites,
            static status => status.Prerequisite.Id == "environment-override");

        ConfigurationPresetDiffRow row = Assert.Single(
            result.Value.Plan.Diff,
            static candidate => candidate.Path == "workspaces.enableFileWrite");

        Assert.Equal(ConfigurationPresetValueSource.EnvironmentOverride, row.CurrentSource);

        Assert.True(row.EnvironmentOverrideIsEffective);

        Assert.True(row.PersistedValueChanges);

        Assert.False(row.EffectiveValueChanges);

    }

    [Fact]

    public void Private_offline_reports_and_blocks_an_environment_masked_network_binding()
    {

        ArcanumSettings settings = ValidSettings();

        ConfigurationEnvironmentSnapshot environment = ConfigurationEnvironmentResolver.Resolve(
            settings,
            new Dictionary<string, string?>
            {

                ["ARCANUM_HOST_ANY"] = "true",

            });

        ConfigurationPresetDefinition preset = ConfigurationPresetCatalog.Find("private-offline")!;

        Result<ConfigurationPresetPlanningResult> result = new ConfigurationPresetPlanner().Plan(
            preset,
            new ConfigurationPresetSnapshot(settings, environment, null));

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.False(result.Value.Plan.IsApplicable);

        Assert.Contains(
            "network host binding enabled",
            result.Value.Plan.CompletionSummary.PrivacyState,
            StringComparison.Ordinal);

        Assert.Contains(
            result.Value.Plan.Prerequisites,
            static status => status.Prerequisite.Id == "environment-override");

    }

    [Fact]

    public void Full_settings_hash_is_stable_for_equivalent_snapshots_and_changes_with_custom_values()
    {

        ArcanumSettings first = ValidSettings();

        ArcanumSettings second = ValidSettings();

        Assert.Equal(
            ConfigurationPresetHash.ComputeSettings(first),
            ConfigurationPresetHash.ComputeSettings(second));

        second.Cli.ShowManaBar = false;

        Assert.NotEqual(
            ConfigurationPresetHash.ComputeSettings(first),
            ConfigurationPresetHash.ComputeSettings(second));

    }

    [Fact]

    public void Inspect_tolerates_null_policy_sections_that_the_validator_accepts()
    {

        ArcanumSettings settings = SettingsWithNullPolicySections();

        Assert.True(new ConfigurationValidator().Validate(settings).IsSuccess);

        ConfigurationPresetInspection inspection = new ConfigurationPresetPlanner().Inspect(
            Snapshot(settings));

        Assert.Equal(ConfigurationPresetEffectiveState.Custom, inspection.State);

        Assert.Equal("Custom", inspection.CompletionSummary.ActivePreset);

    }

    [Theory]

    [InlineData(false)]

    [InlineData(true)]

    public void Completion_summary_never_presents_ordinary_Ward_as_an_approval_gate(
        bool unattendedMode)
    {
        ArcanumSettings settings = ValidSettings();

        settings.Security.Ward.UnattendedMode = unattendedMode;

        ConfigurationPresetInspection inspection = new ConfigurationPresetPlanner().Inspect(
            Snapshot(settings));

        Assert.Equal(
            "Ordinary tool calls are Ward-recorded without approval; Covenant retirement keeps its independent authorization policy; Sanctum path boundaries remain active.",
            inspection.CompletionSummary.ToolPolicy);

    }

    [Fact]

    public void Plan_tolerates_a_null_cost_section_when_evaluating_the_budget_prerequisite()
    {

        ArcanumSettings settings = SettingsWithNullPolicySections();

        ConfigurationPresetDefinition preset = ConfigurationPresetCatalog.Find("automation")!;

        Result<ConfigurationPresetPlanningResult> result = new ConfigurationPresetPlanner().Plan(
            preset,
            Snapshot(settings));

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.False(result.Value.Plan.IsApplicable);

        Assert.Contains(
            result.Value.Plan.Prerequisites,
            static status =>
                status.Prerequisite.Id == ConfigurationPresetCatalog.PositiveBudgetPrerequisite
                && !status.IsSatisfied);

    }

    private static ArcanumSettings SettingsWithNullPolicySections()
    {

        ArcanumSettings settings = ValidSettings();

        settings.Cost = null!;

        settings.Features = null!;

        settings.Security = null!;

        settings.Workspaces = null!;

        settings.Host = null!;

        return settings;

    }

    private static ConfigurationPresetSnapshot Snapshot(
        ArcanumSettings settings,
        ConfigurationPresetProvenance? provenance = null) =>
        new(
            settings,
            ConfigurationEnvironmentResolver.Resolve(
                settings,
                new Dictionary<string, string?>()),
            provenance);

    private static ArcanumSettings ValidSettings(string endpoint = "http://127.0.0.1:11434/v1") =>
        new()
        {

            Providers =
            [
                new ProviderSettings
                {

                    Name = "local",

                    Endpoint = endpoint,

                    Models =
                    [
                        new ModelEntry
                        {

                            Name = "test-model",

                        },
                    ],

                },
            ],

            DefaultModel = "test-model",

        };

}
