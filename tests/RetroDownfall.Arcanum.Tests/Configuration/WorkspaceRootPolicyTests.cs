using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Configuration;

public sealed class WorkspaceRootPolicyTests : IClassFixture<TempWorkspace>
{

    private readonly TempWorkspace _workspace;

    public WorkspaceRootPolicyTests(TempWorkspace workspace)
    {

        _workspace = workspace;

    }

    [Fact]
    public void EnforceAllowedRoots_EmptyAllowlist_DeniesAccess()
    {

        string path = Path.Combine(_workspace.Root, "child");

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

        string child = _workspace.CreateSubdir("allowed-child");

        Result<string> result = WorkspaceRootPolicy.EnforceAllowedRoots(
            child,
            [_workspace.Root],
            "Path.NotAllowed",
            "Path is not allowed.");

        Assert.True(result.IsSuccess);

        Assert.Equal(child, result.Value);

    }

    [Fact]
    public void EnforceAllowedRoots_PathOutsideRoot_Denies()
    {

        string outside = Path.Combine(Path.GetTempPath(), "arcanum-outside-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(outside);

        try
        {

            Result<string> result = WorkspaceRootPolicy.EnforceAllowedRoots(
                outside,
                [_workspace.Root],
                "Path.NotAllowed",
                "Path is not allowed.");

            Assert.True(result.IsFailure);

            Assert.Equal("Path.NotAllowed", result.Error.Code);

        }
        finally
        {

            Directory.Delete(outside, recursive: true);

        }

    }

    [Fact]
    public void IsUnderAnyAllowedRoot_AllowsExactRootMatch()
    {

        bool allowed = WorkspaceRootPolicy.IsUnderAnyAllowedRoot(
            _workspace.Root,
            [_workspace.Root]);

        Assert.True(allowed);

    }

    [Fact]
    public void IsUnderAnyAllowedRoot_AllowsNestedChild()
    {

        string child = _workspace.CreateSubdir("nested");

        bool allowed = WorkspaceRootPolicy.IsUnderAnyAllowedRoot(child, [_workspace.Root]);

        Assert.True(allowed);

    }

    [Fact]
    public void IsUnderAnyAllowedRoot_SkipsBlankRoots()
    {

        string child = _workspace.CreateSubdir("blank-root-child");

        bool allowed = WorkspaceRootPolicy.IsUnderAnyAllowedRoot(child, ["", "   ", _workspace.Root]);

        Assert.True(allowed);

    }

    [Fact]
    public void IsStrictChildPath_SamePath_ReturnsFalse()
    {

        bool isChild = WorkspaceRootPolicy.IsStrictChildPath(_workspace.Root, _workspace.Root);

        Assert.False(isChild);

    }

    [Fact]
    public void IsStrictChildPath_NestedDirectory_ReturnsTrue()
    {

        string child = _workspace.CreateSubdir("strict-child");

        bool isChild = WorkspaceRootPolicy.IsStrictChildPath(_workspace.Root, child);

        Assert.True(isChild);

    }

    [Fact]
    public void IsSamePath_EquivalentPaths_ReturnsTrue()
    {

        string child = _workspace.CreateSubdir("same-path");

        bool same = WorkspaceRootPolicy.IsSamePath(child, child);

        Assert.True(same);

    }

    [Fact]
    public void IsSamePath_DifferentPaths_ReturnsFalse()
    {

        string first = _workspace.CreateSubdir("first");

        string second = _workspace.CreateSubdir("second");

        bool same = WorkspaceRootPolicy.IsSamePath(first, second);

        Assert.False(same);

    }

    [Fact]
    public void EnforceAllowedRoots_SymlinkEscapingRoot_Denies()
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

            Directory.CreateSymbolicLink(linkPath, outside);

            Result<string> result = WorkspaceRootPolicy.EnforceAllowedRoots(
                linkPath,
                [_workspace.Root],
                "Path.NotAllowed",
                "Path is not allowed.");

            Assert.True(result.IsFailure);

            Assert.Equal("Path.NotAllowed", result.Error.Code);

        }
        finally
        {

            Directory.Delete(outside, recursive: true);

        }

    }

    [Fact]
    public void EnforceAllowedRoots_SymlinkOnIntermediateComponent_Denies()
    {

        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {

            return;

        }

        string outside = Path.Combine(Path.GetTempPath(), "arcanum-outside-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(Path.Combine(outside, "secret"));

        try
        {

            string linkPath = Path.Combine(_workspace.Root, "intermediate-link");

            Directory.CreateSymbolicLink(linkPath, outside);

            Result<string> result = WorkspaceRootPolicy.EnforceAllowedRoots(
                Path.Combine(linkPath, "secret"),
                [_workspace.Root],
                "Path.NotAllowed",
                "Path is not allowed.");

            Assert.True(result.IsFailure);

            Assert.Equal("Path.NotAllowed", result.Error.Code);

        }
        finally
        {

            Directory.Delete(outside, recursive: true);

        }

    }

    [Fact]
    public void IsStrictChildPath_SymlinkOnIntermediateComponent_ReturnsFalse()
    {

        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {

            return;

        }

        string outside = Path.Combine(Path.GetTempPath(), "arcanum-outside-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(Path.Combine(outside, "spells"));

        try
        {

            string linkPath = Path.Combine(_workspace.Root, "strict-intermediate-link");

            Directory.CreateSymbolicLink(linkPath, outside);

            bool isChild = WorkspaceRootPolicy.IsStrictChildPath(
                _workspace.Root,
                Path.Combine(linkPath, "spells"));

            Assert.False(isChild);

        }
        finally
        {

            Directory.Delete(outside, recursive: true);

        }

    }

    [Fact]
    public void IsUnderAnyAllowedRoot_SymlinkOnIntermediateComponentPointingBackInside_Allows()
    {

        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {

            return;

        }

        string realDir = _workspace.CreateSubdir("intermediate-real-target");

        Directory.CreateDirectory(Path.Combine(realDir, "nested"));

        string linkPath = Path.Combine(_workspace.Root, "inside-intermediate-link");

        Directory.CreateSymbolicLink(linkPath, realDir);

        bool allowed = WorkspaceRootPolicy.IsUnderAnyAllowedRoot(
            Path.Combine(linkPath, "nested"),
            [_workspace.Root]);

        Assert.True(allowed);

    }

    [Fact]
    public void IsUnderAnyAllowedRoot_NonExistentDescendant_Allows()
    {

        string candidate = Path.Combine(_workspace.Root, "not-created-yet", "deeper");

        bool allowed = WorkspaceRootPolicy.IsUnderAnyAllowedRoot(candidate, [_workspace.Root]);

        Assert.True(allowed);

    }

    [Fact]
    public void EnforceAllowedRoots_SymlinkInsideRoot_Allows()
    {

        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {

            return;

        }

        string realDir = _workspace.CreateSubdir("real-target");

        string linkPath = Path.Combine(_workspace.Root, "inside-link");

        Directory.CreateSymbolicLink(linkPath, realDir);

        Result<string> result = WorkspaceRootPolicy.EnforceAllowedRoots(
            linkPath,
            [_workspace.Root],
            "Path.NotAllowed",
            "Path is not allowed.");

        Assert.True(result.IsSuccess);

    }

}
