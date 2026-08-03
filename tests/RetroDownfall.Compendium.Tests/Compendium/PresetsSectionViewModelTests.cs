using System.Collections.Immutable;

using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Configuration.Presets;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Compendium.Ux.Services;

using RetroDownfall.Compendium.Ux.ViewModels;

using Xunit;

namespace RetroDownfall.Compendium.Ux.Tests.Compendium;

public sealed class PresetsSectionViewModelTests
{

    [Fact]

    public async Task Selection_exposes_the_shared_definition_and_exact_shared_diff()
    {

        PresetFixture fixture = new();

        using InMemoryConfigurationStore store = new();

        TrackingDialogService dialogs = new();

        ConfigurationViewModel root = CreateRoot(store, dialogs, fixture.Service);

        await WaitForLoadAsync(root);

        ConfigurationPresetDefinition listed = Assert.Single(root.Presets.Definitions);

        Assert.Same(fixture.Definition, listed);

        await root.Presets.SelectPresetCommand.ExecuteAsync(listed);

        Assert.Same(fixture.Definition, root.Presets.SelectedPreset);

        ConfigurationPresetPlan selectedPlan =
            Assert.IsType<ConfigurationPresetPlan>(root.Presets.Plan);

        Assert.Same(fixture.Plan, selectedPlan);

        Assert.Equal(ConfigurationPresetEffectiveState.Custom, root.Presets.EffectiveState);

        Assert.Equal(ConfigurationPresetEffectiveState.Drifted, selectedPlan.State);

        Assert.Same(fixture.Definition.Disclosure, root.Presets.Disclosure);

        Assert.Equal(fixture.Definition.Recommendations, root.Presets.Recommendations);

        Assert.Equal(
            fixture.Definition.ProgressiveDisclosure,
            root.Presets.ProgressiveDisclosure);

        ConfigurationPresetDiffRow row = Assert.Single(root.Presets.Diff);

        Assert.Equal("features.webBrowsing", row.Path);

        Assert.Equal("false", row.PersistedValue);

        Assert.Equal("true", row.EffectiveValue);

        Assert.Equal("true", row.ProposedPersistedValue);

        Assert.Equal(
            ConfigurationPresetValueSource.EnvironmentOverride,
            row.CurrentSource);

        Assert.Equal("ARCANUM_Arcanum__Features__WebBrowsing", row.EnvironmentVariable);

        Assert.True(row.EnvironmentOverrideIsEffective);

        Assert.True(row.RequiresRestart);

        Assert.True(row.PersistedValueChanges);

        Assert.False(row.EffectiveValueChanges);

        Assert.Single(root.Presets.Prerequisites);

        Assert.Equal(
            root.Presets.Inspection!.CompletionSummary,
            root.Presets.CompletionSummary);

        Assert.Equal(
            fixture.Plan.CompletionSummary,
            root.Presets.PreviewCompletionSummary);

    }

    [Fact]

    public async Task Previewing_another_preset_preserves_the_canonical_active_state_and_summary()
    {

        PresetFixture fixture = new();

        ConfigurationPresetDefinition active =
            ConfigurationPresetCatalog.Find("general-assistant")!;

        ConfigurationPresetCompletionSummary activeSummary =
            fixture.Plan.CompletionSummary with { ActivePreset = active.DisplayName };

        fixture.Service.Inspection = new ConfigurationPresetInspection(
            ConfigurationPresetEffectiveState.Active,
            active,
            active.Version,
            DateTimeOffset.Parse("2026-08-03T12:00:00Z"),
            "active-owned-values-hash",
            [],
            activeSummary);

        fixture.Service.Plan = fixture.Plan with
        {

            State = ConfigurationPresetEffectiveState.Custom,

        };

        using InMemoryConfigurationStore store = new();

        ConfigurationViewModel root = CreateRoot(
            store,
            new TrackingDialogService(),
            fixture.Service);

        await WaitForLoadAsync(root);

        await root.Presets.SelectPresetCommand.ExecuteAsync(fixture.Definition);

        Assert.Equal(ConfigurationPresetEffectiveState.Active, root.Presets.EffectiveState);

        Assert.Equal(activeSummary, root.Presets.CompletionSummary);

        Assert.Equal(
            fixture.Plan.CompletionSummary,
            root.Presets.PreviewCompletionSummary);

        Assert.Collection(
            root.Presets.CompletionSummaries,
            current =>
            {

                Assert.Equal("Current effective summary", current.Heading);

                Assert.Equal(activeSummary, current.Summary);

            },
            preview =>
            {

                Assert.Equal("Selected preset projection", preview.Heading);

                Assert.Equal(fixture.Plan.CompletionSummary, preview.Summary);

            });

    }

    [Fact]

    public async Task Unsaved_configuration_edits_disable_preset_mutations_with_clear_guidance()
    {

        PresetFixture fixture = new();

        using InMemoryConfigurationStore store = new();

        TrackingDialogService dialogs = new();

        ConfigurationViewModel root = CreateRoot(store, dialogs, fixture.Service);

        await WaitForLoadAsync(root);

        await root.Presets.SelectPresetCommand.ExecuteAsync(fixture.Definition);

        root.MarkDirty();

        Assert.False(root.Presets.ApplyCommand.CanExecute(null));

        Assert.False(root.Presets.ResetCommand.CanExecute(null));

        Assert.Contains(
            "Save or cancel",
            root.Presets.MutationBlockReason,
            StringComparison.OrdinalIgnoreCase);

        Assert.Equal(0, fixture.Service.ApplyCallCount);

        Assert.Equal(0, fixture.Service.ResetCallCount);

        Assert.Equal(0, dialogs.ConfirmCallCount);

    }

    [Fact]

    public async Task Refresh_invalidates_the_previously_reviewed_plan()
    {

        PresetFixture fixture = new();

        using InMemoryConfigurationStore store = new();

        ConfigurationViewModel root = CreateRoot(
            store,
            new TrackingDialogService(),
            fixture.Service);

        await WaitForLoadAsync(root);

        await root.Presets.SelectPresetCommand.ExecuteAsync(fixture.Definition);

        Assert.NotNull(root.Presets.Plan);

        Assert.True(root.Presets.ApplyCommand.CanExecute(null));

        await root.Presets.RefreshCommand.ExecuteAsync(null);

        Assert.Null(root.Presets.Plan);

        Assert.False(root.Presets.ApplyCommand.CanExecute(null));

    }

    [Fact]

    public async Task Failed_refresh_clears_cached_effective_state_instead_of_presenting_it_as_current()
    {

        PresetFixture fixture = new();

        fixture.Service.Inspection = fixture.Service.ApplyResult.Inspection;

        using InMemoryConfigurationStore store = new();

        ConfigurationViewModel root = CreateRoot(
            store,
            new TrackingDialogService(),
            fixture.Service);

        await WaitForLoadAsync(root);

        Assert.NotNull(root.Presets.Inspection);

        fixture.Service.InspectError = new Error(
            "Preset.SidecarInvalid",
            "Preset state could not be verified.");

        await root.Presets.RefreshCommand.ExecuteAsync(null);

        Assert.Null(root.Presets.Inspection);

        Assert.Null(root.Presets.CompletionSummary);

        Assert.Equal("Unavailable", root.Presets.EffectiveStateDisplay);

        Assert.Empty(root.Presets.CompletionSummaries);

        Assert.Contains(
            "could not be verified",
            root.Presets.StatusMessage,
            StringComparison.Ordinal);

        fixture.Service.InspectError = null;

        await root.Presets.RefreshCommand.ExecuteAsync(null);

        Assert.NotNull(root.Presets.Inspection);

        fixture.Service.InspectException = new InvalidOperationException(
            "Preset state read crashed.");

        await root.Presets.RefreshCommand.ExecuteAsync(null);

        Assert.Null(root.Presets.Inspection);

        Assert.Equal("Unavailable", root.Presets.EffectiveStateDisplay);

        Assert.Contains(
            "read crashed",
            root.Presets.StatusMessage,
            StringComparison.Ordinal);

    }

    [Fact]

    public async Task Explicit_apply_and_reset_clicks_show_fresh_inspection_without_confirmation()
    {

        PresetFixture fixture = new();

        using InMemoryConfigurationStore store = new();

        TrackingDialogService dialogs = new();

        ConfigurationViewModel root = CreateRoot(store, dialogs, fixture.Service);

        await WaitForLoadAsync(root);

        await root.Presets.SelectPresetCommand.ExecuteAsync(fixture.Definition);

        int readsBeforeApply = store.ReadCallCount;

        await root.Presets.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(1, fixture.Service.ApplyCallCount);

        Assert.Equal(0, dialogs.ConfirmCallCount);

        Assert.True(store.ReadCallCount > readsBeforeApply);

        Assert.Equal(ConfigurationPresetEffectiveState.Drifted, root.Presets.EffectiveState);

        Assert.Null(root.Presets.Plan);

        ConfigurationPresetDiffRow appliedDrift = Assert.Single(root.Presets.Diff);

        Assert.Equal("features.webBrowsing", appliedDrift.Path);

        Assert.Equal("false", appliedDrift.ProposedPersistedValue);

        int readsBeforeReset = store.ReadCallCount;

        await root.Presets.ResetCommand.ExecuteAsync(null);

        Assert.Equal(1, fixture.Service.ResetCallCount);

        Assert.Equal(0, dialogs.ConfirmCallCount);

        Assert.True(store.ReadCallCount > readsBeforeReset);

        Assert.Equal(ConfigurationPresetEffectiveState.Custom, root.Presets.EffectiveState);

        Assert.Null(root.Presets.Plan);

        Assert.Empty(root.Presets.Diff);

        Assert.Contains("preserved 0", root.Presets.StatusMessage, StringComparison.Ordinal);

    }

    private static ConfigurationViewModel CreateRoot(
        IArcanumConfigurationStore store,
        IDialogService dialogs,
        IConfigurationPresetService presetService) =>
        new(
            store,
            dialogs,
            new SynchronousUiDispatcher(),
            NullLogger<ConfigurationViewModel>.Instance,
            presetService: presetService);

    private static async Task WaitForLoadAsync(ConfigurationViewModel root)
    {

        for (int attempt = 0; attempt < 100; attempt++)
        {

            if (root.StatusMessage.StartsWith("Loaded", StringComparison.Ordinal))
            {

                return;

            }

            await Task.Delay(10);

        }

        Assert.Fail($"Timed out waiting for configuration load. Status={root.StatusMessage}");

    }

    private sealed class InMemoryConfigurationStore : IArcanumConfigurationStore
    {

        public int ReadCallCount { get; private set; }

        public string ConfigurationFilePath => "memory://arcanum.json";

        public event EventHandler? ExternalChange
        {

            add { }

            remove { }

        }

        public DateTimeOffset? GetLastWriteTimeUtc() => null;

        public Task<ArcanumSettings> ReadAsync(CancellationToken ct = default)
        {

            ReadCallCount++;

            return Task.FromResult(new ArcanumSettings());

        }

        public Task<ConfigurationWriteResult> WriteAsync(
            ArcanumSettings settings,
            CancellationToken ct = default) =>
            Task.FromResult(new ConfigurationWriteResult(true, [], null));

        public void Dispose()
        {

        }

    }

    private sealed class TrackingDialogService : IDialogService
    {

        public int ConfirmCallCount { get; private set; }

        public Task ShowAlertAsync(
            string title,
            string message,
            string cancel = "OK") =>
            Task.CompletedTask;

        public Task<bool> ShowConfirmAsync(
            string title,
            string message,
            string accept = "Yes",
            string cancel = "No")
        {

            ConfirmCallCount++;

            return Task.FromResult(true);

        }

    }

    private sealed class FakeConfigurationPresetService : IConfigurationPresetService
    {

        public required ConfigurationPresetDefinition Definition { get; init; }

        public required ConfigurationPresetPlan Plan { get; set; }

        public required ConfigurationPresetInspection Inspection { get; set; }

        public required ConfigurationPresetApplyResult ApplyResult { get; init; }

        public required ConfigurationPresetResetResult ResetResult { get; init; }

        public int ApplyCallCount { get; private set; }

        public int ResetCallCount { get; private set; }

        public Error? InspectError { get; set; }

        public Exception? InspectException { get; set; }

        public IReadOnlyList<ConfigurationPresetDefinition> List() => [Definition];

        public IReadOnlyList<ConfigurationPresetGlossaryEntry> Glossary() => [];

        public ConfigurationPresetDefinition? Find(string idOrName) =>
            string.Equals(idOrName, Definition.Id, StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    idOrName,
                    Definition.DisplayName,
                    StringComparison.OrdinalIgnoreCase)
                ? Definition
                : null;

        public Task<Result<ConfigurationPresetPlan>> DiffAsync(
            string idOrName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<ConfigurationPresetPlan>.Success(Plan));

        public Task<Result<ConfigurationPresetApplyResult>> ApplyAsync(
            string idOrName,
            CancellationToken cancellationToken = default)
        {

            ApplyCallCount++;

            Inspection = ApplyResult.Inspection;

            return Task.FromResult(
                Result<ConfigurationPresetApplyResult>.Success(ApplyResult));

        }

        public Task<Result<ConfigurationPresetResetResult>> ResetAsync(
            CancellationToken cancellationToken = default)
        {

            ResetCallCount++;

            Inspection = ResetResult.Inspection;

            return Task.FromResult(
                Result<ConfigurationPresetResetResult>.Success(ResetResult));

        }

        public Task<Result<ConfigurationPresetInspection>> InspectAsync(
            CancellationToken cancellationToken = default)
        {

            if (InspectException is { } inspectException)
            {

                throw inspectException;

            }

            Error? inspectError = InspectError;

            return Task.FromResult(
                inspectError is null
                    ? Result<ConfigurationPresetInspection>.Success(Inspection)
                    : Result<ConfigurationPresetInspection>.Failure(inspectError.Value));

        }

    }

    private sealed class PresetFixture
    {

        public PresetFixture()
        {

            Definition = new ConfigurationPresetDefinition(
                "research",
                1,
                "Research",
                "Research with validated web access and citations.",
                ImmutableArray.Create(
                    new ConfigurationPresetOwnedSetting(
                        "features.webBrowsing",
                        "true")),
                new ConfigurationPresetDisclosure(
                    "Validated web research.",
                    "Unattended high-risk tools.",
                    "Ward — approval gate remains enabled.",
                    "A configured research provider is required.",
                    "Uses conservative cost and source behavior."),
                ImmutableArray.Create(
                    new ConfigurationPresetPrerequisite(
                        "research-provider",
                        "A research provider credential is available.",
                        "arcanum key provider set perplexity",
                        true)),
                ImmutableArray.Create(
                    new ConfigurationPresetRecommendation(
                        "Run a cited research request.",
                        "arcanum research \"question\"")),
                new ConfigurationPresetProgressiveDisclosure(
                    "Choose Research for cited web work.",
                    ImmutableArray.Create("Attachment RAG"),
                    "Inspect advanced retrieval after the first successful inference."));

            ConfigurationPresetDiffRow diff = new(
                "features.webBrowsing",
                "false",
                "true",
                "true",
                ConfigurationPresetValueSource.EnvironmentOverride,
                "ARCANUM_Arcanum__Features__WebBrowsing",
                true,
                true,
                ImmutableArray.Create("research-provider"),
                true,
                true,
                false);

            ConfigurationPresetCompletionSummary summary = new(
                "Research v1",
                "Provider / model",
                "Workspace / Campaign",
                ImmutableArray.Create("Lexicon — explicit entity memory"),
                "Ward — approval gate enabled; Sanctum — workspace sandbox retained.",
                "Remote research enabled after validation.",
                "arcanum research \"question\"");

            Plan = new ConfigurationPresetPlan(
                Definition,
                ConfigurationPresetEffectiveState.Drifted,
                ImmutableArray.Create(diff),
                ImmutableArray.Create(
                    new ConfigurationPresetPrerequisiteStatus(
                        Definition.Prerequisites[0],
                        true,
                        "Available")),
                "owned-values-hash",
                true,
                false,
                summary);

            ConfigurationPresetInspection customInspection = new(
                ConfigurationPresetEffectiveState.Custom,
                null,
                null,
                null,
                null,
                ImmutableArray<ConfigurationPresetDiffRow>.Empty,
                summary with { ActivePreset = "Custom" });

            ConfigurationPresetDiffRow activeDrift = diff with
            {

                PersistedValue = "true",

                EffectiveValue = "true",

                ProposedPersistedValue = "false",

                CurrentSource = ConfigurationPresetValueSource.Persisted,

                EnvironmentVariable = null,

                EnvironmentOverrideIsEffective = false,

                PersistedValueChanges = true,

                EffectiveValueChanges = true,

            };

            ConfigurationPresetInspection postApplyInspection = new(
                ConfigurationPresetEffectiveState.Drifted,
                Definition,
                Definition.Version,
                DateTimeOffset.Parse("2026-08-03T12:00:00Z"),
                "owned-values-hash",
                ImmutableArray.Create(activeDrift),
                summary);

            ConfigurationPresetInspection resetInspection = customInspection;

            Service = new FakeConfigurationPresetService
            {

                Definition = Definition,

                Plan = Plan,

                Inspection = customInspection,

                ApplyResult = new ConfigurationPresetApplyResult(
                    Plan,
                    postApplyInspection,
                    true,
                    false,
                    ConfigurationPresetRollbackStatus.SnapshotCreated),

                ResetResult = new ConfigurationPresetResetResult(
                    resetInspection,
                    true,
                    1,
                    0,
                    ConfigurationPresetRollbackStatus.Restored),

            };

        }

        public ConfigurationPresetDefinition Definition { get; }

        public ConfigurationPresetPlan Plan { get; }

        public FakeConfigurationPresetService Service { get; }

    }

}
