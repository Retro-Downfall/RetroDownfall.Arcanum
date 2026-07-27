using RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class WorkspaceRootPathTests
{
    [Fact]
    public void Containment_accepts_root_and_descendants_only()
    {
        string parent = Path.GetFullPath(
            Path.Combine(
                Path.GetTempPath(),
                "arcanum-workspace-root-tests"));
        string root = Path.Combine(parent, "workspace");

        Assert.True(WorkspaceRootPath.IsWithinOrEqual(root, root));
        Assert.True(
            WorkspaceRootPath.IsWithinOrEqual(
                Path.Combine(root, "src", "file.cs"),
                root));
        Assert.False(
            WorkspaceRootPath.IsWithinOrEqual(
                parent,
                root));
        Assert.False(
            WorkspaceRootPath.IsWithinOrEqual(
                Path.Combine(parent, "workspace-sibling"),
                root));
    }
}
