using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Core.Services;
using RetroDownfall.TheForge.Ux.ViewModels.Docking;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class DockLayoutViewModelTests
{

    [Fact]
    public void DefaultLayout_MatchesCurrentShell()
    {

        using DockLayoutViewModel layout = new();

        Assert.Equal([DockToolId.Atelier], layout.Left.Tools.Select(t => t.ToolId));

        Assert.Equal(
            [DockToolId.Gatehouse, DockToolId.Treasury, DockToolId.Arsenal, DockToolId.WarTable],
            layout.Right.Tools.Select(t => t.ToolId));

        Assert.Equal(
            [DockToolId.Output, DockToolId.Logs, DockToolId.Hearth],
            layout.Bottom.Tools.Select(t => t.ToolId));

        Assert.Equal(DockToolId.Atelier, layout.Left.SelectedTool?.ToolId);

        Assert.Equal(DockToolId.Gatehouse, layout.Right.SelectedTool?.ToolId);

        Assert.Equal(DockToolId.Output, layout.Bottom.SelectedTool?.ToolId);

        DockToolViewModel? lore = layout.FindTool(DockToolId.Lore);

        Assert.NotNull(lore);

        Assert.False(lore.IsVisible);

        Assert.DoesNotContain(layout.Left.Tools, t => t.ToolId == DockToolId.Lore);

        Assert.DoesNotContain(layout.Right.Tools, t => t.ToolId == DockToolId.Lore);

        Assert.DoesNotContain(layout.Bottom.Tools, t => t.ToolId == DockToolId.Lore);

    }

    [Fact]
    public void ShowTool_Lore_UsesPreferredLeftRegion()
    {

        using DockLayoutViewModel layout = new();

        layout.ShowTool(DockToolId.Lore);

        Assert.Contains(layout.Left.Tools, t => t.ToolId == DockToolId.Lore);

        Assert.Equal(DockToolId.Lore, layout.Left.SelectedTool?.ToolId);

        DockToolViewModel lore = layout.FindTool(DockToolId.Lore)!;

        Assert.True(lore.IsVisible);

        Assert.Equal(DockRegion.Left, lore.Region);

    }

    [Fact]
    public void MoveTool_GatehouseToLeft_RemovesFromRightAndSelects()
    {

        using DockLayoutViewModel layout = new();

        DockToolViewModel gatehouse = Assert.Single(layout.Right.Tools, t => t.ToolId == DockToolId.Gatehouse);

        layout.MoveTool(gatehouse, DockRegion.Left);

        Assert.DoesNotContain(layout.Right.Tools, t => t.ToolId == DockToolId.Gatehouse);

        Assert.Contains(layout.Left.Tools, t => t.ToolId == DockToolId.Gatehouse);

        Assert.Equal(DockToolId.Gatehouse, layout.Left.SelectedTool?.ToolId);

        Assert.True(gatehouse.IsVisible);

        Assert.Equal(DockRegion.Left, gatehouse.Region);

        Assert.Equal(DockRegion.Left, gatehouse.LastRegion);

    }

    [Fact]
    public void HideAndShow_RestoresLastRegion()
    {

        using DockLayoutViewModel layout = new();

        DockToolViewModel arsenal = layout.FindTool(DockToolId.Arsenal)!;

        layout.HideTool(arsenal);

        Assert.DoesNotContain(layout.Right.Tools, t => t.ToolId == DockToolId.Arsenal);

        Assert.False(arsenal.IsVisible);

        Assert.Equal(DockRegion.Hidden, arsenal.Region);

        Assert.Equal(DockRegion.Right, arsenal.LastRegion);

        Assert.NotNull(layout.Right.SelectedTool);

        Assert.NotEqual(DockToolId.Arsenal, layout.Right.SelectedTool!.ToolId);

        layout.ShowTool(arsenal);

        Assert.Contains(layout.Right.Tools, t => t.ToolId == DockToolId.Arsenal);

        Assert.Equal(DockToolId.Arsenal, layout.Right.SelectedTool?.ToolId);

    }

    [Fact]
    public void FocusTool_SelectsWhereverDocked()
    {

        using DockLayoutViewModel layout = new();

        DockToolViewModel warTable = layout.FindTool(DockToolId.WarTable)!;

        layout.MoveTool(warTable, DockRegion.Bottom);

        layout.FocusTool(DockToolId.WarTable);

        Assert.Equal(DockToolId.WarTable, layout.Bottom.SelectedTool?.ToolId);

        layout.FocusTool(DockToolId.Gatehouse);

        Assert.Equal(DockToolId.Gatehouse, layout.Right.SelectedTool?.ToolId);

    }

    [Fact]
    public void ShowTool_WhenAlreadyVisible_SelectsWithoutDuplicating()
    {

        using DockLayoutViewModel layout = new();

        layout.Right.SelectedTool = layout.FindTool(DockToolId.Treasury);

        layout.ShowTool(DockToolId.Gatehouse);

        Assert.Equal(1, layout.Right.Tools.Count(t => t.ToolId == DockToolId.Gatehouse));

        Assert.Equal(DockToolId.Gatehouse, layout.Right.SelectedTool?.ToolId);

    }

    [Fact]
    public void ResetLayout_ReplacesEntireLayoutWithDefaults()
    {

        using DockLayoutViewModel layout = new();

        layout.MoveTool(layout.FindTool(DockToolId.Atelier)!, DockRegion.Right);

        layout.HideTool(layout.FindTool(DockToolId.Hearth)!);

        layout.Left.Size = 400;

        layout.ResetLayout();

        Assert.Equal([DockToolId.Atelier], layout.Left.Tools.Select(t => t.ToolId));

        Assert.Equal(
            [DockToolId.Gatehouse, DockToolId.Treasury, DockToolId.Arsenal, DockToolId.WarTable],
            layout.Right.Tools.Select(t => t.ToolId));

        Assert.Equal(
            [DockToolId.Output, DockToolId.Logs, DockToolId.Hearth],
            layout.Bottom.Tools.Select(t => t.ToolId));

        Assert.Equal(DockLayoutDefaults.DefaultLeftWidth, layout.Left.Size);

        Assert.True(layout.FindTool(DockToolId.Hearth)!.IsVisible);

    }

    [Fact]
    public void ApplyRegionSize_ClampsASplitterDragToTheRegionBounds()
    {

        using DockLayoutViewModel layout = new();

        layout.ApplyRegionSize(DockRegion.Left, 520);

        Assert.Equal(520, layout.Left.Size);

        layout.ApplyRegionSize(DockRegion.Left, DockLayoutDefaults.MaxLeftWidth + 200);

        Assert.Equal(DockLayoutDefaults.MaxLeftWidth, layout.Left.Size);

        layout.ApplyRegionSize(DockRegion.Left, 10);

        Assert.Equal(DockLayoutDefaults.MinLeftWidth, layout.Left.Size);

    }

    [Fact]
    public void ApplyRegionSize_IgnoresACollapsedRegionSoZeroIsNeverPersisted()
    {

        using DockLayoutViewModel layout = new();

        layout.HideTool(layout.FindTool(DockToolId.Atelier)!);

        Assert.True(layout.Left.IsCollapsed);

        layout.ApplyRegionSize(DockRegion.Left, 0);

        Assert.Equal(DockLayoutDefaults.DefaultLeftWidth, layout.Left.Size);

    }

    [Fact]
    public async Task ApplyRegionSize_PersistsTheDraggedWidth()
    {

        string path = Path.Combine(Path.GetTempPath(), $"forge-dock-splitter-{Guid.NewGuid():N}.json");

        try
        {

            TheForgeSettingsStore store = new(path);

            await store.SaveAsync(new TheForgeSettings { Theme = "light" });

            DockLayoutViewModel layout = new(store, persistDebounce: TimeSpan.FromSeconds(30));

            layout.ApplyRegionSize(DockRegion.Left, 520);

            layout.Dispose();

            TheForgeSettings loaded = await store.LoadAsync();

            Assert.False(string.IsNullOrWhiteSpace(loaded.LayoutState));

            TheForgeDockLayoutDto dto = DockLayoutSerializer.DeserializeOrDefault(loaded.LayoutState);

            Assert.Equal(520, dto.LeftWidth);

        }
        finally
        {

            if (File.Exists(path))
            {

                File.Delete(path);

            }

        }

    }

    [Fact]
    public async Task Dispose_FlushesPendingLayoutSave()
    {

        string path = Path.Combine(Path.GetTempPath(), $"forge-dock-flush-{Guid.NewGuid():N}.json");

        try
        {

            TheForgeSettingsStore store = new(path);

            await store.SaveAsync(new TheForgeSettings { Theme = "light" });

            DockLayoutViewModel layout = new(store, persistDebounce: TimeSpan.FromSeconds(30));

            layout.MoveTool(layout.FindTool(DockToolId.Gatehouse)!, DockRegion.Left);

            layout.Dispose();

            TheForgeSettings loaded = await store.LoadAsync();

            Assert.Equal("light", loaded.Theme);

            Assert.False(string.IsNullOrWhiteSpace(loaded.LayoutState));

            Assert.Contains(DockToolId.Gatehouse, loaded.LayoutState!, StringComparison.Ordinal);

        }
        finally
        {

            if (File.Exists(path))
            {

                File.Delete(path);

            }

        }

    }

}
