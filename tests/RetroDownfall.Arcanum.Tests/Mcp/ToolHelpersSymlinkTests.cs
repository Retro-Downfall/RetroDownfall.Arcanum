namespace RetroDownfall.Arcanum.Tests.Mcp;

using RetroDownfall.Arcanum.Infrastructure.Mcp;

public sealed class ToolHelpersSymlinkTests : IDisposable
{

    private readonly string _root;

    private readonly List<string> _cleanup = [];

    public ToolHelpersSymlinkTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "arcanum-toolhelpers-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(_root);

        _cleanup.Add(_root);
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

        _cleanup.Add(outside);

        string linkPath = Path.Combine(_root, "escape-link");

        File.CreateSymbolicLink(linkPath, outside);

        string targetFile = Path.Combine(linkPath, "newfile.txt");

        bool allowed = ToolHelpers.IsPathUnderWorkspaceWithSymlinkCheck(_root, targetFile, out _);

        Assert.False(allowed);
    }

    [Fact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_AllowsNormalRelativePath()
    {
        string nestedDir = Path.Combine(_root, "docs");

        Directory.CreateDirectory(nestedDir);

        string targetFile = Path.Combine(nestedDir, "readme.md");

        bool allowed = ToolHelpers.IsPathUnderWorkspaceWithSymlinkCheck(_root, targetFile, out _);

        Assert.True(allowed);
    }

    [Fact]
    public void RevalidatePathBeforeIo_MatchesSymlinkCheck()
    {
        string nestedDir = Path.Combine(_root, "src");

        Directory.CreateDirectory(nestedDir);

        string targetFile = Path.Combine(nestedDir, "Program.cs");

        Assert.True(ToolHelpers.RevalidatePathBeforeIo(_root, targetFile));
    }

    public void Dispose()
    {
        foreach (string path in _cleanup)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch
            {
                // Best-effort temp cleanup.
            }
        }
    }

}
