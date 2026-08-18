using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Core.Models.DiagnosticMcp;
using RetroDownfall.TheForge.Core.Services;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.Services.Whispers;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;

namespace RetroDownfall.TheForge.Ux.ViewModels.Arsenal;

/// <summary>
/// Diagnostic MCP Invocation — policy-constrained direct invocation of <strong>external</strong> MCP
/// tools by an operator. Surfaces the Arcanum <c>POST /api/mcp/tools/invoke</c> route (external MCP
/// only; internal <c>arcanum-internal</c> server and all Forbidden Arts are blocked server-side).
/// Not model execution; not unauthenticated. Fixtures are saved locally only on explicit user choice.
/// </summary>
public sealed partial class DiagnosticMcpInvocationViewModel : ViewModelBase, IDisposable
{

    public const string InternalServerName = "arcanum-internal";

    public const string PolicyBanner =
        "Diagnostic MCP Invocation — policy-constrained. External MCP tools only. " +
        "The internal server and Forbidden Arts (execute_command, write_file, replace_text_block, " +
        "delete_lexicon, run_spell_script) are blocked and must be exercised through the Master tool " +
        "execution pipeline. Not model execution; not unauthenticated.";

    public const string SensitiveFixtureWarning =
        "Saved fixtures may contain tool arguments and outputs (potentially sensitive). " +
        "They are stored locally on this machine.";

    public const string MutationWarning =
        "Some MCP tools mutate state, write files, or call external services. Confirm before invoking.";

    private readonly IArsenalDataSource _dataSource;

    private readonly IDiagnosticMcpFixtureStore _fixtureStore;

    private readonly FoundryFloorViewModel _foundryFloor;

    private readonly IWhispersService _whispers;

    private readonly IConfirmationDialogService _confirmationDialog;

    private readonly IArtifactFileDialogService _fileDialog;

    private readonly ITextInputDialogService _textInputDialog;

    private bool _disposed;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _lastError;

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    private string _workingDirectory = string.Empty;

    [ObservableProperty]
    private McpServerEntryViewModel? _selectedServer;

    [ObservableProperty]
    private string? _selectedTool;

    [ObservableProperty]
    private string _argumentsText = "{}";

    [ObservableProperty]
    private string _resultText = string.Empty;

    [ObservableProperty]
    private long _lastDurationMs;

    [ObservableProperty]
    private bool _lastTruncated;

    [ObservableProperty]
    private string? _resolvedServerName;

    [ObservableProperty]
    private DiagnosticMcpFixtureRecord? _selectedFixture;

    public DiagnosticMcpInvocationViewModel(
        IArsenalDataSource dataSource,
        IDiagnosticMcpFixtureStore fixtureStore,
        FoundryFloorViewModel foundryFloor,
        IWhispersService whispers,
        IConfirmationDialogService confirmationDialog,
        IArtifactFileDialogService fileDialog,
        ITextInputDialogService textInputDialog)
    {

        _dataSource = dataSource;

        _fixtureStore = fixtureStore;

        _foundryFloor = foundryFloor;

        _whispers = whispers;

        _confirmationDialog = confirmationDialog;

        _fileDialog = fileDialog;

        _textInputDialog = textInputDialog;

        Title = "Diagnostic MCP Invocation";

    }

    public ObservableCollection<McpServerEntryViewModel> Servers { get; } = [];

    public ObservableCollection<string> Tools { get; } = [];

    public ObservableCollection<DiagnosticMcpFixtureRecord> Fixtures { get; } = [];

    public string PolicyText => PolicyBanner;

    public string SensitiveWarning => SensitiveFixtureWarning;

    [RelayCommand]
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {

        IsBusy = true;

        LastError = null;

        try
        {

            (WorkspaceArsenalDto? arsenal, string? error) = await _dataSource
                .GetArsenalAsync(string.IsNullOrWhiteSpace(WorkingDirectory) ? null : WorkingDirectory, cancellationToken)
                .ConfigureAwait(true);

            if (error is not null)
            {

                LastError = error;

                StatusText = "Failed to load MCP servers.";

                _foundryFloor.AppendLine($"Diagnostic MCP refresh error: {error}");

                return;

            }

            Servers.Clear();

            Tools.Clear();

            SelectedServer = null;

            SelectedTool = null;

            if (arsenal is { McpServers: { } servers })
            {

                foreach (McpServerStatusDto server in servers)
                {

                    if (string.Equals(server.ServerName, InternalServerName, StringComparison.Ordinal))
                    {

                        // Internal server is always blocked from diagnostic invocation; do not surface it.
                        continue;

                    }

                    if (!string.Equals(server.Status, "running", StringComparison.OrdinalIgnoreCase))
                    {

                        continue;

                    }

                    Servers.Add(new McpServerEntryViewModel(server.ServerName, server.Status, server.ProvidedTools));

                }

            }

            StatusText = Servers.Count == 0 ? "No running external MCP servers." : $"{Servers.Count} running external server(s).";

        }
        catch (Exception ex)
        {

            LastError = ex.Message;

            StatusText = "Failed to load MCP servers.";

            _foundryFloor.AppendLine($"Diagnostic MCP refresh error: {ex.Message}");

        }
        finally
        {

            IsBusy = false;

        }

    }

    partial void OnSelectedServerChanged(McpServerEntryViewModel? value)
    {

        Tools.Clear();

        SelectedTool = null;

        if (value is not null)
        {

            foreach (string tool in value.ProvidedTools)
            {

                Tools.Add(tool);

            }

        }

    }

    [RelayCommand(CanExecute = nameof(CanInvoke))]
    public async Task InvokeAsync(CancellationToken cancellationToken)
    {

        if (SelectedServer is null || string.IsNullOrWhiteSpace(SelectedTool))
        {

            StatusText = "Select a server and a tool first.";

            return;

        }

        JsonElement arguments;

        try
        {

            using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(ArgumentsText) ? "{}" : ArgumentsText);

            arguments = doc.RootElement.Clone();

        }
        catch (JsonException ex)
        {

            LastError = $"Invalid arguments JSON: {ex.Message}";

            return;

        }

        bool confirmed = await _confirmationDialog
            .ConfirmAsync("Invoke diagnostic MCP tool?", MutationWarning, cancellationToken, confirmIsDefault: false)
            .ConfigureAwait(true);

        if (!confirmed)
        {

            StatusText = "Invocation cancelled.";

            return;

        }

        IsBusy = true;

        LastError = null;

        ResultText = string.Empty;

        LastDurationMs = 0;

        LastTruncated = false;

        ResolvedServerName = null;

        try
        {

            McpToolInvokeRequest request = new()
            {
                ToolName = SelectedTool,
                Arguments = arguments,
                ServerName = SelectedServer.Name,
                WorkingDirectory = string.IsNullOrWhiteSpace(WorkingDirectory) ? null : WorkingDirectory,
            };

            (McpToolInvokeResponse? response, string? error) = await _dataSource
                .InvokeDiagnosticMcpAsync(request, cancellationToken)
                .ConfigureAwait(true);

            if (response is null)
            {

                LastError = error ?? "Diagnostic MCP invocation failed.";

                ResultText = LastError;

                StatusText = "Invocation failed.";

                _foundryFloor.AppendLine($"Diagnostic MCP invoke failed: {SelectedTool} on {SelectedServer.Name} — {LastError}");

                _whispers.Show(WhisperSeverity.Error, "Diagnostic invoke failed.");

                return;

            }

            ResultText = response.Result.GetRawText();

            LastDurationMs = response.DurationMs;

            LastTruncated = response.Truncated;

            ResolvedServerName = response.ServerName;

            StatusText = LastTruncated
                ? $"Invocation complete in {response.DurationMs}ms (output truncated)."
                : $"Invocation complete in {response.DurationMs}ms.";

            _foundryFloor.AppendLine($"Diagnostic MCP invoked: {SelectedTool} on {SelectedServer.Name} ({response.DurationMs}ms{(LastTruncated ? ", truncated" : string.Empty)}).");

            _whispers.Show(WhisperSeverity.Success, "Diagnostic invoke complete.");

        }
        catch (Exception ex)
        {

            LastError = ex.Message;

            StatusText = "Invocation failed.";

            _foundryFloor.AppendLine($"Diagnostic MCP invoke error: {ex.Message}");

        }
        finally
        {

            IsBusy = false;

        }

    }

    private bool CanInvoke() => !IsBusy && SelectedServer is not null && !string.IsNullOrWhiteSpace(SelectedTool);

    partial void OnIsBusyChanged(bool value) => InvokeCommand.NotifyCanExecuteChanged();

    partial void OnSelectedToolChanged(string? value) => InvokeCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    public async Task LoadFixturesAsync(CancellationToken cancellationToken)
    {

        DiagnosticMcpFixtureStoreDocument document = await _fixtureStore.LoadAsync(cancellationToken).ConfigureAwait(true);

        Fixtures.Clear();

        foreach (DiagnosticMcpFixtureRecord fixture in document.Fixtures)
        {

            Fixtures.Add(fixture);

        }

        StatusText = Fixtures.Count == 0 ? "No saved fixtures." : $"Loaded {Fixtures.Count} fixture(s).";

    }

    [RelayCommand]
    public async Task SaveFixtureAsync(CancellationToken cancellationToken)
    {

        if (string.IsNullOrWhiteSpace(SelectedTool))
        {

            StatusText = "Select a tool before saving a fixture.";

            return;

        }

        string? name = await _textInputDialog
            .PromptAsync("Save fixture", "Fixture name", $"{SelectedTool}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}", cancellationToken)
            .ConfigureAwait(true);

        if (string.IsNullOrWhiteSpace(name))
        {

            return;

        }

        DiagnosticMcpFixtureStoreDocument document = await _fixtureStore.LoadAsync(cancellationToken).ConfigureAwait(true);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        DiagnosticMcpFixtureRecord fixture = new(
            Guid.NewGuid(),
            name.Trim(),
            now,
            now,
            SelectedTool,
            SelectedServer?.Name,
            string.IsNullOrWhiteSpace(WorkingDirectory) ? null : WorkingDirectory,
            string.IsNullOrWhiteSpace(ArgumentsText) ? "{}" : ArgumentsText,
            string.IsNullOrWhiteSpace(ResultText) ? null : ResultText,
            ResultText.Length > 0 ? now : null);

        List<DiagnosticMcpFixtureRecord> fixtures = [fixture, .. document.Fixtures];

        await _fixtureStore
            .SaveAsync(new DiagnosticMcpFixtureStoreDocument(DiagnosticMcpFixtureStore.CurrentSchemaVersion, document.CreatedAt, now, fixtures), cancellationToken)
            .ConfigureAwait(true);

        await LoadFixturesAsync(cancellationToken).ConfigureAwait(true);

        _whispers.Show(WhisperSeverity.Success, "Fixture saved.");

    }

    [RelayCommand]
    public async Task DeleteFixtureAsync(CancellationToken cancellationToken)
    {

        if (SelectedFixture is null)
        {

            return;

        }

        bool confirmed = await _confirmationDialog
            .ConfirmAsync("Delete fixture?", $"Delete local fixture '{SelectedFixture.Name}'?", cancellationToken, confirmIsDefault: false)
            .ConfigureAwait(true);

        if (!confirmed)
        {

            return;

        }

        DiagnosticMcpFixtureStoreDocument document = await _fixtureStore.LoadAsync(cancellationToken).ConfigureAwait(true);

        List<DiagnosticMcpFixtureRecord> fixtures = document.Fixtures
            .Where(static f => true)
            .Where(f => f.Id != SelectedFixture.Id)
            .ToList();

        DateTimeOffset now = DateTimeOffset.UtcNow;

        await _fixtureStore
            .SaveAsync(new DiagnosticMcpFixtureStoreDocument(DiagnosticMcpFixtureStore.CurrentSchemaVersion, document.CreatedAt, now, fixtures), cancellationToken)
            .ConfigureAwait(true);

        await LoadFixturesAsync(cancellationToken).ConfigureAwait(true);

        _whispers.Show(WhisperSeverity.Info, "Fixture deleted.");

    }

    [RelayCommand]
    public void LoadFixtureIntoEditor()
    {

        if (SelectedFixture is null)
        {

            return;

        }

        SelectedTool = SelectedFixture.ToolName;

        ArgumentsText = SelectedFixture.ArgumentsJson;

        if (!string.IsNullOrWhiteSpace(SelectedFixture.WorkingDirectory))
        {

            WorkingDirectory = SelectedFixture.WorkingDirectory;

        }

        McpServerEntryViewModel? server = Servers.FirstOrDefault(s => string.Equals(s.Name, SelectedFixture.ServerName, StringComparison.Ordinal));

        if (server is not null)
        {

            SelectedServer = server;

        }

        StatusText = $"Loaded fixture '{SelectedFixture.Name}'.";

    }

    [RelayCommand]
    public async Task ExportResultAsync(CancellationToken cancellationToken)
    {

        if (string.IsNullOrWhiteSpace(ResultText))
        {

            StatusText = "Nothing to export.";

            return;

        }

        string? path = await _fileDialog
            .PickSaveJsonPathAsync($"diagnostic-mcp-{SelectedTool ?? "result"}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.json", cancellationToken)
            .ConfigureAwait(true);

        if (string.IsNullOrWhiteSpace(path))
        {

            return;

        }

        try
        {

            await File.WriteAllTextAsync(path, ResultText, System.Text.Encoding.UTF8, cancellationToken).ConfigureAwait(true);

        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {

            // Unguarded this escapes onto the dispatcher from a fire-and-forget command and takes the
            // window with it, for something as ordinary as a read-only export directory.
            LastError = ex.Message;

            StatusText = "Result export failed.";

            _foundryFloor.AppendLine($"Diagnostic MCP export error: {ex.Message}");

            _whispers.Show(WhisperSeverity.Error, "Result export failed.");

            return;

        }

        StatusText = "Result exported.";

    }

    [RelayCommand]
    public async Task ClearFixturesAsync(CancellationToken cancellationToken)
    {

        bool confirmed = await _confirmationDialog
            .ConfirmAsync("Clear all fixtures?", SensitiveFixtureWarning, cancellationToken, confirmIsDefault: false)
            .ConfigureAwait(true);

        if (!confirmed)
        {

            return;

        }

        DateTimeOffset now = DateTimeOffset.UtcNow;

        await _fixtureStore
            .SaveAsync(new DiagnosticMcpFixtureStoreDocument(DiagnosticMcpFixtureStore.CurrentSchemaVersion, now, now, []), cancellationToken)
            .ConfigureAwait(true);

        Fixtures.Clear();

        StatusText = "Fixtures cleared.";

    }

    public void Dispose()
    {

        if (_disposed)
        {

            return;

        }

        _disposed = true;

    }

}

/// <summary>One visible external MCP server in the Diagnostic MCP Invocation picker.</summary>
public sealed record McpServerEntryViewModel(string Name, string Status, IReadOnlyList<string> ProvidedTools)
{

    public string ToolSummary => ProvidedTools.Count == 0 ? "no tools" : $"{ProvidedTools.Count} tool(s)";

}
