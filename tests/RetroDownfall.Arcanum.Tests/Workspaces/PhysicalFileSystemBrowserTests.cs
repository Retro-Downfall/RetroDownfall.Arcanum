using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Workspaces;

public sealed class PhysicalFileSystemBrowserTests : IAsyncLifetime
{

    private TempWorkspace _workspace = null!;

    public async Task InitializeAsync()
    {

        _workspace = new TempWorkspace();

        await _workspace.InitializeAsync();

        _workspace.WriteFile("alpha.txt", "alpha");

        _workspace.WriteFile("nested/beta.txt", "beta");

        _workspace.CreateSubdir("folder");

    }

    public async Task DisposeAsync()
    {

        await _workspace.DisposeAsync();

    }

    [Fact]
    public async Task ListAsync_returns_sorted_entries_for_directory()
    {

        PhysicalFileSystemBrowser browser = CreateBrowser();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<FileListResult> result = await browser.ListAsync(workspace, null, recursive: false, searchPattern: null, CancellationToken.None);

        Assert.True(result.IsSuccess);

        string[] names = result.Value!.Entries.Select(e => e.Name).ToArray();

        Assert.Contains("alpha.txt", names);

        Assert.Contains("folder", names);

        Assert.Contains("nested", names);

    }

    [Fact]
    public async Task ListAsync_rejects_search_pattern_with_path_separators()
    {

        PhysicalFileSystemBrowser browser = CreateBrowser();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<FileListResult> result = await browser.ListAsync(workspace, null, recursive: false, searchPattern: "nested/*", CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.InvalidSearchPattern", result.Error.Code);

    }

    [Fact]
    public async Task ReadAsync_returns_utf8_content()
    {

        PhysicalFileSystemBrowser browser = CreateBrowser();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<FileReadResult> result = await browser.ReadAsync(workspace, "alpha.txt", CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal("alpha", result.Value!.Content);

        Assert.Equal("utf-8", result.Value.Encoding);

    }

    [Fact]
    public async Task GetInfoAsync_returns_file_metadata()
    {

        PhysicalFileSystemBrowser browser = CreateBrowser();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<FileEntry> result = await browser.GetInfoAsync(workspace, "alpha.txt", CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal("alpha.txt", result.Value!.Name);

        Assert.Equal(FileEntryType.File, result.Value.Type);

        Assert.True(result.Value.Size > 0);

    }

    [Fact]
    public async Task ListAsync_lists_nested_directory_non_recursively()
    {

        PhysicalFileSystemBrowser browser = CreateBrowser();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<FileListResult> result = await browser.ListAsync(workspace, "nested", recursive: false, searchPattern: null, CancellationToken.None);

        Assert.True(result.IsSuccess);

        string[] names = result.Value!.Entries.Select(e => e.Name).ToArray();

        Assert.Contains("beta.txt", names);

    }

    [Fact]
    public async Task ListAsync_recursive_finds_nested_files()
    {

        PhysicalFileSystemBrowser browser = CreateBrowser();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<FileListResult> result = await browser.ListAsync(workspace, null, recursive: true, searchPattern: null, CancellationToken.None);

        Assert.True(result.IsSuccess);

        string[] names = result.Value!.Entries.Select(e => e.Name).ToArray();

        Assert.Contains("alpha.txt", names);

        Assert.Contains("beta.txt", names);

    }

    [Fact]
    public async Task ListAsync_path_traversal_is_rejected()
    {

        PhysicalFileSystemBrowser browser = CreateBrowser();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<FileListResult> result = await browser.ListAsync(workspace, "../outside", recursive: false, searchPattern: null, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.PathTraversal", result.Error.Code);

    }

    [Fact]
    public async Task ListAsync_missing_directory_returns_not_found()
    {

        PhysicalFileSystemBrowser browser = CreateBrowser();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<FileListResult> result = await browser.ListAsync(workspace, "missing-dir", recursive: false, searchPattern: null, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.FileNotFound", result.Error.Code);

    }

    [Fact]
    public async Task ReadAsync_missing_file_returns_not_found()
    {

        PhysicalFileSystemBrowser browser = CreateBrowser();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<FileReadResult> result = await browser.ReadAsync(workspace, "missing.txt", CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.FileNotFound", result.Error.Code);

    }

    [Fact]
    public async Task ReadAsync_directory_path_returns_not_found()
    {

        PhysicalFileSystemBrowser browser = CreateBrowser();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<FileReadResult> result = await browser.ReadAsync(workspace, "folder", CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.FileNotFound", result.Error.Code);

    }

    [Fact]
    public async Task ReadAsync_oversized_file_returns_too_large()
    {

        _workspace.WriteFile("huge.txt", new string('x', 2048));

        ArcanumSettings settings = new()
        {
            Workspaces = new WorkspaceSettings { MaxFileReadSizeBytes = 1024 },
        };

        PhysicalFileSystemBrowser browser = new(new TestOptionsMonitor<ArcanumSettings>(settings));

        WorkspaceInfo workspace = MakeWorkspace();

        Result<FileReadResult> result = await browser.ReadAsync(workspace, "huge.txt", CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.FileTooLarge", result.Error.Code);

    }

    [Fact]
    public async Task GetInfoAsync_root_directory_returns_workspace_root()
    {

        PhysicalFileSystemBrowser browser = CreateBrowser();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<FileEntry> result = await browser.GetInfoAsync(workspace, null, CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(FileEntryType.Directory, result.Value!.Type);

        Assert.Equal(string.Empty, result.Value.RelativePath);

    }

    [Fact]
    public async Task GetInfoAsync_nested_directory_returns_parent_path()
    {

        PhysicalFileSystemBrowser browser = CreateBrowser();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<FileListResult> list = await browser.ListAsync(workspace, "nested", recursive: false, searchPattern: null, CancellationToken.None);

        Assert.True(list.IsSuccess);

        Assert.Equal(string.Empty, list.Value!.ParentPath);

    }

    [Fact]
    public async Task ListAsync_respects_max_paths_setting()
    {

        _workspace.WriteFile("one.txt", "1");

        _workspace.WriteFile("two.txt", "2");

        _workspace.WriteFile("three.txt", "3");

        ArcanumSettings settings = new()
        {
            Intelligence = new IntelligenceSettings { ListDirectoryMaxPaths = 2 },
        };

        PhysicalFileSystemBrowser browser = new(new TestOptionsMonitor<ArcanumSettings>(settings));

        WorkspaceInfo workspace = MakeWorkspace();

        Result<FileListResult> result = await browser.ListAsync(workspace, null, recursive: false, searchPattern: "*.txt", CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.True(result.Value!.Entries.Length <= 2);

    }

    [Fact]
    public async Task ReadAsync_rejects_symlink_to_outside_workspace()
    {

        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {

            return;

        }

        string outsideFile = Path.Combine(Path.GetTempPath(), $"arcanum-outside-{Guid.NewGuid():N}.txt");

        await File.WriteAllTextAsync(outsideFile, "outside secret");

        try
        {

            string linkPath = Path.Combine(_workspace.Root, "escape-link.txt");

            if (File.Exists(linkPath))
            {

                File.Delete(linkPath);

            }

            File.CreateSymbolicLink(linkPath, outsideFile);

            PhysicalFileSystemBrowser browser = CreateBrowser();

            WorkspaceInfo workspace = MakeWorkspace();

            Result<FileReadResult> result = await browser.ReadAsync(workspace, "escape-link.txt", CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal("Workspace.SymbolicLinkEscape", result.Error.Code);

        }
        finally
        {

            if (File.Exists(outsideFile))
            {

                File.Delete(outsideFile);

            }

        }

    }

    private PhysicalFileSystemBrowser CreateBrowser() =>
        new(new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()));

    private WorkspaceInfo MakeWorkspace() =>
        new("id", "test", _workspace.Root, WorkspaceType.Campaign, DateTimeOffset.UtcNow);

}
