using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.Wards;

namespace RetroDownfall.TheForge.Ux.ViewModels.Gatehouse;

/// <summary>One pending ward card in The Gatehouse with approve/deny and an ExpiresAt countdown.</summary>
public sealed partial class WardCardViewModel : ObservableObject
{

    private readonly Func<WardCardViewModel, bool, string?, CancellationToken, Task> _resolve;

    [ObservableProperty]
    private string _denyReason = string.Empty;

    [ObservableProperty]
    private string _countdownText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    public WardCardViewModel(WardDto ward, Func<WardCardViewModel, bool, string?, CancellationToken, Task> resolve)
    {

        Ward = ward;

        _resolve = resolve;

        RefreshCountdown(DateTimeOffset.UtcNow);

    }

    public WardDto Ward { get; private set; }

    public string WardId => Ward.WardId;

    public string ToolName => Ward.ToolName;

    public string? SessionId => Ward.SessionId;

    public DateTimeOffset ExpiresAt => Ward.ExpiresAt;

    public string ArgumentsSummary => FormatArguments(Ward.Arguments);

    /// <summary>Replaces the underlying DTO and refreshes bound display properties.</summary>
    public void Update(WardDto ward)
    {

        Ward = ward;

        OnPropertyChanged(nameof(Ward));

        OnPropertyChanged(nameof(ToolName));

        OnPropertyChanged(nameof(SessionId));

        OnPropertyChanged(nameof(ExpiresAt));

        OnPropertyChanged(nameof(ArgumentsSummary));

        RefreshCountdown(DateTimeOffset.UtcNow);

    }

    public void RefreshCountdown(DateTimeOffset utcNow)
    {

        TimeSpan remaining = ExpiresAt - utcNow;

        if (remaining <= TimeSpan.Zero)
        {

            CountdownText = "expired";

            return;

        }

        CountdownText = remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours}h {remaining.Minutes:D2}m"
            : $"{remaining.Minutes:D2}m {remaining.Seconds:D2}s";

    }

    [RelayCommand]
    private Task ApproveAsync(CancellationToken cancellationToken) =>
        ResolveInternalAsync(allow: true, reason: null, cancellationToken);

    [RelayCommand]
    private Task DenyAsync(CancellationToken cancellationToken) =>
        ResolveInternalAsync(allow: false, string.IsNullOrWhiteSpace(DenyReason) ? null : DenyReason.Trim(), cancellationToken);

    private async Task ResolveInternalAsync(bool allow, string? reason, CancellationToken cancellationToken)
    {

        if (IsBusy)
        {

            return;

        }

        IsBusy = true;

        try
        {

            await _resolve(this, allow, reason, cancellationToken).ConfigureAwait(true);

        }
        finally
        {

            IsBusy = false;

        }

    }

    private static string FormatArguments(JsonElement? arguments)
    {

        if (arguments is null || arguments.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {

            return "(no arguments)";

        }

        string indented;

        try
        {

            indented = JsonSerializer.Serialize(arguments.Value, new JsonSerializerOptions { WriteIndented = true });

        }
        catch
        {

            indented = arguments.Value.ToString();

        }

        const int maxLength = 400;

        return indented.Length <= maxLength
            ? indented
            : indented[..maxLength] + "…";

    }

}
