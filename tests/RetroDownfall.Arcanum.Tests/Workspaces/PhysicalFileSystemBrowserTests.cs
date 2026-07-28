using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Workspaces;

[Collection("WorkspacePathPolicy")]
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

        SecureFileReader.AfterOpenForTests = null;

        FileHandleIdentityInterop.TryGetPathMetadataForTests = null;

        FileHandleIdentityInterop.TryGetHandleMetadataForTests = null;

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

        int oversizedLength = checked(
            (int)ArcanumSettingClamps.MaxFileReadSizeBytes(
                ArcanumRuntimeDefaults.WorkspaceMaxFileReadSizeBytes)
            + 1);
        _workspace.WriteFile("huge.txt", new string('x', oversizedLength));

        PhysicalFileSystemBrowser browser = CreateBrowser();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<FileReadResult> result = await browser.ReadAsync(workspace, "huge.txt", CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.FileTooLarge", result.Error.Code);

    }

    [Fact]
    public async Task ReadAsync_rejects_file_that_grows_past_cap_after_open()
    {

        int capLength = checked(
            (int)ArcanumSettingClamps.MaxFileReadSizeBytes(
                ArcanumRuntimeDefaults.WorkspaceMaxFileReadSizeBytes));

        _workspace.WriteFile("growing.txt", new string('x', capLength));

        PhysicalFileSystemBrowser browser = CreateBrowser();

        WorkspaceInfo workspace = MakeWorkspace();

        SecureFileReader.AfterOpenForTests = path =>
        {
            SecureFileReader.AfterOpenForTests = null;

            File.AppendAllText(path, "y");
        };

        Result<FileReadResult> result = await browser.ReadAsync(
            workspace,
            "growing.txt",
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.FileTooLarge", result.Error.Code);

    }

    [Fact]
    public async Task ReadAsync_rejects_preopen_and_handle_identity_mismatch()
    {

        _workspace.WriteFile("mismatch.txt", "secret");

        FileHandleIdentityInterop.TryGetPathMetadataForTests = _ =>
            new FileHandleMetadata(
                new FileHandleIdentity(7, 1),
                HardLinkCount: 1);

        FileHandleIdentityInterop.TryGetHandleMetadataForTests = _ =>
            new FileHandleMetadata(
                new FileHandleIdentity(7, 2),
                HardLinkCount: 1);

        Result<FileReadResult> result = await CreateBrowser().ReadAsync(
            MakeWorkspace(),
            "mismatch.txt",
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.SymbolicLinkEscape", result.Error.Code);

        Assert.DoesNotContain(
            "secret",
            result.Error.Message,
            StringComparison.Ordinal);

    }

    [Fact]
    public async Task ReadAsync_rejects_malformed_utf8()
    {

        string path = Path.Combine(_workspace.Root, "malformed.txt");

        await File.WriteAllBytesAsync(path, [0x66, 0x80, 0x6f]);

        Result<FileReadResult> result = await CreateBrowser().ReadAsync(
            MakeWorkspace(),
            "malformed.txt",
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.AccessDenied", result.Error.Code);

        Assert.DoesNotContain(path, result.Error.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("f�o", result.Error.Message, StringComparison.Ordinal);

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
    public async Task ListAsync_respects_internal_max_paths_boundary()
    {
        int maxPaths = ArcanumSettingClamps.ListDirectoryMaxPaths(
            ArcanumRuntimeDefaults.Intelligence.ListDirectoryMaxPaths);
        for (int i = 0; i <= maxPaths; i++)
        {
            _workspace.WriteFile($"bounded-{i}.txt", "x");
        }

        PhysicalFileSystemBrowser browser = CreateBrowser();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<FileListResult> result = await browser.ListAsync(workspace, null, recursive: false, searchPattern: "*.txt", CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(maxPaths, result.Value!.Entries.Length);

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
