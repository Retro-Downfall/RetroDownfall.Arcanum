using System.Diagnostics;
using Microsoft.Extensions.Logging;
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

            TheForgeSettingsStore store = new(
                path,
                ImmediateTheForgeLocalMutationRunner.Instance);

            await store.SaveAsync(new TheForgeSettings { Theme = "light" });

            DockLayoutViewModel layout = new(store, persistDebounce: TimeSpan.FromSeconds(30));

            layout.ApplyRegionSize(DockRegion.Left, 520);

            layout.Dispose();

            TheForgeSettings loaded = await WaitForPersistedLayoutAsync(store);

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

            TheForgeSettingsStore store = new(
                path,
                ImmediateTheForgeLocalMutationRunner.Instance);

            await store.SaveAsync(new TheForgeSettings { Theme = "light" });

            DockLayoutViewModel layout = new(store, persistDebounce: TimeSpan.FromSeconds(30));

            layout.MoveTool(layout.FindTool(DockToolId.Gatehouse)!, DockRegion.Left);

            // No poll: production disposes and the process exits with nothing waiting on the flush
            // (App.axaml.cs:78 -> Program.cs:31), so the save has to be complete the instant Dispose
            // returns, not merely "eventually" true within some grace window.
            layout.Dispose();

            TheForgeSettings loaded = await store.LoadAsync();

            Assert.Equal("light", loaded.Theme);

            Assert.False(string.IsNullOrWhiteSpace(loaded.LayoutState));

            Assert.Contains(DockToolId.Gatehouse, loaded.LayoutState!, StringComparison.Ordinal);

            string directory = Path.GetDirectoryName(path)!;

            string tempFilePattern = Path.GetFileName(path) + ".*.tmp";

            Assert.Empty(Directory.EnumerateFiles(directory, tempFilePattern));

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
    public void Dispose_WhenTheFlushExceedsTheShutdownTimeout_ReturnsWithinTheBoundAndLogs()
    {

        RecordingLogger<DockLayoutViewModel> logger = new();

        SlowSettingsStore store = new(blockFor: TimeSpan.FromMilliseconds(400));

        DockLayoutViewModel layout = new(
            store,
            persistDebounce: TimeSpan.FromSeconds(30),
            logger: logger,
            shutdownFlushTimeout: TimeSpan.FromMilliseconds(50));

        layout.MoveTool(layout.FindTool(DockToolId.Gatehouse)!, DockRegion.Left);

        Stopwatch stopwatch = Stopwatch.StartNew();

        layout.Dispose();

        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(300),
            $"Dispose took {stopwatch.Elapsed}, expected to return close to the 50ms bound rather than wait out the 400ms flush.");

        Assert.Contains(
            logger.Messages,
            static m => m.Contains("did not complete", StringComparison.OrdinalIgnoreCase));

    }

    private sealed class SlowSettingsStore(TimeSpan blockFor) : ITheForgeSettingsStore
    {

        public string SettingsPath => "slow-store";

        public Task<TheForgeSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new TheForgeSettings());

        public Task SaveAsync(TheForgeSettings settings, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public async Task SavePatchAsync(
            Func<TheForgeSettings, TheForgeSettings> patch,
            CancellationToken cancellationToken = default)
        {

            // A real slow write (contended disk, a huge payload) is not interrupted by the
            // caller's token either — Dispose's flush always calls through with CancellationToken
            // .None, so this ignores cancellationToken and blocks unconditionally.
            await Task.Delay(blockFor, CancellationToken.None).ConfigureAwait(false);

        }

    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {

        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));

    }

    private static async Task<TheForgeSettings> WaitForPersistedLayoutAsync(
        TheForgeSettingsStore store)
    {

        for (int attempt = 0; attempt < 100; attempt++)
        {

            TheForgeSettings loaded = await store.LoadAsync();

            if (!string.IsNullOrWhiteSpace(loaded.LayoutState))
            {

                return loaded;

            }

            await Task.Delay(10);

        }

        return await store.LoadAsync();

    }

}
