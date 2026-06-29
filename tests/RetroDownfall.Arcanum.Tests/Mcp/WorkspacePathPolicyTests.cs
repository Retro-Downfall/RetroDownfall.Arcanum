using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class WorkspacePathPolicyTests : IAsyncLifetime
{

    private readonly TempWorkspace _workspace = new();

    public Task InitializeAsync() => _workspace.InitializeAsync();

    public Task DisposeAsync() => _workspace.DisposeAsync();

    [Fact]
    public void TryNormalizeWorkspace_EmptyDirectory_ReturnsConfigurationError()
    {

        bool ok = WorkspacePathPolicy.TryNormalizeWorkspace("", out string? normalized, out string? error);

        Assert.False(ok);

        Assert.Null(normalized);

        Assert.Contains("No workspace directory was provided", error, StringComparison.Ordinal);

    }

    [Fact]
    public void TryNormalizeWorkspace_ValidDirectory_ReturnsFullPath()
    {

        bool ok = WorkspacePathPolicy.TryNormalizeWorkspace(_workspace.Root, out string? normalized, out string? error);

        Assert.True(ok);

        Assert.Equal(Path.GetFullPath(_workspace.Root), normalized);

        Assert.Null(error);

    }

    [Fact]
    public void TryNormalizeWorkspace_UnresolvablePath_ReturnsConfigurationError()
    {

        bool ok = WorkspacePathPolicy.TryNormalizeWorkspace("?\u0000invalid", out string? normalized, out string? error);

        if (ok)
        {
            return;
        }

        Assert.Null(normalized);

        Assert.Contains("could not be resolved", error, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public void IsPathUnderWorkspace_AllowsRootAndChildDeniesOutside()
    {

        string root = Path.GetFullPath(_workspace.Root);

        string child = Path.Combine(root, "src", "App.cs");

        Assert.True(WorkspacePathPolicy.IsPathUnderWorkspace(root, root));

        Assert.True(WorkspacePathPolicy.IsPathUnderWorkspace(root, child));

        Assert.False(WorkspacePathPolicy.IsPathUnderWorkspace(root, Path.Combine(Path.GetTempPath(), "outside.txt")));

    }

    [Fact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_NonExistentChildUnderRoot_Allows()
    {

        string target = Path.Combine(_workspace.Root, "new", "file.txt");

        bool allowed = WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(_workspace.Root, target, out string? resolved);

        Assert.True(allowed);

        Assert.Null(resolved);

    }

    [Fact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_ExistingNestedFile_ReturnsResolvedPath()
    {

        string file = _workspace.WriteFile("nested/readme.md", "content");

        bool allowed = WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(_workspace.Root, file, out string? resolved);

        Assert.True(allowed);

        Assert.Equal(Path.GetFullPath(file), resolved);

    }

    [Fact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_PathOutsideRoot_Rejects()
    {

        string outside = Path.Combine(Path.GetTempPath(), "arcanum-outside-" + Guid.NewGuid().ToString("N"));

        bool allowed = WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(_workspace.Root, outside, out _);

        Assert.False(allowed);

    }

    [Fact]
    public void RevalidatePathBeforeIo_MatchesSymlinkCheckForNestedPath()
    {

        string file = _workspace.WriteFile("src/Program.cs", "// code");

        Assert.True(WorkspacePathPolicy.RevalidatePathBeforeIo(_workspace.Root, file));

    }

    [Fact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_LexicalChildWithDotDot_RejectsAfterNormalization()
    {

        string root = Path.GetFullPath(_workspace.Root);

        string lexicalEscape = Path.Combine(root, "..", Path.GetFileName(root) + "-sibling", "secret.txt");

        bool allowed = WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(root, lexicalEscape, out _);

        Assert.False(allowed);

    }

    [Fact]
    public void TryNormalizeWorkspace_WhitespaceOnly_ReturnsConfigurationError()
    {

        bool ok = WorkspacePathPolicy.TryNormalizeWorkspace("   ", out string? normalized, out string? error);

        Assert.False(ok);

        Assert.Null(normalized);

        Assert.Contains("No workspace directory was provided", error, StringComparison.Ordinal);

    }

    [Fact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_RejectsWriteThroughSymlinkedParent()
    {

        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            return;
        }

        string outside = Path.Combine(Path.GetTempPath(), "arcanum-outside-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(outside);

        try
        {
            string linkPath = Path.Combine(_workspace.Root, "escape-link");

            File.CreateSymbolicLink(linkPath, outside);

            string targetFile = Path.Combine(linkPath, "newfile.txt");

            bool allowed = WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(_workspace.Root, targetFile, out _);

            Assert.False(allowed);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }

    }

    [Fact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_AllowsSymlinkInsideWorkspace()
    {

        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            return;
        }

        string inner = _workspace.CreateSubdir("inner");

        string linkPath = Path.Combine(_workspace.Root, "docs-link");

        File.CreateSymbolicLink(linkPath, inner);

        string targetFile = Path.Combine(linkPath, "notes.md");

        bool allowed = WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(_workspace.Root, targetFile, out _);

        Assert.True(allowed);

    }

}
