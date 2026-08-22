using System.Collections.Immutable;

using System.Text.Json;

using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Core.Configuration.Presets;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Hosting;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Collection("GlobalConsole")]
public sealed class PresetCommandTests
{

    [Fact]

    public void Source_generated_json_contract_serializes_the_shared_preset_plan()
    {

        FakeConfigurationPresetService presetService = new();

        string json = JsonSerializer.Serialize(
            presetService.DiffResult.Value,
            CliJsonContext.Default.ConfigurationPresetPlan);

        Assert.Contains("\"diff\"", json, StringComparison.Ordinal);

    }

    [Fact]
    public void Root_help_lists_preset_command_family()
    {

        ServiceCollection services = CreateServices();

        CliTestResult result = CliTestHarness.Run(services, "--help");

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Contains("preset", result.Output, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public void Preset_help_lists_complete_command_family()
    {

        ServiceCollection services = CreateServices();

        CliTestResult result = CliTestHarness.Run(services, "preset", "--help");

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Contains("list", result.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("show", result.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("diff", result.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("apply", result.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("reset", result.Output, StringComparison.OrdinalIgnoreCase);

    }

    [Theory]
    [InlineData("show")]
    [InlineData("diff")]
    [InlineData("apply")]
    public void Preset_name_commands_require_a_name(string command)
    {

        ServiceCollection services = CreateServices();

        CliTestResult result = CliTestHarness.Run(services, "preset", command);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Contains(
            "Required argument missing",
            result.Error,
            StringComparison.OrdinalIgnoreCase);

    }

    [Theory]
    [InlineData("--json")]
    [InlineData("--plain")]
    [InlineData("--yes")]
    [InlineData("--no-context")]
    public void Global_flags_remain_recursive_for_preset_commands(string flag)
    {

        ServiceCollection services = CreateServices();

        CliTestResult result = CliTestHarness.Run(
            services,
            "preset",
            "apply",
            "--help",
            flag);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.DoesNotContain(
            "Unrecognized command or argument",
            result.Error,
            StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public void List_renders_concise_catalog_and_effective_state()
    {

        FakeConfigurationPresetService presetService = new();

        ServiceCollection services = CreateServices(presetService);

        CliTestResult result = CliTestHarness.Run(services, "preset", "list");

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Equal(1, presetService.InspectCalls);

        Assert.Contains("General Assistant", result.Output, StringComparison.Ordinal);

        Assert.Contains("general-assistant", result.Output, StringComparison.Ordinal);

        Assert.Contains("Active", result.Output, StringComparison.Ordinal);

        Assert.Contains("preset show <name>", result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain("Owned settings", result.Output, StringComparison.Ordinal);

    }

    [Fact]
    public void List_json_is_one_typed_payload_with_effective_state()
    {

        FakeConfigurationPresetService presetService = new();

        ServiceCollection services = CreateServices(presetService);

        CliTestResult result = CliTestHarness.Run(
            services,
            "preset",
            "list",
            "--json");

        using JsonDocument document = JsonDocument.Parse(result.Output);

        JsonElement preset = document.RootElement
            .GetProperty("presets")[0];

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Equal("general-assistant", preset.GetProperty("id").GetString());

        Assert.Equal("Active", preset.GetProperty("effectiveState").GetString());

        Assert.False(document.RootElement.TryGetProperty("exitCode", out _));

    }

    [Fact]
    public void Show_renders_disclosures_progressive_details_setup_and_shared_glossary()
    {

        FakeConfigurationPresetService presetService = new();

        ServiceCollection services = CreateServices(presetService);

        CliTestResult result = CliTestHarness.Run(
            services,
            "preset",
            "show",
            "general-assistant");

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Equal("general-assistant", presetService.LastFindName);

        Assert.Contains("Enables:", result.Output, StringComparison.Ordinal);

        Assert.Contains("Disables:", result.Output, StringComparison.Ordinal);

        Assert.Contains("Security implications:", result.Output, StringComparison.Ordinal);

        Assert.Contains("Provider requirements:", result.Output, StringComparison.Ordinal);

        Assert.Contains("Resource and cost behavior:", result.Output, StringComparison.Ordinal);

        Assert.Contains("arcanum config open", result.Output, StringComparison.Ordinal);

        Assert.Contains("Deferred advanced features", result.Output, StringComparison.Ordinal);

        Assert.Contains("After first success", result.Output, StringComparison.Ordinal);

        Assert.Contains("Ward — approval gate", result.Output, StringComparison.Ordinal);

        Assert.Contains("Sanctum — workspace sandbox", result.Output, StringComparison.Ordinal);

        Assert.Contains("providers.0.endpoint = ***", result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain(
            FakeConfigurationPresetService.SensitiveValue,
            result.Output,
            StringComparison.Ordinal);

    }

    [Fact]
    public void Show_unknown_preset_is_an_actionable_configuration_error()
    {

        FakeConfigurationPresetService presetService = new();

        ServiceCollection services = CreateServices(presetService);

        CliTestResult result = CliTestHarness.Run(
            services,
            "preset",
            "show",
            "unknown");

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Contains("Unknown preset", result.Error, StringComparison.Ordinal);

        Assert.Contains("general-assistant", result.Error, StringComparison.Ordinal);

    }

    [Fact]
    public void Diff_renders_every_required_value_and_prerequisite_field()
    {

        FakeConfigurationPresetService presetService = new();

        ServiceCollection services = CreateServices(presetService);

        CliTestResult result = CliTestHarness.Run(
            services,
            "preset",
            "diff",
            "general-assistant");

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Equal("general-assistant", presetService.LastDiffName);

        Assert.Contains("Persisted value: false", result.Output, StringComparison.Ordinal);

        Assert.Contains("Effective value: true", result.Output, StringComparison.Ordinal);

        Assert.Contains("Proposed persisted value: true", result.Output, StringComparison.Ordinal);

        Assert.Contains("Current source: EnvironmentOverride", result.Output, StringComparison.Ordinal);

        Assert.Contains("ARCANUM_Arcanum__Features__Attachments", result.Output, StringComparison.Ordinal);

        Assert.Contains("Owned by preset: yes", result.Output, StringComparison.Ordinal);

        Assert.Contains("Restart required: yes", result.Output, StringComparison.Ordinal);

        Assert.Contains("Persisted value changes: yes", result.Output, StringComparison.Ordinal);

        Assert.Contains("Effective value changes: no", result.Output, StringComparison.Ordinal);

        Assert.Contains("arcanum config open", result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain(
            FakeConfigurationPresetService.EnvironmentValue,
            result.Output,
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            FakeConfigurationPresetService.SensitiveValue,
            result.Output,
            StringComparison.Ordinal);

    }

    [Fact]
    public void Diff_json_uses_the_shared_plan_shape_without_environment_values()
    {

        FakeConfigurationPresetService presetService = new();

        ServiceCollection services = CreateServices(presetService);

        CliTestResult result = CliTestHarness.Run(
            services,
            "preset",
            "diff",
            "general-assistant",
            "--json");

        Assert.True(
            result.ExitCode == (int)CliExitCode.Success,
            $"stdout: {result.Output}{System.Environment.NewLine}stderr: {result.Error}");

        using JsonDocument document = JsonDocument.Parse(result.Output);

        JsonElement row = document.RootElement.GetProperty("diff")[0];

        Assert.Equal("features.attachments", row.GetProperty("path").GetString());

        Assert.Equal(
            "ARCANUM_Arcanum__Features__Attachments",
            row.GetProperty("environmentVariable").GetString());

        Assert.DoesNotContain(
            FakeConfigurationPresetService.EnvironmentValue,
            result.Output,
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            FakeConfigurationPresetService.SensitiveValue,
            result.Output,
            StringComparison.Ordinal);

    }

    [Fact]
    public void Apply_is_noninteractive_and_renders_the_shared_completion_summary()
    {

        FakeConfigurationPresetService presetService = new();

        ServiceCollection services = CreateServices(presetService);

        CliTestResult result = CliTestHarness.Run(
            services,
            "preset",
            "apply",
            "general-assistant");

        Assert.True(
            result.ExitCode == (int)CliExitCode.Success,
            $"stdout: {result.Output}{System.Environment.NewLine}stderr: {result.Error}");

        Assert.Equal("general-assistant", presetService.LastApplyName);

        Assert.Contains("Preset applied atomically", result.Output, StringComparison.Ordinal);

        Assert.Contains("Rollback status: SnapshotCreated", result.Output, StringComparison.Ordinal);

        AssertCompletionSummary(result.Output);

        Assert.Empty(result.Error);

    }

    [Fact]
    public void Apply_reports_idempotent_reapplication_honestly()
    {

        FakeConfigurationPresetService presetService = new();

        ConfigurationPresetApplyResult applied = presetService.ApplyResult.Value;

        presetService.ApplyResult = Result<ConfigurationPresetApplyResult>.Success(
            applied with
            {

                Applied = false,

                AlreadyApplied = true,

                RollbackStatus = ConfigurationPresetRollbackStatus.NotRequired,

            });

        ServiceCollection services = CreateServices(presetService);

        CliTestResult result = CliTestHarness.Run(
            services,
            "preset",
            "apply",
            "general-assistant");

        Assert.True(
            result.ExitCode == (int)CliExitCode.Success,
            $"stdout: {result.Output}{System.Environment.NewLine}stderr: {result.Error}");

        Assert.Contains("already applied", result.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("configuration is unchanged", result.Output, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public void Apply_json_uses_the_shared_result_and_redacts_sensitive_values()
    {

        FakeConfigurationPresetService presetService = new();

        ServiceCollection services = CreateServices(presetService);

        CliTestResult result = CliTestHarness.Run(
            services,
            "preset",
            "apply",
            "general-assistant",
            "--json");

        Assert.True(
            result.ExitCode == (int)CliExitCode.Success,
            $"stdout: {result.Output}{System.Environment.NewLine}stderr: {result.Error}");

        using JsonDocument document = JsonDocument.Parse(result.Output);

        Assert.True(document.RootElement.GetProperty("applied").GetBoolean());

        Assert.Equal(
            "SnapshotCreated",
            document.RootElement.GetProperty("rollbackStatus").GetString());

        Assert.Equal(
            "General Assistant v1",
            document.RootElement
                .GetProperty("inspection")
                .GetProperty("completionSummary")
                .GetProperty("activePreset")
                .GetString());

        Assert.DoesNotContain(
            FakeConfigurationPresetService.SensitiveValue,
            result.Output,
            StringComparison.Ordinal);

    }

    [Fact]
    public void Apply_missing_required_prerequisite_reports_exact_setup_command_and_fails()
    {

        FakeConfigurationPresetService presetService = new();

        ConfigurationPresetApplyResult applied = presetService.ApplyResult.Value;

        presetService.ApplyResult = Result<ConfigurationPresetApplyResult>.Success(
            applied with
            {

                Applied = false,

                Plan = applied.Plan with { IsApplicable = false },

                RollbackStatus = ConfigurationPresetRollbackStatus.NotRequired,

            });

        ServiceCollection services = CreateServices(presetService);

        CliTestResult result = CliTestHarness.Run(
            services,
            "preset",
            "apply",
            "general-assistant");

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Contains("Preset was not applied", result.Output, StringComparison.Ordinal);

        Assert.Contains("arcanum config open", result.Output, StringComparison.Ordinal);

    }

    [Fact]
    public void Reset_without_active_preset_is_an_idempotent_success()
    {

        FakeConfigurationPresetService presetService = new();

        ConfigurationPresetResetResult reset = presetService.ResetResult.Value;

        presetService.ResetResult = Result<ConfigurationPresetResetResult>.Success(
            reset with
            {

                Reset = false,

                RestoredSettingCount = 0,

                RollbackStatus = ConfigurationPresetRollbackStatus.NotRequired,

            });

        ServiceCollection services = CreateServices(presetService);

        CliTestResult result = CliTestHarness.Run(services, "preset", "reset");

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Equal(1, presetService.ResetCalls);

        Assert.Contains("No active preset", result.Output, StringComparison.Ordinal);

        AssertCompletionSummary(result.Output);

    }

    [Fact]
    public void Reset_reports_failed_rollback_as_configuration_error()
    {

        FakeConfigurationPresetService presetService = new();

        ConfigurationPresetResetResult reset = presetService.ResetResult.Value;

        presetService.ResetResult = Result<ConfigurationPresetResetResult>.Success(
            reset with
            {

                Reset = false,

                RollbackStatus = ConfigurationPresetRollbackStatus.Failed,

            });

        ServiceCollection services = CreateServices(presetService);

        CliTestResult result = CliTestHarness.Run(services, "preset", "reset");

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Contains("Rollback status: Failed", result.Output, StringComparison.Ordinal);

    }

    [Fact]
    public void Reset_json_uses_the_shared_result_shape()
    {

        FakeConfigurationPresetService presetService = new();

        ServiceCollection services = CreateServices(presetService);

        CliTestResult result = CliTestHarness.Run(
            services,
            "preset",
            "reset",
            "--json");

        Assert.True(
            result.ExitCode == (int)CliExitCode.Success,
            $"stdout: {result.Output}{System.Environment.NewLine}stderr: {result.Error}");

        using JsonDocument document = JsonDocument.Parse(result.Output);

        Assert.True(document.RootElement.GetProperty("reset").GetBoolean());

        Assert.Equal(
            "Restored",
            document.RootElement.GetProperty("rollbackStatus").GetString());

        Assert.Equal(1, document.RootElement.GetProperty("restoredSettingCount").GetInt32());

    }

    [Theory]
    [InlineData("A running host owns the maintenance lock.")]
    [InlineData("The maintenance lock topology is unsafe.")]
    [InlineData("An installation factory reset is active.")]
    public async Task Refused_exclusive_ownership_blocks_every_preset_persistence_interaction(
        string refusal)
    {

        foreach (string[] arguments in new[]
                 {

                     new[] { "preset", "list" },

                     new[] { "preset", "show", "general-assistant" },

                     new[] { "preset", "diff", "general-assistant" },

                     new[] { "preset", "apply", "general-assistant" },

                     new[] { "preset", "reset" },

                 })
        {

            FakeConfigurationPresetService presetService = new();

            RecordingGrimoireCliInitialization initialization = new(refusal);

            CliTestResult result = await CliTestHarness.RunAsync(
                CreateServices(presetService, initialization),
                arguments);

            Assert.Equal((int)CliExitCode.GenericError, result.ExitCode);

            Assert.Equal(1, initialization.ExclusiveCalls);

            Assert.Equal(0, initialization.BootstrapCalls);

            Assert.Equal(0, presetService.PersistenceInteractionCount);

        }

    }

    private static ServiceCollection CreateServices(
        FakeConfigurationPresetService? presetService = null,
        IGrimoireCliInitialization? initialization = null)
    {

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        services.RemoveAll<IGrimoireCliInitialization>();

        services.AddSingleton<IGrimoireCliInitialization>(
            initialization ?? new RecordingGrimoireCliInitialization());

        services.AddSingleton<IConfigurationPresetService>(
            presetService ?? new FakeConfigurationPresetService());

        return services;

    }

    private static void AssertCompletionSummary(string output)
    {

        Assert.Contains("Setup completion summary", output, StringComparison.Ordinal);

        Assert.Contains("Active preset: General Assistant v1", output, StringComparison.Ordinal);

        Assert.Contains("Provider/model: OpenAI / gpt-test", output, StringComparison.Ordinal);

        Assert.Contains("Workspace/campaign: /workspace / Campaign", output, StringComparison.Ordinal);

        Assert.Contains("Enabled memory sources: Attachments, Lexicon", output, StringComparison.Ordinal);

        Assert.Contains("Tool policy: Ward approval gate enabled", output, StringComparison.Ordinal);

        Assert.Contains("Privacy state: Loopback only", output, StringComparison.Ordinal);

        Assert.Contains(
            "Next recommended command: arcanum run",
            output,
            StringComparison.Ordinal);

    }

    private sealed class FakeConfigurationPresetService : IConfigurationPresetService
    {

        public const string EnvironmentValue = "do-not-render-environment-value";

        public const string SensitiveValue = "https://sensitive-provider.example/v1";

        private readonly ConfigurationPresetDefinition _definition;

        public FakeConfigurationPresetService()
        {

            _definition = Definition();

            ConfigurationPresetPlan plan = Plan(_definition);

            ConfigurationPresetInspection inspection = Inspection(_definition, plan);

            DiffResult = Result<ConfigurationPresetPlan>.Success(plan);

            ApplyResult = Result<ConfigurationPresetApplyResult>.Success(
                new ConfigurationPresetApplyResult(
                    plan,
                    inspection,
                    Applied: true,
                    AlreadyApplied: false,
                    ConfigurationPresetRollbackStatus.SnapshotCreated));

            ResetResult = Result<ConfigurationPresetResetResult>.Success(
                new ConfigurationPresetResetResult(
                    inspection,
                    Reset: true,
                    RestoredSettingCount: 1,
                    PreservedDriftCount: 1,
                    ConfigurationPresetRollbackStatus.Restored));

            InspectResult = Result<ConfigurationPresetInspection>.Success(inspection);

        }

        public Result<ConfigurationPresetPlan> DiffResult { get; set; }

        public Result<ConfigurationPresetApplyResult> ApplyResult { get; set; }

        public Result<ConfigurationPresetResetResult> ResetResult { get; set; }

        public Result<ConfigurationPresetInspection> InspectResult { get; set; }

        public string? LastFindName { get; private set; }

        public string? LastDiffName { get; private set; }

        public string? LastApplyName { get; private set; }

        public int InspectCalls { get; private set; }

        public int ResetCalls { get; private set; }

        public int PersistenceInteractionCount { get; private set; }

        public IReadOnlyList<ConfigurationPresetDefinition> List() => [_definition];

        public IReadOnlyList<ConfigurationPresetGlossaryEntry> Glossary() =>
        [
            new("Ward", "approval gate"),

            new("Sanctum", "workspace sandbox"),

            new("Weave", "semantic index"),

            new("Saga", "long-term extracted memory"),

            new("Lexicon", "explicit entity memory"),
        ];

        public ConfigurationPresetDefinition? Find(string idOrName)
        {

            LastFindName = idOrName;

            return string.Equals(
                    idOrName,
                    _definition.Id,
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    idOrName,
                    _definition.DisplayName,
                    StringComparison.OrdinalIgnoreCase)
                ? _definition
                : null;

        }

        public Task<Result<ConfigurationPresetPlan>> DiffAsync(
            string idOrName,
            CancellationToken cancellationToken = default)
        {

            LastDiffName = idOrName;

            PersistenceInteractionCount++;

            return Task.FromResult(DiffResult);

        }

        public Task<Result<ConfigurationPresetApplyResult>> ApplyAsync(
            string idOrName,
            CancellationToken cancellationToken = default)
        {

            LastApplyName = idOrName;

            PersistenceInteractionCount++;

            return Task.FromResult(ApplyResult);

        }

        public Task<Result<ConfigurationPresetResetResult>> ResetAsync(
            CancellationToken cancellationToken = default)
        {

            ResetCalls++;

            PersistenceInteractionCount++;

            return Task.FromResult(ResetResult);

        }

        public Task<Result<ConfigurationPresetInspection>> InspectAsync(
            CancellationToken cancellationToken = default)
        {

            InspectCalls++;

            PersistenceInteractionCount++;

            return Task.FromResult(InspectResult);

        }

        private static ConfigurationPresetDefinition Definition() =>
            new(
                "general-assistant",
                1,
                "General Assistant",
                "A practical assistant for everyday work.",
                ImmutableArray.Create(
                    new ConfigurationPresetOwnedSetting(
                        "features.attachments",
                        "true"),
                    new ConfigurationPresetOwnedSetting(
                        "providers.0.endpoint",
                        $"\"{SensitiveValue}\"")),
                new ConfigurationPresetDisclosure(
                    "Attachments and conservative memory.",
                    "No unattended high-risk tools.",
                    "Ward and Sanctum protections remain active.",
                    "A configured provider and model are required.",
                    "Uses the operator's configured model and cost policy."),
                ImmutableArray.Create(
                    new ConfigurationPresetPrerequisite(
                        "provider",
                        "Configure a provider and model.",
                        "arcanum config open",
                        Required: true)),
                ImmutableArray.Create(
                    new ConfigurationPresetRecommendation(
                        "Try one inference.",
                        "arcanum run")),
                new ConfigurationPresetProgressiveDisclosure(
                    "Choose a provider and model.",
                    ImmutableArray.Create("Web research", "Semantic retrieval"),
                    "Explore optional advanced features after a successful inference."));

        private static ConfigurationPresetPlan Plan(
            ConfigurationPresetDefinition definition)
        {

            ConfigurationPresetPrerequisite prerequisite = definition.Prerequisites[0];

            return new ConfigurationPresetPlan(
                definition,
                ConfigurationPresetEffectiveState.Active,
                ImmutableArray.Create(
                    new ConfigurationPresetDiffRow(
                        "features.attachments",
                        "false",
                        "true",
                        "true",
                        ConfigurationPresetValueSource.EnvironmentOverride,
                        "ARCANUM_Arcanum__Features__Attachments",
                        EnvironmentOverrideIsEffective: true,
                        OwnedByPreset: true,
                        ImmutableArray.Create("provider"),
                        RequiresRestart: true,
                        PersistedValueChanges: true,
                        EffectiveValueChanges: false),
                    new ConfigurationPresetDiffRow(
                        "providers.0.endpoint",
                        SensitiveValue,
                        SensitiveValue,
                        SensitiveValue,
                        ConfigurationPresetValueSource.Persisted,
                        EnvironmentVariable: null,
                        EnvironmentOverrideIsEffective: false,
                        OwnedByPreset: true,
                        PrerequisiteIds: ImmutableArray<string>.Empty,
                        RequiresRestart: true,
                        PersistedValueChanges: false,
                        EffectiveValueChanges: false)),
                ImmutableArray.Create(
                    new ConfigurationPresetPrerequisiteStatus(
                        prerequisite,
                        IsSatisfied: false,
                        "No provider is configured.")),
                "owned-values-hash",
                IsApplicable: true,
                IsIdempotent: false,
                Summary());

        }

        private static ConfigurationPresetInspection Inspection(
            ConfigurationPresetDefinition definition,
            ConfigurationPresetPlan plan) =>
            new(
                ConfigurationPresetEffectiveState.Active,
                definition,
                AppliedVersion: 1,
                AppliedAt: DateTimeOffset.Parse("2026-08-03T12:00:00Z"),
                OwnedValuesHash: "owned-values-hash",
                plan.Diff,
                Summary());

        private static ConfigurationPresetCompletionSummary Summary() =>
            new(
                "General Assistant v1",
                "OpenAI / gpt-test",
                "/workspace / Campaign",
                ImmutableArray.Create("Attachments", "Lexicon"),
                "Ward approval gate enabled",
                "Loopback only",
                "arcanum run");

    }

}
