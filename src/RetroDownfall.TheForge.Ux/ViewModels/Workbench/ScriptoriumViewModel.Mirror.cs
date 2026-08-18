using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services.Diff;

namespace RetroDownfall.TheForge.Ux.ViewModels.Workbench;

/// <summary>Prompt Mirror — version list, fetch-by-id, multi-field diff (no activate-prompt).</summary>
public sealed partial class ScriptoriumViewModel
{

    private string _persistedTemplate = string.Empty;

    private string _persistedDescription = string.Empty;

    private string _persistedVersion = string.Empty;

    private string _persistedTagsText = string.Empty;

    private string _persistedModel = string.Empty;

    private string _persistedProvider = string.Empty;

    private string _persistedTemperatureText = string.Empty;

    private string _persistedTopPText = string.Empty;

    private string _persistedMaxOutputTokensText = string.Empty;

    // Seeded to match the unloaded editor fields exactly. Seeding these two to "{}" instead made a
    // freshly opened Scriptorium report IsEditorDirty before the operator had typed anything, which
    // the Load guard would then read as a buffer worth protecting.
    private string _persistedParameterSchemaJson = string.Empty;

    private string _persistedDefaultParametersJson = string.Empty;

    /// <summary>
    /// Monotonic ticket for mirror refreshes. Only the newest refresh may write the mirror surface,
    /// so rapid version selection cannot leave a superseded version's diff on screen.
    /// </summary>
    private int _mirrorRefreshGeneration;

    [ObservableProperty]
    private PromptDetailDto? _mirrorComparedPrompt;

    [ObservableProperty]
    private string _mirrorStatusText = string.Empty;

    public BulkObservableCollection<DiffLineItem> MirrorDiffLines { get; } = [];

    public bool IsEditorDirty =>
        !string.Equals(Template, _persistedTemplate, StringComparison.Ordinal)
        || !string.Equals(Description, _persistedDescription, StringComparison.Ordinal)
        || !string.Equals(Version, _persistedVersion, StringComparison.Ordinal)
        || !string.Equals(TagsText, _persistedTagsText, StringComparison.Ordinal)
        || !string.Equals(Model, _persistedModel, StringComparison.Ordinal)
        || !string.Equals(Provider, _persistedProvider, StringComparison.Ordinal)
        || !string.Equals(TemperatureText, _persistedTemperatureText, StringComparison.Ordinal)
        || !string.Equals(TopPText, _persistedTopPText, StringComparison.Ordinal)
        || !string.Equals(MaxOutputTokensText, _persistedMaxOutputTokensText, StringComparison.Ordinal)
        || !string.Equals(ParameterSchemaJson, _persistedParameterSchemaJson, StringComparison.Ordinal)
        || !string.Equals(DefaultParametersJson, _persistedDefaultParametersJson, StringComparison.Ordinal);

    public bool ShowDirtyComparisonWarning => IsEditorDirty;

    partial void OnTemplateChanged(string value) => OnPropertyChanged(nameof(ShowDirtyComparisonWarning));

    partial void OnDescriptionChanged(string value) => OnPropertyChanged(nameof(ShowDirtyComparisonWarning));

    partial void OnVersionChanged(string value) => OnPropertyChanged(nameof(ShowDirtyComparisonWarning));

    partial void OnTagsTextChanged(string value) => OnPropertyChanged(nameof(ShowDirtyComparisonWarning));

    partial void OnModelChanged(string value) => OnPropertyChanged(nameof(ShowDirtyComparisonWarning));

    partial void OnProviderChanged(string value) => OnPropertyChanged(nameof(ShowDirtyComparisonWarning));

    partial void OnTemperatureTextChanged(string value) => OnPropertyChanged(nameof(ShowDirtyComparisonWarning));

    partial void OnTopPTextChanged(string value) => OnPropertyChanged(nameof(ShowDirtyComparisonWarning));

    partial void OnMaxOutputTokensTextChanged(string value) => OnPropertyChanged(nameof(ShowDirtyComparisonWarning));

    partial void OnParameterSchemaJsonChanged(string value) => OnPropertyChanged(nameof(ShowDirtyComparisonWarning));

    partial void OnDefaultParametersJsonChanged(string value) => OnPropertyChanged(nameof(ShowDirtyComparisonWarning));

    partial void OnSelectedVersionChanged(PromptVersionDto? value)
    {

        if (value is not null)
        {

            TaskUtilities.FireAndForget(RefreshMirrorDiffAsync(CancellationToken.None));

        }

    }

    private void CapturePersistedSnapshot()
    {

        _persistedTemplate = Template;

        _persistedDescription = Description;

        _persistedVersion = Version;

        _persistedTagsText = TagsText;

        _persistedModel = Model;

        _persistedProvider = Provider;

        _persistedTemperatureText = TemperatureText;

        _persistedTopPText = TopPText;

        _persistedMaxOutputTokensText = MaxOutputTokensText;

        _persistedParameterSchemaJson = ParameterSchemaJson;

        _persistedDefaultParametersJson = DefaultParametersJson;

        OnPropertyChanged(nameof(ShowDirtyComparisonWarning));

        OnPropertyChanged(nameof(IsEditorDirty));

    }

    [RelayCommand]
    public async Task RefreshMirrorDiffAsync(CancellationToken cancellationToken)
    {

        if (_disposed)
        {

            return;

        }

        int generation = ++_mirrorRefreshGeneration;

        MirrorDiffLines.Clear();

        MirrorComparedPrompt = null;

        PromptVersionDto? version = SelectedVersion;

        if (version is null)
        {

            MirrorStatusText = "Select a version to compare.";

            return;

        }

        PromptDetailDto? detail;

        try
        {

            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeCts.Token);

            detail = await _dataSource
                .LoadPromptAsync(version.Id, linked.Token)
                .ConfigureAwait(true);

        }
        catch (OperationCanceledException)
        {

            return;

        }

        if (_disposed || generation != _mirrorRefreshGeneration)
        {

            // A newer selection (or a closed document) already owns the mirror surface.

            return;

        }

        if (detail is null)
        {

            LastError = "Mirror version detail unavailable — the server rejected the request.";

            MirrorStatusText = "Version detail unavailable.";

            _foundryFloor.AppendLine($"Scriptorium Mirror failed for version {version.Version} ({version.Id:D}).");

            return;

        }

        MirrorComparedPrompt = detail;

        string left = BuildMirrorSnapshot(
            _persistedTemplate,
            _persistedDescription,
            _persistedVersion,
            _persistedTagsText,
            _persistedModel,
            _persistedProvider,
            _persistedTemperatureText,
            _persistedTopPText,
            _persistedMaxOutputTokensText,
            _persistedParameterSchemaJson,
            _persistedDefaultParametersJson);

        string right = BuildMirrorSnapshot(
            detail.Template ?? string.Empty,
            detail.Description ?? string.Empty,
            detail.Version,
            string.Join(", ", detail.Tags),
            detail.Model ?? string.Empty,
            detail.Provider ?? string.Empty,
            detail.Temperature?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            detail.TopP?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            detail.MaxOutputTokens?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            detail.ParameterSchema?.RootElement.GetRawText() ?? "{}",
            detail.DefaultParameters?.RootElement.GetRawText() ?? "{}");

        // LCS over two large snapshots is bounded but still CPU-bound, and every caller is on the UI
        // thread. Compute it on the pool, then publish the whole diff in one notification.
        IReadOnlyList<DiffLineItem> lines = await Task
            .Run(() => LineDiff.Compare(left, right), cancellationToken)
            .ConfigureAwait(true);

        if (_disposed || generation != _mirrorRefreshGeneration)
        {

            return;

        }

        MirrorDiffLines.ResetTo(lines);

        MirrorStatusText =
            $"Compared persisted editor snapshot vs version {detail.Version} ({detail.Id:D}). "
            + "There is no activate-prompt — open or clone a version to work with it.";

    }

    private static string BuildMirrorSnapshot(
        string template,
        string description,
        string version,
        string tags,
        string model,
        string provider,
        string temperature,
        string topP,
        string maxOutputTokens,
        string parameterSchema,
        string defaultParameters)
    {

        StringBuilder sb = new();

        sb.AppendLine("## Metadata");

        sb.AppendLine($"version: {version}");

        sb.AppendLine($"description: {description}");

        sb.AppendLine($"tags: {tags}");

        sb.AppendLine($"model: {model}");

        sb.AppendLine($"provider: {provider}");

        sb.AppendLine($"temperature: {temperature}");

        sb.AppendLine($"topP: {topP}");

        sb.AppendLine($"maxOutputTokens: {maxOutputTokens}");

        sb.AppendLine();

        sb.AppendLine("## Template");

        sb.AppendLine(template);

        sb.AppendLine();

        sb.AppendLine("## ParameterSchema");

        sb.AppendLine(NormalizeJson(parameterSchema));

        sb.AppendLine();

        sb.AppendLine("## DefaultParameters");

        sb.AppendLine(NormalizeJson(defaultParameters));

        return sb.ToString();

    }

    private static string NormalizeJson(string json)
    {

        try
        {

            using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);

            return doc.RootElement.GetRawText();

        }
        catch (JsonException)
        {

            return json;

        }

    }

}
