using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.TheForge.Core.Services;
using RetroDownfall.TheForge.Core.Serialization;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.Services.Whispers;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;

namespace RetroDownfall.TheForge.Ux.ViewModels.AuditBrowser;

/// <summary>
/// Audit Browser (Phase 8) — a tabbed dock tool over <c>GET /api/audit</c> and
/// <c>GET /api/guardrails/audit</c> (ApiResponse envelopes). Empty results surface the honest
/// "disabled or no records" empty states (the server returns [] when logging is off — not an
/// error). Guardrails <see cref="GuardrailAuditRecord.MatchedTextRedacted"/> is always the
/// redacted form; the UI never implies raw PII is available. Session ids open The Tome. CSV/JSON
/// export via the artifact file dialog. Nothing throws on API failure.
/// </summary>
public sealed partial class AuditBrowserViewModel : ViewModelBase
{

    public const int DefaultLimit = 100;

    public const string InferenceEmptyMessageText =
        "Inference audit logging is disabled or no records are available.";

    public const string GuardrailsEmptyMessageText =
        "Guardrails audit logging is disabled or no records are available.";

    public const string GuardrailsRedactionNoteText =
        "Matched text is server-redacted. The Forge never receives raw PII from the guardrails audit log.";

    public string InferenceEmptyMessage => InferenceEmptyMessageText;

    public string GuardrailsEmptyMessage => GuardrailsEmptyMessageText;

    public string GuardrailsRedactionNote => GuardrailsRedactionNoteText;

    private readonly IAuditBrowserDataSource _dataSource;

    private readonly INavigationService _navigation;

    private readonly FoundryFloorViewModel _foundryFloor;

    private readonly IArtifactFileDialogService _fileDialog;

    private readonly IClipboardService _clipboard;

    private readonly IWhispersService _whispers;

    private readonly ITheForgeLocalMutationRunner _mutationRunner;

    private bool _loaded;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _lastError;

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private int _activeTabIndex;

    // Inference filters
    [ObservableProperty]
    private string _inferenceFromText = string.Empty;

    [ObservableProperty]
    private string _inferenceToText = string.Empty;

    [ObservableProperty]
    private string _inferenceModel = string.Empty;

    [ObservableProperty]
    private string _inferenceSessionId = string.Empty;

    [ObservableProperty]
    private InferenceAuditRecord? _selectedInferenceRecord;

    // Guardrails filters
    [ObservableProperty]
    private string _guardrailsFromText = string.Empty;

    [ObservableProperty]
    private string _guardrailsToText = string.Empty;

    [ObservableProperty]
    private string _guardrailsStage = string.Empty;

    [ObservableProperty]
    private string _guardrailsViolationType = string.Empty;

    [ObservableProperty]
    private string _guardrailsSessionId = string.Empty;

    [ObservableProperty]
    private GuardrailAuditRecord? _selectedGuardrailRecord;

    public AuditBrowserViewModel(
        IAuditBrowserDataSource dataSource,
        INavigationService navigation,
        FoundryFloorViewModel foundryFloor,
        IArtifactFileDialogService fileDialog,
        IClipboardService clipboard,
        IWhispersService whispers,
        ITheForgeLocalMutationRunner mutationRunner)
    {

        _dataSource = dataSource;

        _navigation = navigation;

        _foundryFloor = foundryFloor;

        _fileDialog = fileDialog;

        _clipboard = clipboard;

        _whispers = whispers;

        _mutationRunner = mutationRunner;

        Title = "Audit Browser";

        StatusText = "Audit Browser ready.";

    }

    public ObservableCollection<InferenceAuditRecord> InferenceRecords { get; } = [];

    public ObservableCollection<GuardrailAuditRecord> GuardrailRecords { get; } = [];

    public bool HasNoInferenceRecords => InferenceRecords.Count == 0;

    public bool HasNoGuardrailRecords => GuardrailRecords.Count == 0;

    public string InferenceAuditPathsMessage =>
        DisabledSettingPaths.FormatEnableMessage("Inference audit logging", DisabledSettingPaths.InferenceAudit);

    public string GuardrailsAuditPathsMessage =>
        DisabledSettingPaths.FormatEnableMessage("Guardrails audit logging", DisabledSettingPaths.GuardrailsAudit);

    partial void OnIsVisibleChanged(bool value)
    {

        if (value && !_loaded)
        {

            _loaded = true;

            _ = RefreshInferenceAsync(CancellationToken.None);

        }

    }

    [RelayCommand]
    public async Task RefreshInferenceAsync(CancellationToken cancellationToken)
    {

        IsBusy = true;

        LastError = null;

        try
        {

            if (!TryParseOptionalDate(InferenceFromText, "from", out DateTimeOffset? from, out string? parseError)
                || !TryParseOptionalDate(InferenceToText, "to", out DateTimeOffset? to, out parseError))
            {

                LastError = parseError;

                StatusText = "Invalid filter.";

                return;

            }

            string? model = NullIfWhiteSpace(InferenceModel);

            string? sessionId = NullIfWhiteSpace(InferenceSessionId);

            DataSourceResult<InferenceAuditRecord[]> result = await _dataSource
                .QueryInferenceAsync(from, to, model, sessionId, DefaultLimit, cancellationToken)
                .ConfigureAwait(true);

            InferenceRecords.Clear();

            SelectedInferenceRecord = null;

            if (result.Success && result.Data is { } records)
            {

                foreach (InferenceAuditRecord record in records)
                {

                    InferenceRecords.Add(record);

                }

                StatusText = InferenceRecords.Count == 0
                    ? InferenceEmptyMessageText
                    : $"{InferenceRecords.Count} inference audit record(s).";

            }
            else
            {

                LastError = result.ErrorMessage ?? "Failed to load inference audit.";

                StatusText = "Inference audit unavailable.";

                _foundryFloor.AppendLine($"Audit Browser inference query failed: {LastError}");

            }

            OnPropertyChanged(nameof(HasNoInferenceRecords));

        }
        catch (Exception ex)
        {

            LastError = ex.Message;

            _foundryFloor.AppendLine($"Audit Browser inference error: {ex.Message}");

        }
        finally
        {

            IsBusy = false;

        }

    }

    [RelayCommand]
    public async Task RefreshGuardrailsAsync(CancellationToken cancellationToken)
    {

        IsBusy = true;

        LastError = null;

        try
        {

            if (!TryParseOptionalDate(GuardrailsFromText, "from", out DateTimeOffset? from, out string? parseError)
                || !TryParseOptionalDate(GuardrailsToText, "to", out DateTimeOffset? to, out parseError))
            {

                LastError = parseError;

                StatusText = "Invalid filter.";

                return;

            }

            string? stage = NullIfWhiteSpace(GuardrailsStage);

            string? violationType = NullIfWhiteSpace(GuardrailsViolationType);

            string? sessionId = NullIfWhiteSpace(GuardrailsSessionId);

            DataSourceResult<GuardrailAuditRecord[]> result = await _dataSource
                .QueryGuardrailsAsync(from, to, stage, violationType, sessionId, DefaultLimit, cancellationToken)
                .ConfigureAwait(true);

            GuardrailRecords.Clear();

            SelectedGuardrailRecord = null;

            if (result.Success && result.Data is { } records)
            {

                foreach (GuardrailAuditRecord record in records)
                {

                    GuardrailRecords.Add(record);

                }

                StatusText = GuardrailRecords.Count == 0
                    ? GuardrailsEmptyMessageText
                    : $"{GuardrailRecords.Count} guardrails audit record(s).";

            }
            else
            {

                LastError = result.ErrorMessage ?? "Failed to load guardrails audit.";

                StatusText = "Guardrails audit unavailable.";

                _foundryFloor.AppendLine($"Audit Browser guardrails query failed: {LastError}");

            }

            OnPropertyChanged(nameof(HasNoGuardrailRecords));

        }
        catch (Exception ex)
        {

            LastError = ex.Message;

            _foundryFloor.AppendLine($"Audit Browser guardrails error: {ex.Message}");

        }
        finally
        {

            IsBusy = false;

        }

    }

    [RelayCommand]
    private void OpenInferenceSession(InferenceAuditRecord? record)
    {

        if (record?.SessionId is { Length: > 0 } sessionId && Guid.TryParse(sessionId, out Guid id))
        {

            _navigation.OpenDocument(DocumentKind.Session, id.ToString("D"));

        }

    }

    [RelayCommand]
    private void OpenGuardrailSession(GuardrailAuditRecord? record)
    {

        if (record?.SessionId is { Length: > 0 } sessionId && Guid.TryParse(sessionId, out Guid id))
        {

            _navigation.OpenDocument(DocumentKind.Session, id.ToString("D"));

        }

    }

    [RelayCommand]
    public async Task ExportInferenceJsonAsync(CancellationToken cancellationToken)
    {

        string? path = await _fileDialog
            .PickSaveJsonPathAsync($"inference-audit-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.json", cancellationToken)
            .ConfigureAwait(true);

        if (path is null)
        {

            return;

        }

        try
        {

            int exportedCount = 0;

            await WriteExportAsync(
                    path,
                    () =>
                    {

                        InferenceAuditRecord[] payload = [.. InferenceRecords];

                        exportedCount = payload.Length;

                        return JsonSerializer.Serialize(
                            payload,
                            TheForgeJsonContext.Default.InferenceAuditRecordArray);

                    },
                    cancellationToken)
                .ConfigureAwait(true);

            StatusText = $"Exported {exportedCount} inference record(s) to JSON.";

            _whispers.Show(WhisperSeverity.Success, "Inference audit exported.");

        }
        catch (Exception ex)
        {

            LastError = ex.Message;

            _whispers.Show(WhisperSeverity.Error, "Export failed.");

        }

    }

    [RelayCommand]
    public async Task ExportInferenceCsvAsync(CancellationToken cancellationToken)
    {

        string? path = await _fileDialog
            .PickSaveCsvPathAsync($"inference-audit-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.csv", cancellationToken)
            .ConfigureAwait(true);

        if (path is null)
        {

            return;

        }

        try
        {

            int exportedCount = 0;

            await WriteExportAsync(
                    path,
                    () =>
                    {

                        StringBuilder csv = new();

                        csv.AppendLine("timestamp,sessionId,requestType,model,provider,promptTokens,completionTokens,totalTokens,latencyMs,toolCalls,finishReason,spellName,campaignId");

                        foreach (InferenceAuditRecord r in InferenceRecords)
                        {

                            exportedCount++;

                            csv.Append(EscapeCsv(r.Timestamp)).Append(',');

                            csv.Append(EscapeCsv(r.SessionId)).Append(',');

                            csv.Append(EscapeCsv(r.RequestType)).Append(',');

                            csv.Append(EscapeCsv(r.Model)).Append(',');

                            csv.Append(EscapeCsv(r.Provider)).Append(',');

                            csv.Append(r.PromptTokens.ToString(CultureInfo.InvariantCulture)).Append(',');

                            csv.Append(r.CompletionTokens.ToString(CultureInfo.InvariantCulture)).Append(',');

                            csv.Append(r.TotalTokens.ToString(CultureInfo.InvariantCulture)).Append(',');

                            csv.Append(r.LatencyMs.ToString(CultureInfo.InvariantCulture)).Append(',');

                            csv.Append(r.ToolCalls.ToString(CultureInfo.InvariantCulture)).Append(',');

                            csv.Append(EscapeCsv(r.FinishReason)).Append(',');

                            csv.Append(EscapeCsv(r.SpellName)).Append(',');

                            csv.AppendLine(EscapeCsv(r.CampaignId));

                        }

                        return csv.ToString();

                    },
                    cancellationToken)
                .ConfigureAwait(true);

            StatusText = $"Exported {exportedCount} inference record(s) to CSV.";

            _whispers.Show(WhisperSeverity.Success, "Inference audit exported.");

        }
        catch (Exception ex)
        {

            LastError = ex.Message;

            _whispers.Show(WhisperSeverity.Error, "Export failed.");

        }

    }

    [RelayCommand]
    public async Task ExportGuardrailsJsonAsync(CancellationToken cancellationToken)
    {

        string? path = await _fileDialog
            .PickSaveJsonPathAsync($"guardrails-audit-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.json", cancellationToken)
            .ConfigureAwait(true);

        if (path is null)
        {

            return;

        }

        try
        {

            int exportedCount = 0;

            await WriteExportAsync(
                    path,
                    () =>
                    {

                        GuardrailAuditRecord[] payload = [.. GuardrailRecords];

                        exportedCount = payload.Length;

                        return JsonSerializer.Serialize(
                            payload,
                            TheForgeJsonContext.Default.GuardrailAuditRecordArray);

                    },
                    cancellationToken)
                .ConfigureAwait(true);

            StatusText = $"Exported {exportedCount} guardrails record(s) to JSON.";

            _whispers.Show(WhisperSeverity.Success, "Guardrails audit exported.");

        }
        catch (Exception ex)
        {

            LastError = ex.Message;

            _whispers.Show(WhisperSeverity.Error, "Export failed.");

        }

    }

    [RelayCommand]
    public async Task ExportGuardrailsCsvAsync(CancellationToken cancellationToken)
    {

        string? path = await _fileDialog
            .PickSaveCsvPathAsync($"guardrails-audit-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.csv", cancellationToken)
            .ConfigureAwait(true);

        if (path is null)
        {

            return;

        }

        try
        {

            int exportedCount = 0;

            await WriteExportAsync(
                    path,
                    () =>
                    {

                        StringBuilder csv = new();

                        csv.AppendLine("timestamp,sessionId,stage,violationType,matchedTextRedacted,model");

                        foreach (GuardrailAuditRecord r in GuardrailRecords)
                        {

                            exportedCount++;

                            csv.Append(EscapeCsv(r.Timestamp)).Append(',');

                            csv.Append(EscapeCsv(r.SessionId)).Append(',');

                            csv.Append(EscapeCsv(r.Stage)).Append(',');

                            csv.Append(EscapeCsv(r.ViolationType)).Append(',');

                            csv.Append(EscapeCsv(r.MatchedTextRedacted)).Append(',');

                            csv.AppendLine(EscapeCsv(r.Model));

                        }

                        return csv.ToString();

                    },
                    cancellationToken)
                .ConfigureAwait(true);

            StatusText = $"Exported {exportedCount} guardrails record(s) to CSV.";

            _whispers.Show(WhisperSeverity.Success, "Guardrails audit exported.");

        }
        catch (Exception ex)
        {

            LastError = ex.Message;

            _whispers.Show(WhisperSeverity.Error, "Export failed.");

        }

    }

    private Task WriteExportAsync(
        string path,
        string contents,
        CancellationToken cancellationToken) =>
        WriteExportAsync(path, () => contents, cancellationToken);

    private Task WriteExportAsync(
        string path,
        Func<string> contentsFactory,
        CancellationToken cancellationToken) =>
        _mutationRunner.RunAsync(
            path,
            admittedCancellationToken => File.WriteAllTextAsync(
                path,
                contentsFactory(),
                admittedCancellationToken),
            cancellationToken);

    [RelayCommand]
    private async Task CopyDisabledPathsAsync(string? surface, CancellationToken cancellationToken)
    {

        string[] paths = surface switch
        {

            "InferenceAudit" => DisabledSettingPaths.InferenceAudit,

            "GuardrailsAudit" => DisabledSettingPaths.GuardrailsAudit,

            _ => [],

        };

        if (paths.Length == 0)
        {

            return;

        }

        await _clipboard.SetTextAsync(DisabledSettingPaths.JoinForClipboard(paths), cancellationToken).ConfigureAwait(true);

    }

    private static bool TryParseOptionalDate(string text, string label, out DateTimeOffset? value, out string? error)
    {

        value = null;

        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {

            return true;

        }

        if (DateTimeOffset.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed))
        {

            value = parsed;

            return true;

        }

        error = $"'{label}' is not a valid date/time.";

        return false;

    }

    private static string? NullIfWhiteSpace(string text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string EscapeCsv(string? value)
    {

        if (string.IsNullOrEmpty(value))
        {

            return string.Empty;

        }

        if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
        {

            return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

        }

        return value;

    }

}
