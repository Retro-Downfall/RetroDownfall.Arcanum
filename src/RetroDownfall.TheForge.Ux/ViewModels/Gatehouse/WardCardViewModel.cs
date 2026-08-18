using System.Buffers;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.Wards;

namespace RetroDownfall.TheForge.Ux.ViewModels.Gatehouse;

/// <summary>One pending ward card in The Gatehouse with approve/deny and an ExpiresAt countdown.</summary>
public sealed partial class WardCardViewModel : ObservableObject
{

    private const int MaxArgumentsSummaryLength = 400;

    private static readonly JsonWriterOptions IndentedWriterOptions = new() { Indented = true };

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

        ArgumentsSummary = FormatArguments(ward.Arguments);

        RefreshCountdown(DateTimeOffset.UtcNow);

    }

    public WardDto Ward { get; private set; }

    public string WardId => Ward.WardId;

    public string ToolName => Ward.ToolName;

    public string? SessionId => Ward.SessionId;

    public DateTimeOffset ExpiresAt => Ward.ExpiresAt;

    /// <summary>Formatted once per DTO; the Gatehouse re-polls every two seconds and the view re-reads on every notification.</summary>
    public string ArgumentsSummary { get; private set; }

    /// <summary>Replaces the underlying DTO and refreshes bound display properties.</summary>
    public void Update(WardDto ward)
    {

        Ward = ward;

        ArgumentsSummary = FormatArguments(ward.Arguments);

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

            ArrayBufferWriter<byte> buffer = new();

            using (Utf8JsonWriter writer = new(buffer, IndentedWriterOptions))
            {

                arguments.Value.WriteTo(writer);

            }

            indented = Encoding.UTF8.GetString(buffer.WrittenSpan);

        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ObjectDisposedException)
        {

            indented = arguments.Value.ToString();

        }

        return indented.Length <= MaxArgumentsSummaryLength
            ? indented
            : string.Concat(indented.AsSpan(0, MaxArgumentsSummaryLength), "…");

    }

}
