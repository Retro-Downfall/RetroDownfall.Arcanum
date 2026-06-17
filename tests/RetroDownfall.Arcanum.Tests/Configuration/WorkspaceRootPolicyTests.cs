using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Configuration;

public sealed class WorkspaceRootPolicyTests
{

    [Fact]
    public void EnforceAllowedRoots_EmptyAllowlist_DeniesAccess()
    {

        string path = Path.GetFullPath("/tmp/arcanum-test");

        Result<string> result = WorkspaceRootPolicy.EnforceAllowedRoots(
            path,
            [],
            "Path.NotAllowed",
            "Path is not allowed.");

        Assert.True(result.IsFailure);

        Assert.Equal("Path.NotAllowed", result.Error.Code);

    }

    [Fact]
    public void EnforceAllowedRoots_NonEmptyAllowlist_AllowsUnderRoot()
    {

        string root = Path.GetFullPath("/tmp");

        string child = Path.Combine(root, "arcanum-child");

        Result<string> result = WorkspaceRootPolicy.EnforceAllowedRoots(
            child,
            [root],
            "Path.NotAllowed",
            "Path is not allowed.");

        Assert.True(result.IsSuccess);

        Assert.Equal(child, result.Value);

    }

}
