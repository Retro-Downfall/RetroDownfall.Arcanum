using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Workspaces;

public sealed class PhysicalFileSystemWriterTests : IAsyncLifetime
{

    private TempWorkspace _workspace = null!;

    public async Task InitializeAsync()
    {

        _workspace = new TempWorkspace();

        await _workspace.InitializeAsync();

    }

    public async Task DisposeAsync()
    {

        await _workspace.DisposeAsync();

    }

    [Fact]
    public async Task WriteFileAsync_creates_new_file_with_parent_directories()
    {

        PhysicalFileSystemWriter writer = CreateWriter();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<FileWriteResult> result = await writer.WriteFileAsync(workspace, "nested/deep/new.txt", "hello", CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal("nested/deep/new.txt", result.Value!.RelativePath.Replace('\\', '/'));

        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(_workspace.Root, "nested", "deep", "new.txt")));

    }

    [Fact]
    public async Task WriteFileAsync_overwrites_existing_file()
    {

        _workspace.WriteFile("existing.txt", "old content");

        PhysicalFileSystemWriter writer = CreateWriter();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<FileWriteResult> result = await writer.WriteFileAsync(workspace, "existing.txt", "new content", CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal("new content", await File.ReadAllTextAsync(Path.Combine(_workspace.Root, "existing.txt")));

    }

    [Fact]
    public async Task WriteFileAsync_rejects_path_traversal()
    {

        PhysicalFileSystemWriter writer = CreateWriter();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<FileWriteResult> result = await writer.WriteFileAsync(workspace, "../outside.txt", "hello", CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.PathTraversal", result.Error.Code);

    }

    [Fact]
    public async Task WriteFileAsync_rejects_absolute_paths()
    {

        PhysicalFileSystemWriter writer = CreateWriter();

        WorkspaceInfo workspace = MakeWorkspace();

        string absolutePath = OperatingSystem.IsWindows() ? "C:\\outside.txt" : "/etc/outside.txt";

        Result<FileWriteResult> result = await writer.WriteFileAsync(workspace, absolutePath, "hello", CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.PathTraversal", result.Error.Code);

    }

    [Fact]
    public async Task WriteFileAsync_rejects_symlink_escape()
    {

        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            return;
        }

        string outsideDir = Path.Combine(Path.GetTempPath(), $"arcanum-outside-{Guid.NewGuid():N}");

        Directory.CreateDirectory(outsideDir);

        try
        {

            string linkPath = Path.Combine(_workspace.Root, "escape-link.txt");

            string outsideFile = Path.Combine(outsideDir, "target.txt");

            await File.WriteAllTextAsync(outsideFile, "outside secret");

            File.CreateSymbolicLink(linkPath, outsideFile);

            PhysicalFileSystemWriter writer = CreateWriter();

            WorkspaceInfo workspace = MakeWorkspace();

            Result<FileWriteResult> result = await writer.WriteFileAsync(workspace, "escape-link.txt", "overwritten", CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal("Workspace.SymbolicLinkEscape", result.Error.Code);

            Assert.Equal("outside secret", await File.ReadAllTextAsync(outsideFile));

        }
        finally
        {

            Directory.Delete(outsideDir, recursive: true);

        }

    }

    [Fact]
    public async Task WriteFileAsync_rejects_content_exceeding_MaxFileWriteSizeBytes()
    {

        ArcanumSettings settings = new()
        {
            Workspaces = new WorkspaceSettings { EnableFileWrite = true, MaxFileWriteSizeBytes = 1024 },
        };

        PhysicalFileSystemWriter writer = CreateWriter(settings);

        WorkspaceInfo workspace = MakeWorkspace();

        Result<FileWriteResult> result = await writer.WriteFileAsync(workspace, "big.txt", new string('x', 2048), CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.FileTooLarge", result.Error.Code);

    }

    [Fact]
    public async Task WriteFileAsync_returns_FileWriteDisabled_when_toggle_is_off()
    {

        PhysicalFileSystemWriter writer = CreateWriter(new ArcanumSettings());

        WorkspaceInfo workspace = MakeWorkspace();

        Result<FileWriteResult> result = await writer.WriteFileAsync(workspace, "any.txt", "hello", CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.FileWriteDisabled", result.Error.Code);

        Assert.False(File.Exists(Path.Combine(_workspace.Root, "any.txt")));

    }

    [Fact]
    public async Task WriteFileAsync_rejects_existing_directory_target()
    {

        _workspace.CreateSubdir("adir");

        PhysicalFileSystemWriter writer = CreateWriter();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<FileWriteResult> result = await writer.WriteFileAsync(workspace, "adir", "hello", CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.PathIsDirectory", result.Error.Code);

    }

    [Fact]
    public async Task ReplaceTextBlockAsync_replaces_single_occurrence()
    {

        _workspace.WriteFile("target.txt", "hello world, hello universe once");

        PhysicalFileSystemWriter writer = CreateWriter();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<TextBlockReplaceResult> result = await writer.ReplaceTextBlockAsync(
            workspace, "target.txt", "universe once", "galaxy", null, CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(1, result.Value!.Replacements);

        Assert.Equal("hello world, hello galaxy", await File.ReadAllTextAsync(Path.Combine(_workspace.Root, "target.txt")));

    }

    [Fact]
    public async Task ReplaceTextBlockAsync_replaces_multiple_occurrences_with_expected_replacements()
    {

        _workspace.WriteFile("target.txt", "foo foo foo");

        PhysicalFileSystemWriter writer = CreateWriter();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<TextBlockReplaceResult> result = await writer.ReplaceTextBlockAsync(
            workspace, "target.txt", "foo", "bar", 3, CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(3, result.Value!.Replacements);

        Assert.Equal("bar bar bar", await File.ReadAllTextAsync(Path.Combine(_workspace.Root, "target.txt")));

    }

    [Fact]
    public async Task ReplaceTextBlockAsync_returns_ReplacementNotFound_when_oldString_absent()
    {

        _workspace.WriteFile("target.txt", "hello world");

        PhysicalFileSystemWriter writer = CreateWriter();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<TextBlockReplaceResult> result = await writer.ReplaceTextBlockAsync(
            workspace, "target.txt", "missing", "replacement", null, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.ReplacementNotFound", result.Error.Code);

    }

    [Fact]
    public async Task ReplaceTextBlockAsync_returns_ReplacementAmbiguous_when_multiple_matches_and_no_expected_replacements()
    {

        _workspace.WriteFile("target.txt", "foo foo");

        PhysicalFileSystemWriter writer = CreateWriter();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<TextBlockReplaceResult> result = await writer.ReplaceTextBlockAsync(
            workspace, "target.txt", "foo", "bar", null, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.ReplacementAmbiguous", result.Error.Code);

    }

    [Fact]
    public async Task ReplaceTextBlockAsync_returns_ReplacementAmbiguous_when_expected_replacements_mismatches_actual_count()
    {

        _workspace.WriteFile("target.txt", "foo foo foo");

        PhysicalFileSystemWriter writer = CreateWriter();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<TextBlockReplaceResult> result = await writer.ReplaceTextBlockAsync(
            workspace, "target.txt", "foo", "bar", 2, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.ReplacementAmbiguous", result.Error.Code);

    }

    [Fact]
    public async Task ReplaceTextBlockAsync_returns_FileNotFound_when_file_does_not_exist()
    {

        PhysicalFileSystemWriter writer = CreateWriter();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<TextBlockReplaceResult> result = await writer.ReplaceTextBlockAsync(
            workspace, "missing.txt", "foo", "bar", null, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.FileNotFound", result.Error.Code);

    }

    [Fact]
    public async Task ReplaceTextBlockAsync_returns_FileNotFound_for_directory_path()
    {

        _workspace.CreateSubdir("adir");

        PhysicalFileSystemWriter writer = CreateWriter();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<TextBlockReplaceResult> result = await writer.ReplaceTextBlockAsync(
            workspace, "adir", "foo", "bar", null, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.FileNotFound", result.Error.Code);

    }

    [Fact]
    public async Task ReplaceTextBlockAsync_rejects_combined_size_exceeding_MaxReplaceTextBlockBytes()
    {

        _workspace.WriteFile("target.txt", "foo");

        ArcanumSettings settings = new()
        {
            Workspaces = new WorkspaceSettings { EnableFileWrite = true, MaxReplaceTextBlockBytes = 1024 },
        };

        PhysicalFileSystemWriter writer = CreateWriter(settings);

        WorkspaceInfo workspace = MakeWorkspace();

        Result<TextBlockReplaceResult> result = await writer.ReplaceTextBlockAsync(
            workspace, "target.txt", "foo", new string('y', 2048), null, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.FileTooLarge", result.Error.Code);

    }

    [Fact]
    public async Task ReplaceTextBlockAsync_write_failure_leaves_no_temp_file_and_original_content_intact()
    {

        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            return;
        }

        string subdir = _workspace.CreateSubdir("readonly-dir");

        string filePath = Path.Combine(subdir, "target.txt");

        await File.WriteAllTextAsync(filePath, "original content");

        UnixFileMode originalMode = File.GetUnixFileMode(subdir);

        File.SetUnixFileMode(subdir, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        try
        {

            PhysicalFileSystemWriter writer = CreateWriter();

            WorkspaceInfo workspace = MakeWorkspace();

            Result<TextBlockReplaceResult> result = await writer.ReplaceTextBlockAsync(
                workspace, "readonly-dir/target.txt", "original", "modified", null, CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal("Workspace.AccessDenied", result.Error.Code);

        }
        finally
        {

            File.SetUnixFileMode(subdir, originalMode);

        }

        Assert.Equal("original content", await File.ReadAllTextAsync(filePath));

        Assert.Empty(Directory.EnumerateFiles(subdir, ".arcanum-*.tmp"));

    }

    [Fact]
    public async Task DeleteAsync_removes_file()
    {

        _workspace.WriteFile("gone.txt", "bye");

        PhysicalFileSystemWriter writer = CreateWriter();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<FileDeleteResult> result = await writer.DeleteAsync(workspace, "gone.txt", recursive: false, CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.False(result.Value!.WasDirectory);

        Assert.False(File.Exists(Path.Combine(_workspace.Root, "gone.txt")));

    }

    [Fact]
    public async Task DeleteAsync_removes_empty_directory()
    {

        string dir = _workspace.CreateSubdir("empty-dir");

        PhysicalFileSystemWriter writer = CreateWriter();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<FileDeleteResult> result = await writer.DeleteAsync(workspace, "empty-dir", recursive: false, CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.True(result.Value!.WasDirectory);

        Assert.False(Directory.Exists(dir));

    }

    [Fact]
    public async Task DeleteAsync_returns_DirectoryNotEmpty_for_non_empty_directory_without_recursive()
    {

        _workspace.WriteFile("non-empty-dir/child.txt", "content");

        PhysicalFileSystemWriter writer = CreateWriter();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<FileDeleteResult> result = await writer.DeleteAsync(workspace, "non-empty-dir", recursive: false, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.DirectoryNotEmpty", result.Error.Code);

    }

    [Fact]
    public async Task DeleteAsync_recursive_removes_non_empty_directory_tree()
    {

        _workspace.WriteFile("tree/a.txt", "a");

        _workspace.WriteFile("tree/nested/b.txt", "b");

        PhysicalFileSystemWriter writer = CreateWriter();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<FileDeleteResult> result = await writer.DeleteAsync(workspace, "tree", recursive: true, CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.False(Directory.Exists(Path.Combine(_workspace.Root, "tree")));

    }

    [Fact]
    public async Task DeleteAsync_recursive_skips_symlinks_that_escape_workspace()
    {

        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            return;
        }

        string outsideDir = Path.Combine(Path.GetTempPath(), $"arcanum-outside-{Guid.NewGuid():N}");

        Directory.CreateDirectory(outsideDir);

        try
        {

            string outsideFile = Path.Combine(outsideDir, "target.txt");

            await File.WriteAllTextAsync(outsideFile, "outside secret");

            string parentDir = _workspace.CreateSubdir("recurseDelete");

            _workspace.WriteFile("recurseDelete/keep.txt", "keep me gone");

            string linkPath = Path.Combine(parentDir, "escape-link");

            File.CreateSymbolicLink(linkPath, outsideFile);

            PhysicalFileSystemWriter writer = CreateWriter();

            WorkspaceInfo workspace = MakeWorkspace();

            Result<FileDeleteResult> result = await writer.DeleteAsync(workspace, "recurseDelete", recursive: true, CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal("Workspace.DeleteFailed", result.Error.Code);

            Assert.True(File.Exists(outsideFile));

            Assert.True(File.Exists(linkPath) || Directory.Exists(linkPath));

            Assert.False(File.Exists(Path.Combine(parentDir, "keep.txt")));

        }
        finally
        {

            Directory.Delete(outsideDir, recursive: true);

        }

    }

    [Fact]
    public async Task DeleteAsync_returns_FileNotFound_when_path_does_not_exist()
    {

        PhysicalFileSystemWriter writer = CreateWriter();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<FileDeleteResult> result = await writer.DeleteAsync(workspace, "missing.txt", recursive: false, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.FileNotFound", result.Error.Code);

    }

    [Fact]
    public async Task CreateDirectoryAsync_creates_nested_directories()
    {

        PhysicalFileSystemWriter writer = CreateWriter();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<DirectoryCreateResult> result = await writer.CreateDirectoryAsync(workspace, "a/b/c", CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.True(Directory.Exists(Path.Combine(_workspace.Root, "a", "b", "c")));

    }

    [Fact]
    public async Task CreateDirectoryAsync_rejects_path_traversal()
    {

        PhysicalFileSystemWriter writer = CreateWriter();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<DirectoryCreateResult> result = await writer.CreateDirectoryAsync(workspace, "../outside-dir", CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.PathTraversal", result.Error.Code);

    }

    [Fact]
    public async Task CreateDirectoryAsync_rejects_existing_file_target()
    {

        _workspace.WriteFile("afile.txt", "content");

        PhysicalFileSystemWriter writer = CreateWriter();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<DirectoryCreateResult> result = await writer.CreateDirectoryAsync(workspace, "afile.txt", CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.PathIsFile", result.Error.Code);

    }

    [Fact]
    public async Task WriteFileAsync_UnauthorizedAccessException_maps_to_AccessDenied()
    {

        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            return;
        }

        string subdir = _workspace.CreateSubdir("locked-dir");

        UnixFileMode originalMode = File.GetUnixFileMode(subdir);

        File.SetUnixFileMode(subdir, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        try
        {

            PhysicalFileSystemWriter writer = CreateWriter();

            WorkspaceInfo workspace = MakeWorkspace();

            Result<FileWriteResult> result = await writer.WriteFileAsync(workspace, "locked-dir/new.txt", "hello", CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal("Workspace.AccessDenied", result.Error.Code);

        }
        finally
        {

            File.SetUnixFileMode(subdir, originalMode);

        }

    }

    private static PhysicalFileSystemWriter CreateWriter() =>
        CreateWriter(new ArcanumSettings { Workspaces = new WorkspaceSettings { EnableFileWrite = true } });

    private static PhysicalFileSystemWriter CreateWriter(ArcanumSettings settings) =>
        new(new TestOptionsSnapshot<ArcanumSettings>(settings));

    private WorkspaceInfo MakeWorkspace() =>
        new("id", "test", _workspace.Root, WorkspaceType.Campaign, DateTimeOffset.UtcNow);

}
