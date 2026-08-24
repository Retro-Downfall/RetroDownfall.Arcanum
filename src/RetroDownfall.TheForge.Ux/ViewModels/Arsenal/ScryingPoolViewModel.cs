using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;

namespace RetroDownfall.TheForge.Ux.ViewModels.Arsenal;

/// <summary>
/// The Scrying Pool tab of The Arsenal: lists built-in (native) tools exposed by
/// <c>POST /api/intelligence/arsenal</c> and invokes them via <c>POST /api/tools/invoke</c>.
/// External MCP direct invocation lives in the Diagnostic MCP Invocation tab.
/// </summary>
public sealed partial class ScryingPoolViewModel : ViewModelBase
{

    private readonly IArsenalDataSource _dataSource;

    private readonly FoundryFloorViewModel _foundryFloor;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _lastError;

    [ObservableProperty]
    private string? _statusText;

    public ObservableCollection<string> NativeTools { get; } = [];

    [ObservableProperty]
    private string? _selectedTool;

    [ObservableProperty]
    private string _argumentsText = "{}";

    [ObservableProperty]
    private string _resultText = string.Empty;

    public ScryingPoolViewModel(IArsenalDataSource dataSource, FoundryFloorViewModel foundryFloor)
    {

        _dataSource = dataSource;

        _foundryFloor = foundryFloor;

        Title = "Scrying Pool";

    }

    public string InvocationNote => "Built-in Tool Invocation — for external MCP tools use the Diagnostic MCP Invocation tab.";

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {

        IsBusy = true;

        LastError = null;

        try
        {

            (WorkspaceArsenalDto? arsenal, string? error) = await _dataSource
                .GetArsenalAsync(null, cancellationToken)
                .ConfigureAwait(true);

            if (error is not null)
            {

                LastError = error;

                StatusText = "Failed to load built-in tools.";

                _foundryFloor.AppendLine($"Scrying Pool refresh error: {error}");

                return;

            }

            NativeTools.Clear();

            if (arsenal is { NativeTools: { } nativeTools })
            {

                foreach (string tool in nativeTools)
                {

                    NativeTools.Add(tool);

                }

            }

            StatusText = NativeTools.Count == 0 ? "No built-in tools reported." : $"{NativeTools.Count} built-in tool(s).";

        }
        catch (Exception ex)
        {

            LastError = ex.Message;

            StatusText = "Failed to load built-in tools.";

            _foundryFloor.AppendLine($"Scrying Pool refresh error: {ex.Message}");

        }
        finally
        {

            IsBusy = false;

        }

    }

    private bool CanRefresh() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanInvoke))]
    public async Task InvokeAsync(CancellationToken cancellationToken)
    {

        if (string.IsNullOrWhiteSpace(SelectedTool))
        {

            StatusText = "Select a built-in tool first.";

            return;

        }

        IsBusy = true;

        LastError = null;

        ResultText = string.Empty;

        try
        {

            using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(ArgumentsText) ? "{}" : ArgumentsText);

            ToolInvokeRequest request = new(SelectedTool, doc.RootElement.Clone());

            (ToolInvokeResponse? response, string? error) = await _dataSource
                .InvokeToolAsync(request, cancellationToken)
                .ConfigureAwait(true);

            if (response is not null && response.Result.ValueKind != JsonValueKind.Undefined)
            {

                ResultText = response.Result.GetRawText();

                StatusText = "Invocation complete.";

            }
            else
            {

                string detail = error ?? "Tool invocation failed.";

                ResultText = detail;

                StatusText = "Invocation failed.";

                LastError = detail;

                _foundryFloor.AppendLine($"Scrying Pool invoke failed: {SelectedTool} — {detail}");

            }

        }
        catch (JsonException ex)
        {

            LastError = $"Invalid arguments JSON: {ex.Message}";

        }
        catch (Exception ex)
        {

            LastError = ex.Message;

            _foundryFloor.AppendLine($"Scrying Pool invoke error: {ex.Message}");

        }
        finally
        {

            IsBusy = false;

        }

    }

    private bool CanInvoke() => !IsBusy && !string.IsNullOrWhiteSpace(SelectedTool);

    partial void OnIsBusyChanged(bool value)
    {

        RefreshCommand.NotifyCanExecuteChanged();

        InvokeCommand.NotifyCanExecuteChanged();

    }

    partial void OnSelectedToolChanged(string? value)
    {

        InvokeCommand.NotifyCanExecuteChanged();

    }

}
