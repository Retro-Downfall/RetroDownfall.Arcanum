using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Core.Services;

namespace RetroDownfall.TheForge.Ux.ViewModels.Docking;

/// <summary>
/// Owns dock tool metadata and Left/Right/Bottom group state. Does not create API-backed content
/// ViewModels — <c>MainViewModel</c> assigns <see cref="DockToolViewModel.Content"/>.
/// </summary>
public sealed partial class DockLayoutViewModel : ObservableObject, IDisposable
{

    private readonly Dictionary<string, DockToolViewModel> _toolsById = new(StringComparer.Ordinal);

    private readonly ITheForgeSettingsStore? _settingsStore;

    private readonly ILogger<DockLayoutViewModel>? _logger;

    private readonly TimeSpan _persistDebounce;

    private CancellationTokenSource? _persistCts;

    private bool _suppressPersist;

    private bool _disposed;

    [ObservableProperty]
    private bool _isDragging;

    [ObservableProperty]
    private DockRegion? _dragPreviewRegion;

    public DockLayoutViewModel(
        ITheForgeSettingsStore? settingsStore = null,
        string? layoutState = null,
        TimeSpan? persistDebounce = null,
        ILogger<DockLayoutViewModel>? logger = null)
    {

        _settingsStore = settingsStore;

        _logger = logger;

        _persistDebounce = persistDebounce ?? TimeSpan.FromMilliseconds(400);

        Left = new DockGroupViewModel(DockRegion.Left, DockLayoutDefaults.DefaultLeftWidth);

        Right = new DockGroupViewModel(DockRegion.Right, DockLayoutDefaults.DefaultRightWidth);

        Bottom = new DockGroupViewModel(DockRegion.Bottom, DockLayoutDefaults.DefaultBottomHeight);

        foreach ((string toolId, string title, string? iconKey, DockRegion region, int order) in DockLayoutDefaults.Tools)
        {

            DockToolViewModel tool = new(toolId, title, iconKey, region, order)
            {
                Owner = this,
            };

            _toolsById[toolId] = tool;

            AllTools.Add(tool);

        }

        ApplyDto(DockLayoutSerializer.DeserializeOrDefault(layoutState), persist: false);

        Left.PropertyChanged += OnGroupPropertyChanged;

        Right.PropertyChanged += OnGroupPropertyChanged;

        Bottom.PropertyChanged += OnGroupPropertyChanged;

    }

    public ObservableCollection<DockToolViewModel> AllTools { get; } = [];

    public DockGroupViewModel Left { get; }

    public DockGroupViewModel Right { get; }

    public DockGroupViewModel Bottom { get; }

    public DockToolViewModel? FindTool(string toolId) =>
        _toolsById.TryGetValue(toolId, out DockToolViewModel? tool) ? tool : null;

    public void SetContent(string toolId, object? content)
    {

        if (_toolsById.TryGetValue(toolId, out DockToolViewModel? tool))
        {

            tool.Content = content;

        }

    }

    [RelayCommand]
    private void MoveLeft(DockToolViewModel? tool)
    {

        if (tool is not null)
        {

            MoveTool(tool, DockRegion.Left);

        }

    }

    [RelayCommand]
    private void MoveRight(DockToolViewModel? tool)
    {

        if (tool is not null)
        {

            MoveTool(tool, DockRegion.Right);

        }

    }

    [RelayCommand]
    private void MoveBottom(DockToolViewModel? tool)
    {

        if (tool is not null)
        {

            MoveTool(tool, DockRegion.Bottom);

        }

    }

    [RelayCommand]
    public void MoveTo(string? toolIdAndRegion)
    {

        if (string.IsNullOrWhiteSpace(toolIdAndRegion))
        {

            return;

        }

        string[] parts = toolIdAndRegion.Split('|', 2, StringSplitOptions.TrimEntries);

        if (parts.Length != 2 || !_toolsById.TryGetValue(parts[0], out DockToolViewModel? tool))
        {

            return;

        }

        DockRegion? region = DockLayoutSerializer.ParseRegion(parts[1]);

        if (region is null || region == DockRegion.Hidden)
        {

            return;

        }

        MoveTool(tool, region.Value);

    }

    public void MoveTool(DockToolViewModel tool, DockRegion region)
    {

        ObjectDisposedException.ThrowIf(_disposed, this);

        if (region == DockRegion.Hidden)
        {

            HideTool(tool);

            return;

        }

        DockGroupViewModel? oldGroup = GroupFor(tool.Region);

        DockGroupViewModel target = GroupFor(region) ?? throw new ArgumentOutOfRangeException(nameof(region));

        if (oldGroup is not null && ReferenceEquals(oldGroup, target) && tool.IsVisible)
        {

            target.SelectedTool = tool;

            SchedulePersist();

            return;

        }

        if (oldGroup is not null)
        {

            oldGroup.Tools.Remove(tool);

            oldGroup.SelectNeighborOrDefault(tool);

        }

        tool.Region = region;

        tool.LastRegion = region;

        tool.IsVisible = true;

        tool.Order = target.Tools.Count;

        target.Tools.Add(tool);

        target.SelectedTool = tool;

        Renumber(target);

        NotifyGroupSizes();

        SchedulePersist();

    }

    [RelayCommand]
    private void Hide(DockToolViewModel? tool)
    {

        if (tool is not null)
        {

            HideTool(tool);

        }

    }

    [RelayCommand]
    public void HideTool(string? toolId)
    {

        if (toolId is null || !_toolsById.TryGetValue(toolId, out DockToolViewModel? tool))
        {

            return;

        }

        HideTool(tool);

    }

    public void HideTool(DockToolViewModel tool)
    {

        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!tool.IsVisible && tool.Region == DockRegion.Hidden)
        {

            return;

        }

        DockGroupViewModel? oldGroup = GroupFor(tool.Region);

        if (oldGroup is not null)
        {

            tool.LastRegion = oldGroup.Region;

            oldGroup.Tools.Remove(tool);

            oldGroup.SelectNeighborOrDefault(tool);

        }

        tool.Region = DockRegion.Hidden;

        tool.IsVisible = false;

        NotifyGroupSizes();

        SchedulePersist();

    }

    [RelayCommand]
    public void ShowTool(string? toolId)
    {

        if (toolId is null || !_toolsById.TryGetValue(toolId, out DockToolViewModel? tool))
        {

            return;

        }

        ShowTool(tool);

    }

    public void ShowTool(DockToolViewModel tool)
    {

        ObjectDisposedException.ThrowIf(_disposed, this);

        if (tool.IsVisible && tool.Region != DockRegion.Hidden)
        {

            FocusTool(tool);

            return;

        }

        DockRegion target = tool.LastRegion == DockRegion.Hidden
            ? DockLayoutDefaults.DefaultRegionFor(tool.ToolId)
            : tool.LastRegion;

        MoveTool(tool, target);

    }

    [RelayCommand]
    public void FocusTool(string? toolId)
    {

        if (toolId is null || !_toolsById.TryGetValue(toolId, out DockToolViewModel? tool))
        {

            return;

        }

        FocusTool(tool);

    }

    public void FocusTool(DockToolViewModel tool)
    {

        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!tool.IsVisible || tool.Region == DockRegion.Hidden)
        {

            ShowTool(tool);

            return;

        }

        DockGroupViewModel? group = GroupFor(tool.Region);

        if (group is not null)
        {

            group.SelectedTool = tool;

        }

    }

    [RelayCommand]
    public void ResetLayout()
    {

        ObjectDisposedException.ThrowIf(_disposed, this);

        ApplyDto(DockLayoutSerializer.CreateDefaultDto(), persist: true);

    }

    public TheForgeDockLayoutDto CaptureDto()
    {

        List<TheForgeDockToolLayoutDto> tools = AllTools
            .Select(t => new TheForgeDockToolLayoutDto(
                t.ToolId,
                DockLayoutSerializer.RegionToString(t.IsVisible ? t.Region : DockRegion.Hidden),
                DockLayoutSerializer.RegionToString(t.LastRegion == DockRegion.Hidden
                    ? DockLayoutDefaults.DefaultRegionFor(t.ToolId)
                    : t.LastRegion),
                t.IsVisible && t.Region != DockRegion.Hidden,
                t.Order))
            .ToList();

        return DockLayoutSerializer.Normalize(new TheForgeDockLayoutDto(
            DockLayoutDefaults.SchemaVersion,
            tools,
            Left.SelectedTool?.ToolId,
            Right.SelectedTool?.ToolId,
            Bottom.SelectedTool?.ToolId,
            Left.Size,
            Right.Size,
            Bottom.Size));

    }

    public void Dispose()
    {

        if (_disposed)
        {

            return;

        }

        _disposed = true;

        Left.PropertyChanged -= OnGroupPropertyChanged;

        Right.PropertyChanged -= OnGroupPropertyChanged;

        Bottom.PropertyChanged -= OnGroupPropertyChanged;

        // Flush pending layout save before cancelling the debounce timer.
        FlushPersist();

        _persistCts?.Cancel();

        _persistCts?.Dispose();

        _persistCts = null;

        GC.SuppressFinalize(this);

    }

    private void ApplyDto(TheForgeDockLayoutDto dto, bool persist)
    {

        _suppressPersist = true;

        try
        {

            Left.Tools.Clear();

            Right.Tools.Clear();

            Bottom.Tools.Clear();

            Left.Size = dto.LeftWidth;

            Right.Size = dto.RightWidth;

            Bottom.Size = dto.BottomHeight;

            foreach (TheForgeDockToolLayoutDto toolDto in dto.Tools.OrderBy(t => t.Order).ThenBy(t => t.ToolId))
            {

                if (!_toolsById.TryGetValue(toolDto.ToolId, out DockToolViewModel? tool))
                {

                    continue;

                }

                DockRegion region = DockLayoutSerializer.ParseRegion(toolDto.Region) ?? DockLayoutDefaults.DefaultRegionFor(toolDto.ToolId);

                DockRegion lastRegion = DockLayoutSerializer.ParseRegion(toolDto.LastRegion) is { } parsedLast && parsedLast != DockRegion.Hidden
                    ? parsedLast
                    : (region == DockRegion.Hidden ? DockLayoutDefaults.DefaultRegionFor(toolDto.ToolId) : region);

                tool.Order = toolDto.Order;

                tool.LastRegion = lastRegion;

                if (!toolDto.IsVisible || region == DockRegion.Hidden)
                {

                    tool.Region = DockRegion.Hidden;

                    tool.IsVisible = false;

                    continue;

                }

                tool.Region = region;

                tool.IsVisible = true;

                DockGroupViewModel? group = GroupFor(region);

                group?.Tools.Add(tool);

            }

            SelectActive(Left, dto.ActiveLeftToolId);

            SelectActive(Right, dto.ActiveRightToolId);

            SelectActive(Bottom, dto.ActiveBottomToolId);

            NotifyGroupSizes();

        }
        finally
        {

            _suppressPersist = false;

        }

        if (persist)
        {

            SchedulePersist();

        }

    }

    private static void SelectActive(DockGroupViewModel group, string? activeId)
    {

        if (activeId is not null)
        {

            DockToolViewModel? match = group.Tools.FirstOrDefault(t => t.ToolId == activeId);

            if (match is not null)
            {

                group.SelectedTool = match;

                return;

            }

        }

        group.SelectedTool = group.Tools.Count > 0 ? group.Tools[0] : null;

    }

    private DockGroupViewModel? GroupFor(DockRegion region) => region switch
    {
        DockRegion.Left => Left,
        DockRegion.Right => Right,
        DockRegion.Bottom => Bottom,
        _ => null,
    };

    private static void Renumber(DockGroupViewModel group)
    {

        for (int i = 0; i < group.Tools.Count; i++)
        {

            group.Tools[i].Order = i;

        }

    }

    private void NotifyGroupSizes()
    {

        Left.NotifyLayoutChanged();

        Right.NotifyLayoutChanged();

        Bottom.NotifyLayoutChanged();

    }

    private void OnGroupPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {

        if (_suppressPersist || _disposed)
        {

            return;

        }

        if (e.PropertyName is nameof(DockGroupViewModel.Size) or nameof(DockGroupViewModel.SelectedTool))
        {

            SchedulePersist();

        }

    }

    private void SchedulePersist()
    {

        if (_suppressPersist || _disposed || _settingsStore is null)
        {

            return;

        }

        _persistCts?.Cancel();

        _persistCts?.Dispose();

        CancellationTokenSource cts = new();

        _persistCts = cts;

        _ = PersistAfterDelayAsync(cts);

    }

    private async Task PersistAfterDelayAsync(CancellationTokenSource cts)
    {

        try
        {

            await Task.Delay(_persistDebounce, cts.Token).ConfigureAwait(false);

            await PersistNowAsync(CancellationToken.None).ConfigureAwait(false);

        }
        catch (OperationCanceledException)
        {

            // Debounced — a newer change replaced this timer.
        }
        catch (Exception ex)
        {

            _logger?.LogWarning(ex, "Failed to persist dock layout state.");

        }

    }

    private void FlushPersist()
    {

        if (_settingsStore is null || _persistCts is null)
        {

            return;

        }

        try
        {

            PersistNowAsync(CancellationToken.None).GetAwaiter().GetResult();

        }
        catch (Exception ex)
        {

            _logger?.LogWarning(ex, "Failed to flush dock layout state on dispose.");

        }

    }

    private async Task PersistNowAsync(CancellationToken cancellationToken)
    {

        if (_settingsStore is null)
        {

            return;

        }

        string json = DockLayoutSerializer.Serialize(CaptureDto());

        await _settingsStore.SavePatchAsync(s => s with { LayoutState = json }, cancellationToken).ConfigureAwait(false);

    }

}
