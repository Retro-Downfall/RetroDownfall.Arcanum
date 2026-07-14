using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.TheForge.Ux.Markdown;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;

namespace RetroDownfall.TheForge.Ux.ViewModels.Workbench;

/// <summary>
/// Workbench document for CODEX.md — campaign-scoped when <see cref="CampaignId"/> is set, or the
/// Grimoire-global Codex when null. Loads and saves exclusively through Arcanum HTTP routes
/// (<c>/api/campaigns/{id}/codex</c> or <c>/api/codex</c>); never reads the workspace disk.
/// </summary>
public sealed partial class CodexViewModel : ViewModelBase
{

    private readonly ICodexDataSource _dataSource;

    private readonly FoundryFloorViewModel _foundryFloor;

    [ObservableProperty]
    private string _content = string.Empty;

    [ObservableProperty]
    private string? _codexPath;

    [ObservableProperty]
    private bool _exists;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _lastError;

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSourceVisible))]
    [NotifyPropertyChangedFor(nameof(IsPreviewVisible))]
    [NotifyPropertyChangedFor(nameof(IsSplitterVisible))]
    private MarkdownViewMode _viewMode = MarkdownViewMode.Source;

    [ObservableProperty]
    private bool _loadRemoteImages;

    /// <summary>
    /// Codex uses a TextBox source editor — scroll sync is best-effort only and defaults off.
    /// </summary>
    [ObservableProperty]
    private bool _syncScrollEnabled;

    public CodexViewModel(
        Guid? campaignId,
        ICodexDataSource dataSource,
        FoundryFloorViewModel foundryFloor)
    {

        CampaignId = campaignId;

        _dataSource = dataSource;

        _foundryFloor = foundryFloor;

        Title = campaignId is { } id
            ? $"Codex: {id:D}"
            : "Codex: global";

    }

    public override DocumentKind? Kind => DocumentKind.Codex;

    public Guid? CampaignId { get; }

    public bool IsGlobal => CampaignId is null;

    public bool IsSourceVisible => MarkdownViewModeHelper.IsSourceVisible(ViewMode);

    public bool IsPreviewVisible => MarkdownViewModeHelper.IsPreviewVisible(ViewMode);

    public bool IsSplitterVisible => MarkdownViewModeHelper.IsSplitterVisible(ViewMode);

    [RelayCommand]
    private void SetViewMode(MarkdownViewMode mode) => ViewMode = mode;

    [RelayCommand]
    public async Task LoadAsync(CancellationToken cancellationToken)
    {

        IsBusy = true;

        LastError = null;

        try
        {

            DataSourceResult<CodexContentDto> result = CampaignId is { } campaignId
                ? await _dataSource.GetCampaignCodexAsync(campaignId, cancellationToken).ConfigureAwait(true)
                : await _dataSource.GetGlobalCodexAsync(cancellationToken).ConfigureAwait(true);

            if (result.Success && result.Data is { } dto)
            {

                ApplyDto(dto);

                StatusText = Exists ? "Loaded." : "Codex not found — empty editor ready.";

            }

            else if (result.Success)
            {

                Content = string.Empty;

                Exists = false;

                CodexPath = null;

                StatusText = "Codex not found — empty editor ready.";

            }

            else
            {

                LastError = result.ErrorMessage ?? "Failed to load Codex.";

                StatusText = "Load failed.";

                _foundryFloor.AppendLine($"Codex load failed: {LastError}");

            }

        }

        catch (Exception ex)
        {

            LastError = ex.Message;

            _foundryFloor.AppendLine($"Codex load error: {ex.Message}");

        }

        finally
        {

            IsBusy = false;

        }

    }

    [RelayCommand]
    public async Task SaveAsync(CancellationToken cancellationToken)
    {

        IsBusy = true;

        LastError = null;

        try
        {

            DataSourceResult<CodexContentDto> result = CampaignId is { } campaignId
                ? await _dataSource.PutCampaignCodexAsync(campaignId, Content, cancellationToken).ConfigureAwait(true)
                : await _dataSource.PutGlobalCodexAsync(Content, cancellationToken).ConfigureAwait(true);

            if (result.Success && result.Data is { } dto)
            {

                ApplyDto(dto);

                StatusText = "Saved.";

            }

            else if (result.Success)
            {

                Exists = true;

                StatusText = "Saved.";

            }

            else
            {

                LastError = result.ErrorMessage ?? "Failed to save Codex.";

                StatusText = "Save failed.";

                _foundryFloor.AppendLine($"Codex save failed: {LastError}");

            }

        }

        catch (Exception ex)
        {

            LastError = ex.Message;

            _foundryFloor.AppendLine($"Codex save error: {ex.Message}");

        }

        finally
        {

            IsBusy = false;

        }

    }

    [RelayCommand]
    public async Task DeleteAsync(CancellationToken cancellationToken)
    {

        IsBusy = true;

        LastError = null;

        try
        {

            DataSourceResult<bool> result = CampaignId is { } campaignId
                ? await _dataSource.DeleteCampaignCodexAsync(campaignId, cancellationToken).ConfigureAwait(true)
                : await _dataSource.DeleteGlobalCodexAsync(cancellationToken).ConfigureAwait(true);

            if (result.Success)
            {

                Content = string.Empty;

                Exists = false;

                StatusText = "Deleted.";

            }

            else
            {

                LastError = result.ErrorMessage ?? "Failed to delete Codex.";

                StatusText = "Delete failed.";

                _foundryFloor.AppendLine($"Codex delete failed: {LastError}");

            }

        }

        catch (Exception ex)
        {

            LastError = ex.Message;

            _foundryFloor.AppendLine($"Codex delete error: {ex.Message}");

        }

        finally
        {

            IsBusy = false;

        }

    }

    private void ApplyDto(CodexContentDto dto)
    {

        Content = dto.Content ?? string.Empty;

        Exists = dto.Exists;

        CodexPath = dto.Path;

        if (!string.IsNullOrWhiteSpace(dto.Path))
        {

            Title = IsGlobal ? $"Codex: {dto.Path}" : $"Codex: {dto.Path}";

        }

    }

}
