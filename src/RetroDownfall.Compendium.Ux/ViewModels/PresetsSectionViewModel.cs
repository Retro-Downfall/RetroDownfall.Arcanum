using System.Collections.Immutable;

using System.ComponentModel;

using CommunityToolkit.Mvvm.ComponentModel;

using CommunityToolkit.Mvvm.Input;

using RetroDownfall.Arcanum.Core.Configuration.Presets;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Compendium.Ux.Services;

namespace RetroDownfall.Compendium.Ux.ViewModels;

public sealed record PresetCompletionSummaryCard(
    string Heading,
    ConfigurationPresetCompletionSummary Summary);

public sealed partial class PresetsSectionViewModel : ObservableObject
{

    private readonly ConfigurationViewModel _root;

    private readonly IConfigurationPresetService? _service;

    private readonly IUiDispatcher _uiDispatcher;

    [ObservableProperty]
    private ConfigurationPresetDefinition? _selectedPreset;

    [ObservableProperty]
    private ConfigurationPresetPlan? _plan;

    [ObservableProperty]
    private ConfigurationPresetInspection? _inspection;

    [ObservableProperty]
    private ConfigurationPresetEffectiveState _effectiveState =
        ConfigurationPresetEffectiveState.Custom;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public PresetsSectionViewModel(
        ConfigurationViewModel root,
        IConfigurationPresetService? service,
        IUiDispatcher uiDispatcher)
    {

        _root = root;

        _service = service;

        _uiDispatcher = uiDispatcher;

        Definitions = service?.List() ?? [];

        Glossary = service?.Glossary() ?? [];

        SelectPresetCommand = new AsyncRelayCommand<ConfigurationPresetDefinition>(
            SelectPresetAsync,
            _ => _service is not null && !IsBusy);

        ApplyCommand = new AsyncRelayCommand(
            ApplyAsync,
            CanApply);

        ResetCommand = new AsyncRelayCommand(
            ResetAsync,
            CanReset);

        RefreshCommand = new AsyncRelayCommand(
            RefreshAsync,
            () => _service is not null && !IsBusy);

        _root.PropertyChanged += OnRootPropertyChanged;

    }

    public IReadOnlyList<ConfigurationPresetDefinition> Definitions { get; }

    public IReadOnlyList<ConfigurationPresetGlossaryEntry> Glossary { get; }

    public ConfigurationPresetDisclosure? Disclosure => SelectedPreset?.Disclosure;

    public ImmutableArray<ConfigurationPresetRecommendation> Recommendations =>
        SelectedPreset?.Recommendations
        ?? ImmutableArray<ConfigurationPresetRecommendation>.Empty;

    public ConfigurationPresetProgressiveDisclosure? ProgressiveDisclosure =>
        SelectedPreset?.ProgressiveDisclosure;

    public ImmutableArray<ConfigurationPresetDiffRow> Diff =>
        Plan?.Diff
        ?? Inspection?.Drift
        ?? ImmutableArray<ConfigurationPresetDiffRow>.Empty;

    public ImmutableArray<ConfigurationPresetPrerequisiteStatus> Prerequisites =>
        Plan?.Prerequisites
        ?? ImmutableArray<ConfigurationPresetPrerequisiteStatus>.Empty;

    public ConfigurationPresetCompletionSummary? CompletionSummary =>
        Inspection?.CompletionSummary;

    public ConfigurationPresetCompletionSummary? PreviewCompletionSummary =>
        Plan?.CompletionSummary;

    public IReadOnlyList<PresetCompletionSummaryCard> CompletionSummaries
    {

        get
        {

            List<PresetCompletionSummaryCard> summaries = [];

            if (CompletionSummary is { } current)
            {

                summaries.Add(new PresetCompletionSummaryCard(
                    "Current effective summary",
                    current));

            }

            if (PreviewCompletionSummary is { } preview)
            {

                summaries.Add(new PresetCompletionSummaryCard(
                    "Selected preset projection",
                    preview));

            }

            return summaries;

        }

    }

    public bool HasPreview => Plan is not null;

    public string EffectiveStateDisplay =>
        Inspection is null ? "Unavailable" : EffectiveState.ToString();

    public string MutationBlockReason
    {

        get
        {

            if (_root.IsDirty)
            {

                return "Save or cancel your current configuration edits before applying or resetting a preset.";

            }

            if (_root.IsSaving)
            {

                return "Wait for the current configuration save to finish.";

            }

            if (_root.HasExternalChange)
            {

                return "Reload the configuration changed on disk before applying or resetting a preset.";

            }

            if (_service is null)
            {

                return "Preset operations are not available in this session.";

            }

            return string.Empty;

        }

    }

    public IAsyncRelayCommand<ConfigurationPresetDefinition> SelectPresetCommand { get; }

    public IAsyncRelayCommand ApplyCommand { get; }

    public IAsyncRelayCommand ResetCommand { get; }

    public IAsyncRelayCommand RefreshCommand { get; }

    internal async Task RefreshAsync()
    {

        if (_service is null || IsBusy)
        {

            return;

        }

        await SetBusyAsync(true).ConfigureAwait(false);

        await _uiDispatcher.InvokeAsync(() =>
        {

            Plan = null;

            NotifyPresentationChanged();

        }).ConfigureAwait(false);

        try
        {

            Result<ConfigurationPresetInspection> result =
                await _service.InspectAsync(CancellationToken.None).ConfigureAwait(false);

            await _uiDispatcher.InvokeAsync(() =>
            {

                if (result.IsFailure)
                {

                    Inspection = null;

                    EffectiveState = ConfigurationPresetEffectiveState.Custom;

                    StatusMessage = result.Error.Message;

                    NotifyPresentationChanged();

                    return;

                }

                Inspection = result.Value;

                EffectiveState = result.Value.State;

                StatusMessage = "Preset state refreshed.";

                if (SelectedPreset is null && result.Value.ActivePreset is not null)
                {

                    SelectedPreset = result.Value.ActivePreset;

                }

                NotifyPresentationChanged();

            }).ConfigureAwait(false);

        }
        catch (Exception ex)
        {

            await _uiDispatcher.InvokeAsync(() =>
            {

                Inspection = null;

                EffectiveState = ConfigurationPresetEffectiveState.Custom;

                StatusMessage = $"Could not inspect presets: {ex.Message}";

                NotifyPresentationChanged();

            }).ConfigureAwait(false);

        }
        finally
        {

            await SetBusyAsync(false).ConfigureAwait(false);

        }

    }

    private async Task SelectPresetAsync(ConfigurationPresetDefinition? preset)
    {

        if (_service is null || preset is null || IsBusy)
        {

            return;

        }

        await SetBusyAsync(true).ConfigureAwait(false);

        await _uiDispatcher.InvokeAsync(() =>
        {

            SelectedPreset = preset;

            Plan = null;

            StatusMessage = $"Previewing {preset.DisplayName}.";

            NotifyPresentationChanged();

        }).ConfigureAwait(false);

        try
        {

            Result<ConfigurationPresetPlan> result =
                await _service.DiffAsync(preset.Id, CancellationToken.None).ConfigureAwait(false);

            await _uiDispatcher.InvokeAsync(() =>
            {

                if (result.IsFailure)
                {

                    StatusMessage = result.Error.Message;

                    return;

                }

                Plan = result.Value;

                StatusMessage = result.Value.IsApplicable
                    ? "Preview ready. Review every change before applying."
                    : "Resolve the listed prerequisites before applying.";

                NotifyPresentationChanged();

            }).ConfigureAwait(false);

        }
        catch (Exception ex)
        {

            await _uiDispatcher
                .InvokeAsync(() => StatusMessage = $"Could not preview the preset: {ex.Message}")
                .ConfigureAwait(false);

        }
        finally
        {

            await SetBusyAsync(false).ConfigureAwait(false);

        }

    }

    private async Task ApplyAsync()
    {

        if (!CanApply() || _service is null || SelectedPreset is null)
        {

            return;

        }

        string presetId = SelectedPreset.Id;

        await SetBusyAsync(true).ConfigureAwait(false);

        try
        {

            Result<ConfigurationPresetApplyResult> result =
                await _service.ApplyAsync(presetId, CancellationToken.None).ConfigureAwait(false);

            if (result.IsFailure)
            {

                await _uiDispatcher
                    .InvokeAsync(() => StatusMessage = result.Error.Message)
                    .ConfigureAwait(false);

                return;

            }

            await _root.ReloadAfterPresetMutationAsync().ConfigureAwait(false);

            await _uiDispatcher.InvokeAsync(() =>
            {

                Plan = null;

                Inspection = result.Value.Inspection;

                EffectiveState = result.Value.Inspection.State;

                StatusMessage = result.Value.AlreadyApplied
                    ? $"{result.Value.Plan.Preset.DisplayName} was already active."
                    : $"Applied {result.Value.Plan.Preset.DisplayName}.";

                NotifyPresentationChanged();

            }).ConfigureAwait(false);

        }
        catch (Exception ex)
        {

            await _uiDispatcher
                .InvokeAsync(() => StatusMessage = $"Could not apply the preset: {ex.Message}")
                .ConfigureAwait(false);

        }
        finally
        {

            await SetBusyAsync(false).ConfigureAwait(false);

        }

    }

    private async Task ResetAsync()
    {

        if (!CanReset() || _service is null)
        {

            return;

        }

        await SetBusyAsync(true).ConfigureAwait(false);

        try
        {

            Result<ConfigurationPresetResetResult> result =
                await _service.ResetAsync(CancellationToken.None).ConfigureAwait(false);

            if (result.IsFailure)
            {

                await _uiDispatcher
                    .InvokeAsync(() => StatusMessage = result.Error.Message)
                    .ConfigureAwait(false);

                return;

            }

            await _root.ReloadAfterPresetMutationAsync().ConfigureAwait(false);

            await _uiDispatcher.InvokeAsync(() =>
            {

                Plan = null;

                Inspection = result.Value.Inspection;

                EffectiveState = result.Value.Inspection.State;

                StatusMessage = result.Value.Reset
                    ? $"Reset {result.Value.RestoredSettingCount} preset-owned settings; "
                        + $"preserved {result.Value.PreservedDriftCount} user changes."
                    : "No active preset needed resetting.";

                NotifyPresentationChanged();

            }).ConfigureAwait(false);

        }
        catch (Exception ex)
        {

            await _uiDispatcher
                .InvokeAsync(() => StatusMessage = $"Could not reset the preset: {ex.Message}")
                .ConfigureAwait(false);

        }
        finally
        {

            await SetBusyAsync(false).ConfigureAwait(false);

        }

    }

    private bool CanApply() =>
        _service is not null
        && SelectedPreset is not null
        && Plan?.IsApplicable == true
        && !IsBusy
        && string.IsNullOrEmpty(MutationBlockReason);

    private bool CanReset() =>
        _service is not null
        && Inspection?.ActivePreset is not null
        && !IsBusy
        && string.IsNullOrEmpty(MutationBlockReason);

    private async Task SetBusyAsync(bool value)
    {

        await _uiDispatcher.InvokeAsync(() =>
        {

            IsBusy = value;

            NotifyCommandsChanged();

        }).ConfigureAwait(false);

    }

    private void OnRootPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {

        if (e.PropertyName is nameof(ConfigurationViewModel.IsDirty)
            or nameof(ConfigurationViewModel.IsSaving)
            or nameof(ConfigurationViewModel.HasExternalChange))
        {

            OnPropertyChanged(nameof(MutationBlockReason));

            NotifyCommandsChanged();

        }

    }

    partial void OnSelectedPresetChanged(ConfigurationPresetDefinition? value) =>
        NotifyPresentationChanged();

    partial void OnPlanChanged(ConfigurationPresetPlan? value) =>
        NotifyPresentationChanged();

    partial void OnInspectionChanged(ConfigurationPresetInspection? value) =>
        NotifyPresentationChanged();

    private void NotifyPresentationChanged()
    {

        OnPropertyChanged(nameof(Disclosure));

        OnPropertyChanged(nameof(Recommendations));

        OnPropertyChanged(nameof(ProgressiveDisclosure));

        OnPropertyChanged(nameof(Diff));

        OnPropertyChanged(nameof(Prerequisites));

        OnPropertyChanged(nameof(CompletionSummary));

        OnPropertyChanged(nameof(PreviewCompletionSummary));

        OnPropertyChanged(nameof(CompletionSummaries));

        OnPropertyChanged(nameof(HasPreview));

        OnPropertyChanged(nameof(EffectiveStateDisplay));

        NotifyCommandsChanged();

    }

    private void NotifyCommandsChanged()
    {

        SelectPresetCommand.NotifyCanExecuteChanged();

        ApplyCommand.NotifyCanExecuteChanged();

        ResetCommand.NotifyCanExecuteChanged();

        RefreshCommand.NotifyCanExecuteChanged();

    }

}
