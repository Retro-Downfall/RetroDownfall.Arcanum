using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Pattern;
using RetroDownfall.Arcanum.Core.Pattern.Entities;
using RetroDownfall.Arcanum.Infrastructure.Pattern;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Pattern;

public sealed class EyeOfTheWorldServiceTests : IAsyncLifetime
{

    private TempWorkspace _workspace = null!;

    public async Task InitializeAsync()
    {

        _workspace = new TempWorkspace();

        await _workspace.InitializeAsync();

        _workspace.WriteFile("App.sln", "solution");

        _workspace.WriteFile("src/App.csproj", "<Project />");

        _workspace.WriteFile("README.md", "# Notes");

        string ignored = _workspace.CreateSubdir("bin");

        File.WriteAllText(Path.Combine(ignored, "ignored.cs"), "// skip");

    }

    public async Task DisposeAsync()
    {

        await _workspace.DisposeAsync();

    }

    [Fact]
    public async Task PerceivePatternAsync_empty_path_returns_unknown_domain()
    {

        EyeOfTheWorldService service = new(new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()));

        PatternSnapshot snapshot = await service.PerceivePatternAsync("  ", CancellationToken.None);

        Assert.Equal(DomainType.Unknown, snapshot.Domain);

        Assert.Contains(snapshot.Threads, t => t.Contains("empty or invalid", StringComparison.OrdinalIgnoreCase));

    }

    [Fact]
    public async Task PerceivePatternAsync_missing_directory_returns_unknown_domain()
    {

        EyeOfTheWorldService service = new(new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()));

        string missing = Path.Combine(_workspace.Root, "missing");

        PatternSnapshot snapshot = await service.PerceivePatternAsync(missing, CancellationToken.None);

        Assert.Equal(DomainType.Unknown, snapshot.Domain);

        Assert.Contains(snapshot.Threads, t => t.Contains("directory not found", StringComparison.OrdinalIgnoreCase));

    }

    [Fact]
    public async Task PerceivePatternAsync_detects_software_engineering_artifacts()
    {

        EyeOfTheWorldService service = new(new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()));

        PatternSnapshot snapshot = await service.PerceivePatternAsync(_workspace.Root, CancellationToken.None);

        Assert.Equal(DomainType.SoftwareEngineering, snapshot.Domain);

        Assert.Contains(snapshot.Threads, t => t.StartsWith("Solution:", StringComparison.Ordinal));

        Assert.Contains(snapshot.Threads, t => t.StartsWith("Project:", StringComparison.Ordinal));

        Assert.DoesNotContain(snapshot.Threads, t => t.Contains("bin", StringComparison.OrdinalIgnoreCase));

    }

    [Fact]
    public async Task PerceivePatternAsync_detects_administration_domain_from_office_files()
    {

        TempWorkspace adminWorkspace = new();

        await adminWorkspace.InitializeAsync();

        try
        {

            adminWorkspace.WriteFile("report.pdf", "%PDF");

            adminWorkspace.WriteFile("budget.xlsx", "xlsx");

            adminWorkspace.WriteFile("memo.docx", "docx");

            EyeOfTheWorldService service = new(new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()));

            PatternSnapshot snapshot = await service.PerceivePatternAsync(adminWorkspace.Root, CancellationToken.None);

            Assert.Equal(DomainType.Administration, snapshot.Domain);

            Assert.Contains(snapshot.Threads, t => t.StartsWith("Document:", StringComparison.Ordinal));

        }
        finally
        {

            await adminWorkspace.DisposeAsync();

        }

    }

    [Fact]
    public async Task PerceivePatternAsync_detects_research_domain_from_prose_files()
    {

        TempWorkspace researchWorkspace = new();

        await researchWorkspace.InitializeAsync();

        try
        {

            researchWorkspace.WriteFile("notes/a.md", "# A");

            researchWorkspace.WriteFile("notes/b.md", "# B");

            researchWorkspace.WriteFile("notes/c.txt", "C");

            researchWorkspace.WriteFile("notes/d.md", "# D");

            researchWorkspace.WriteFile("notes/e.txt", "E");

            EyeOfTheWorldService service = new(new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()));

            PatternSnapshot snapshot = await service.PerceivePatternAsync(researchWorkspace.Root, CancellationToken.None);

            Assert.Equal(DomainType.Research, snapshot.Domain);

            Assert.Contains(snapshot.Threads, t => t.StartsWith("Note:", StringComparison.Ordinal));

        }
        finally
        {

            await researchWorkspace.DisposeAsync();

        }

    }

    [Fact]
    public async Task PerceivePatternAsync_unknown_domain_lists_recent_files()
    {

        TempWorkspace unknownWorkspace = new();

        await unknownWorkspace.InitializeAsync();

        try
        {

            unknownWorkspace.WriteFile("alpha.dat", "a");

            unknownWorkspace.WriteFile("beta.dat", "b");

            EyeOfTheWorldService service = new(new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()));

            PatternSnapshot snapshot = await service.PerceivePatternAsync(unknownWorkspace.Root, CancellationToken.None);

            Assert.Equal(DomainType.Unknown, snapshot.Domain);

            Assert.Contains(snapshot.Threads, t => t.StartsWith("File:", StringComparison.Ordinal));

        }
        finally
        {

            await unknownWorkspace.DisposeAsync();

        }

    }

    [Fact]
    public async Task PerceivePatternAsync_truncates_when_enumeration_limit_reached()
    {

        TempWorkspace truncWorkspace = new();

        await truncWorkspace.InitializeAsync();

        try
        {

            for (int i = 0; i < 6; i++)
            {
                truncWorkspace.WriteFile($"file{i}.dat", "x");
            }

            ArcanumSettings settings = new()
            {
                Perception = new PerceptionSettings { MaxEnumerationSteps = 3 },
            };

            EyeOfTheWorldService service = new(new TestOptionsMonitor<ArcanumSettings>(settings));

            PatternSnapshot snapshot = await service.PerceivePatternAsync(truncWorkspace.Root, CancellationToken.None);

            Assert.Contains(snapshot.Threads, t => t.Contains("truncated after 3 files", StringComparison.Ordinal));

        }
        finally
        {

            await truncWorkspace.DisposeAsync();

        }

    }

    [Fact]
    public async Task PerceivePatternAsync_detects_package_json_and_dockerfile()
    {

        TempWorkspace markerWorkspace = new();

        await markerWorkspace.InitializeAsync();

        try
        {

            markerWorkspace.WriteFile("package.json", "{}");

            markerWorkspace.WriteFile("Dockerfile", "FROM scratch");

            EyeOfTheWorldService service = new(new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()));

            PatternSnapshot snapshot = await service.PerceivePatternAsync(markerWorkspace.Root, CancellationToken.None);

            Assert.Equal(DomainType.SoftwareEngineering, snapshot.Domain);

            Assert.Contains(snapshot.Threads, t => t.StartsWith("Package:", StringComparison.Ordinal));

            Assert.Contains(snapshot.Threads, t => t.StartsWith("Dockerfile:", StringComparison.Ordinal));

        }
        finally
        {

            await markerWorkspace.DisposeAsync();

        }

    }

    [Fact]
    public async Task PerceivePatternAsync_classifies_dev_source_volume_without_project_files()
    {

        TempWorkspace sourceWorkspace = new();

        await sourceWorkspace.InitializeAsync();

        try
        {

            for (int i = 0; i < 25; i++)
            {
                sourceWorkspace.WriteFile($"src/File{i}.cs", $"class F{i} {{}}");
            }

            EyeOfTheWorldService service = new(new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()));

            PatternSnapshot snapshot = await service.PerceivePatternAsync(sourceWorkspace.Root, CancellationToken.None);

            Assert.Equal(DomainType.SoftwareEngineering, snapshot.Domain);

        }
        finally
        {

            await sourceWorkspace.DisposeAsync();

        }

    }

    [Fact]
    public async Task PerceivePatternAsync_cancellation_propagates()
    {

        EyeOfTheWorldService service = new(new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()));

        using CancellationTokenSource cts = new();

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.PerceivePatternAsync(_workspace.Root, cts.Token));

    }

    [Fact]
    public async Task PerceivePatternAsync_prunes_ignored_directories_before_descent()
    {

        TempWorkspace pruneWorkspace = new();

        await pruneWorkspace.InitializeAsync();

        try
        {

            pruneWorkspace.WriteFile("App.sln", "solution");

            for (int i = 0; i < 80; i++)
            {

                pruneWorkspace.WriteFile(
                    $"node_modules/pkg/file{i}.js".Replace('/', Path.DirectorySeparatorChar),
                    "console.log('ignored');");

            }

            // Budget smaller than node_modules file count — prune-before-descend must still see App.sln.
            ArcanumSettings settings = new()
            {
                Perception = new PerceptionSettings { MaxEnumerationSteps = 20 },
            };

            EyeOfTheWorldService service = new(new TestOptionsMonitor<ArcanumSettings>(settings));

            PatternSnapshot snapshot = await service.PerceivePatternAsync(pruneWorkspace.Root, CancellationToken.None);

            Assert.Equal(DomainType.SoftwareEngineering, snapshot.Domain);

            Assert.Contains(snapshot.Threads, t => t.StartsWith("Solution:", StringComparison.Ordinal));

            Assert.DoesNotContain(snapshot.Threads, t => t.Contains("node_modules", StringComparison.OrdinalIgnoreCase));

            Assert.DoesNotContain(snapshot.Threads, t => t.Contains("truncated", StringComparison.OrdinalIgnoreCase));

        }
        finally
        {

            await pruneWorkspace.DisposeAsync();

        }

    }

    [Fact]
    public async Task PerceivePatternAsync_symlink_cycle_terminates_via_visited_set()
    {

        TempWorkspace cycleWorkspace = new();

        await cycleWorkspace.InitializeAsync();

        try
        {

            string nested = cycleWorkspace.CreateSubdir("nested");

            cycleWorkspace.WriteFile("marker.dat", "x");

            string linkPath = Path.Combine(nested, "cycle-back");

            try
            {

                _ = Directory.CreateSymbolicLink(linkPath, cycleWorkspace.Root);

            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {

                // Symlink creation may require privileges on some hosts — skip rather than fail CI.
                return;

            }

            ArcanumSettings settings = new()
            {
                Perception = new PerceptionSettings { MaxEnumerationSteps = 50 },
            };

            EyeOfTheWorldService service = new(new TestOptionsMonitor<ArcanumSettings>(settings));

            PatternSnapshot snapshot = await service.PerceivePatternAsync(cycleWorkspace.Root, CancellationToken.None);

            // Completes without hanging; cycle must not consume the full enumeration budget alone.
            Assert.DoesNotContain(snapshot.Threads, t => t.Contains("truncated", StringComparison.OrdinalIgnoreCase));

            Assert.Contains(snapshot.Threads, t => t.Contains("marker.dat", StringComparison.Ordinal));

        }
        finally
        {

            await cycleWorkspace.DisposeAsync();

        }

    }

}
