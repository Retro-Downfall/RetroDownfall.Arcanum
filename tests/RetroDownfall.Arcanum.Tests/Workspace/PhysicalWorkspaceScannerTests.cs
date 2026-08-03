using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Workspaces;

public sealed class PhysicalWorkspaceScannerTests : IAsyncLifetime
{

    private TempWorkspace _workspace = null!;

    public async Task InitializeAsync()
    {

        _workspace = new TempWorkspace();

        await _workspace.InitializeAsync();

        _workspace.WriteFile("App.sln", "fake solution");

        _workspace.WriteFile("src/App.csproj", "<Project />");

        string ignoredBin = _workspace.CreateSubdir("bin");

        File.WriteAllText(Path.Combine(ignoredBin, "Hidden.sln"), "ignored");

    }

    public async Task DisposeAsync()
    {

        await _workspace.DisposeAsync();

    }

    [Fact]
    public async Task BuildProjectSummaryAsync_lists_solution_files_outside_ignored_dirs()
    {

        PhysicalWorkspaceScanner scanner = new();

        string summary = await scanner.BuildProjectSummaryAsync(_workspace.Root);

        Assert.Contains("App.sln", summary, StringComparison.Ordinal);

        Assert.DoesNotContain("Hidden.sln", summary, StringComparison.Ordinal);

        Assert.Contains("Working directory:", summary, StringComparison.Ordinal);

    }

    [Fact]
    public async Task BuildProjectSummaryAsync_missing_root_returns_not_found_message()
    {

        PhysicalWorkspaceScanner scanner = new();

        string missing = Path.Combine(_workspace.Root, "does-not-exist");

        string summary = await scanner.BuildProjectSummaryAsync(missing);

        Assert.Contains("Root path not found", summary, StringComparison.Ordinal);

    }

    [Fact]
    public async Task BuildProjectSummaryAsync_empty_tree_reports_none_found()
    {

        string emptyRoot = _workspace.CreateSubdir("empty-scan");

        PhysicalWorkspaceScanner scanner = new();

        string summary = await scanner.BuildProjectSummaryAsync(emptyRoot);

        Assert.Contains("(none found)", summary, StringComparison.Ordinal);

    }

    [Fact]
    public async Task BuildProjectSummaryAsync_discovers_solutions_beyond_the_former_depth_ceiling()
    {

        string current = _workspace.Root;

        for (int depth = 0; depth < 70; depth++)
        {

            current = Directory.CreateDirectory(Path.Combine(current, $"level-{depth:D2}")).FullName;

        }

        string solutionPath = Path.Combine(current, "Deep.sln");

        await File.WriteAllTextAsync(solutionPath, "fake solution");

        PhysicalWorkspaceScanner scanner = new();

        string summary = await scanner.BuildProjectSummaryAsync(_workspace.Root);

        Assert.Contains("Deep.sln", summary, StringComparison.Ordinal);

    }

}
