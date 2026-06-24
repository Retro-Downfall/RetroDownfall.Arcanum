namespace RetroDownfall.Arcanum.Tests.Mcp;

using RetroDownfall.Arcanum.Infrastructure.Mcp;
using Xunit;

[Collection("ToolHelpers")]
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
    public void IsPathUnderWorkspace_CandidateEqualsRoot_Allows()
    {
        string root = Path.GetFullPath(_root);

        Assert.True(ToolHelpers.IsPathUnderWorkspace(root, root));
    }

    [SkippableFact]
    public void IsPathUnderWorkspace_OnWindows_IgnoresDirectoryNameCase()
    {
        Skip.IfNot(OperatingSystem.IsWindows());

        string root = Path.Combine(_root, "CaseDir");

        Directory.CreateDirectory(root);

        string child = Path.Combine(root.ToUpperInvariant(), "file.txt");

        Assert.True(ToolHelpers.IsPathUnderWorkspace(root, child));
    }

    [SkippableFact]
    public void IsPathUnderWorkspace_OnNonWindows_WithTestSeam_IgnoresDirectoryNameCase()
    {
        Skip.If(OperatingSystem.IsWindows());

        ToolHelpers.SetUseOrdinalIgnoreCasePathComparisonForTests(true);

        try
        {
            string root = Path.Combine(_root, "CaseDir");

            Directory.CreateDirectory(root);

            string child = Path.Combine(root.ToUpperInvariant(), "file.txt");

            Assert.True(ToolHelpers.IsPathUnderWorkspace(root, child));
        }
        finally
        {
            ToolHelpers.SetUseOrdinalIgnoreCasePathComparisonForTests(false);
        }
    }

    [SkippableFact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_OnNonWindows_WithTestSeam_IgnoresDirectoryNameCase()
    {
        Skip.If(OperatingSystem.IsWindows());

        ToolHelpers.SetUseOrdinalIgnoreCasePathComparisonForTests(true);

        try
        {
            string root = Path.GetFullPath(Path.Combine(_root, "CaseRoot"));

            Directory.CreateDirectory(root);

            string nestedDir = Path.Combine(root, "nested");

            Directory.CreateDirectory(nestedDir);

            string child = Path.Combine(root.ToUpperInvariant(), "nested", "file.txt");

            File.WriteAllText(child, "ok");

            bool allowed = ToolHelpers.IsPathUnderWorkspaceWithSymlinkCheck(root, child, out string? resolved);

            Assert.True(allowed);

            Assert.Equal(Path.GetFullPath(child), resolved);
        }
        finally
        {
            ToolHelpers.SetUseOrdinalIgnoreCasePathComparisonForTests(false);
        }
    }

    [Fact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_WorkspaceRoot_ReturnsResolvedRoot()
    {
        string root = Path.GetFullPath(_root);

        bool allowed = ToolHelpers.IsPathUnderWorkspaceWithSymlinkCheck(root, root, out string? resolved);

        Assert.True(allowed);

        Assert.Equal(root, resolved);
    }

    [Fact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_ExistingRegularFile_ReturnsResolvedPath()
    {
        string file = Path.Combine(_root, "plain.txt");

        File.WriteAllText(file, "ok");

        bool allowed = ToolHelpers.IsPathUnderWorkspaceWithSymlinkCheck(_root, file, out string? resolved);

        Assert.True(allowed);

        Assert.Equal(Path.GetFullPath(file), resolved);
    }

    [Fact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_ExistingFileSymlinkOutsideRoot_Rejects()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            return;
        }

        string outside = Path.Combine(Path.GetTempPath(), "arcanum-outside-" + Guid.NewGuid().ToString("N"));

        File.WriteAllText(outside, "secret");

        _cleanup.Add(outside);

        try
        {
            string linkPath = Path.Combine(_root, "outside-file-link");

            File.CreateSymbolicLink(linkPath, outside);

            bool allowed = ToolHelpers.IsPathUnderWorkspaceWithSymlinkCheck(_root, linkPath, out _);

            Assert.False(allowed);
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_ExistingFileSymlinkInsideRoot_Allows()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            return;
        }

        string innerFile = Path.Combine(_root, "inner.txt");

        File.WriteAllText(innerFile, "ok");

        string linkPath = Path.Combine(_root, "inner-link");

        File.CreateSymbolicLink(linkPath, innerFile);

        bool allowed = ToolHelpers.IsPathUnderWorkspaceWithSymlinkCheck(_root, linkPath, out string? resolved);

        Assert.True(allowed);

        Assert.Equal(Path.GetFullPath(innerFile), resolved);
    }

    [Fact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_ExistingDirectorySymlinkOutsideRoot_Rejects()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            return;
        }

        string outside = Path.Combine(Path.GetTempPath(), "arcanum-outside-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(outside);

        _cleanup.Add(outside);

        string linkPath = Path.Combine(_root, "outside-dir-link");

        Directory.CreateSymbolicLink(linkPath, outside);

        string targetFile = Path.Combine(linkPath, "child.txt");

        bool allowed = ToolHelpers.IsPathUnderWorkspaceWithSymlinkCheck(_root, targetFile, out _);

        Assert.False(allowed);
    }

    [Fact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_LexicalDotDotSegment_RejectsAfterFullPathNormalization()
    {
        string root = Path.GetFullPath(_root);

        string sibling = Path.Combine(Path.GetDirectoryName(root)!, Path.GetFileName(root) + "-peer");

        Directory.CreateDirectory(sibling);

        _cleanup.Add(sibling);

        string lexical = Path.Combine(root, "..", Path.GetFileName(root) + "-peer", "secret.txt");

        bool allowed = ToolHelpers.IsPathUnderWorkspaceWithSymlinkCheck(root, lexical, out _);

        Assert.False(allowed);
    }

    [Fact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_ExistingDirectorySymlinkInsideRoot_ResolvesTarget()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            return;
        }

        string inner = Path.Combine(_root, "inner-dir");

        Directory.CreateDirectory(inner);

        string linkPath = Path.Combine(_root, "dir-link");

        Directory.CreateSymbolicLink(linkPath, inner);

        string targetFile = Path.Combine(linkPath, "notes.txt");

        bool allowed = ToolHelpers.IsPathUnderWorkspaceWithSymlinkCheck(_root, targetFile, out _);

        Assert.True(allowed);
    }

    [Fact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_CandidateIsExistingDirectorySymlink_ResolvesLeafTarget()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            return;
        }

        string inner = Path.Combine(_root, "inner-target-dir");

        Directory.CreateDirectory(inner);

        string linkPath = Path.Combine(_root, "dir-symlink-leaf");

        Directory.CreateSymbolicLink(linkPath, inner);

        bool allowed = ToolHelpers.IsPathUnderWorkspaceWithSymlinkCheck(_root, linkPath, out string? resolved);

        Assert.True(allowed);

        Assert.Equal(Path.GetFullPath(inner), resolved);
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
    public void IsPathUnderWorkspaceWithSymlinkCheck_IntermediateDirectorySymlink_UpdatesWalkTarget()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            return;
        }

        string realDir = Path.Combine(_root, "real-dir");

        Directory.CreateDirectory(realDir);

        string linkDir = Path.Combine(_root, "link-dir");

        Directory.CreateSymbolicLink(linkDir, realDir);

        string targetFile = Path.Combine(linkDir, "inside.txt");

        File.WriteAllText(Path.Combine(realDir, "inside.txt"), "ok");

        bool allowed = ToolHelpers.IsPathUnderWorkspaceWithSymlinkCheck(_root, targetFile, out string? resolved);

        Assert.True(allowed);

        Assert.Equal(Path.GetFullPath(targetFile), resolved);
    }

    [Fact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_CandidateFileSymlinkInsideRoot_ResolvesFinalTarget()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            return;
        }

        string realFile = Path.Combine(_root, "real.txt");

        File.WriteAllText(realFile, "ok");

        string linkFile = Path.Combine(_root, "link.txt");

        File.CreateSymbolicLink(linkFile, realFile);

        bool allowed = ToolHelpers.IsPathUnderWorkspaceWithSymlinkCheck(_root, linkFile, out string? resolved);

        Assert.True(allowed);

        Assert.Equal(Path.GetFullPath(realFile), resolved);
    }

    [Fact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_RelativePathEscapeViaTestSeam_Rejects()
    {

        string root = Path.GetFullPath(_root);

        string nested = Path.Combine(root, "nested");

        Directory.CreateDirectory(nested);

        try
        {

            ToolHelpers.SetRelativePathResolverForTests((_, _) => "../outside");

            bool allowed = ToolHelpers.IsPathUnderWorkspaceWithSymlinkCheck(root, nested, out _);

            Assert.False(allowed);

        }
        finally
        {

            ToolHelpers.SetRelativePathResolverForTests(null);

        }

    }

    [Fact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_RootedRelativePathViaTestSeam_Rejects()
    {

        string root = Path.GetFullPath(_root);

        string nested = Path.Combine(root, "nested");

        Directory.CreateDirectory(nested);

        try
        {

            ToolHelpers.SetRelativePathResolverForTests((_, _) => "/absolute/outside");

            bool allowed = ToolHelpers.IsPathUnderWorkspaceWithSymlinkCheck(root, nested, out _);

            Assert.False(allowed);

        }
        finally
        {

            ToolHelpers.SetRelativePathResolverForTests(null);

        }

    }

    [Fact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_IntermediateFileSymlink_ResolvesInsideRoot()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            return;
        }

        string realFile = Path.Combine(_root, "real.txt");

        File.WriteAllText(realFile, "ok");

        string sub = Path.Combine(_root, "sub");

        Directory.CreateDirectory(sub);

        string linkInSub = Path.Combine(sub, "lnk");

        File.CreateSymbolicLink(linkInSub, realFile);

        string target = Path.Combine(sub, "lnk");

        bool allowed = ToolHelpers.IsPathUnderWorkspaceWithSymlinkCheck(_root, target, out string? resolved);

        Assert.True(allowed);

        Assert.Equal(Path.GetFullPath(realFile), resolved);

    }

    [Fact]
    public void TryResolveFinalSymlinkTargetForCoverageTest_NonExistentPath_ReturnsTrue()
    {

        ToolHelpers.SetSymlinkResolverForTests(null);

        string missing = Path.Combine(_root, "ghost-path");

        bool ok = ToolHelpers.TryResolveFinalSymlinkTargetForCoverageTest(missing, out string? resolved);

        Assert.True(ok);

        Assert.Null(resolved);

    }

    [Fact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_LeafResolveFailureViaTestSeam_Rejects()
    {

        string root = Path.GetFullPath(_root);

        string file = Path.Combine(root, "leaf-fail.txt");

        File.WriteAllText(file, "x");

        int calls = 0;

        try
        {

            ToolHelpers.SetSymlinkResolverForTests(_ =>
            {
                calls++;

                return calls == 1 ? (true, null) : (false, null);
            });

            bool allowed = ToolHelpers.IsPathUnderWorkspaceWithSymlinkCheck(root, file, out _);

            Assert.False(allowed);

            Assert.True(calls >= 2);

        }
        finally
        {

            ToolHelpers.SetSymlinkResolverForTests(null);

        }

    }

    [Fact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_ResolveSeam_CoversNullAndNonNullTargets()
    {

        string root = Path.GetFullPath(_root);

        string file = Path.Combine(root, "combo.txt");

        File.WriteAllText(file, "ok");

        string redirected = Path.Combine(root, "combo-redirect.txt");

        File.WriteAllText(redirected, "ok");

        try
        {

            ToolHelpers.SetSymlinkResolverForTests(_ => (true, null));

            Assert.True(ToolHelpers.IsPathUnderWorkspaceWithSymlinkCheck(root, file, out string? nullTarget));

            Assert.Equal(Path.GetFullPath(file), nullTarget);

            ToolHelpers.SetSymlinkResolverForTests(path =>
                string.Equals(path, file, StringComparison.Ordinal)
                    ? (true, redirected)
                    : (true, null));

            Assert.True(ToolHelpers.IsPathUnderWorkspaceWithSymlinkCheck(root, file, out string? nonNullTarget));

            Assert.Equal(redirected, nonNullTarget);

        }
        finally
        {

            ToolHelpers.SetSymlinkResolverForTests(null);

        }

    }

    [Fact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_RealDirectorySymlink_UsesNativeResolver()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            return;
        }

        ToolHelpers.SetSymlinkResolverForTests(null);

        string inner = Path.Combine(_root, "native-inner");

        Directory.CreateDirectory(inner);

        string linkPath = Path.Combine(_root, "native-link");

        Directory.CreateSymbolicLink(linkPath, inner);

        bool allowed = ToolHelpers.IsPathUnderWorkspaceWithSymlinkCheck(_root, linkPath, out string? resolved);

        Assert.True(allowed);

        Assert.Equal(Path.GetFullPath(inner), resolved);

    }

    [Fact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_ResolveNonNullTargetViaTestSeam_UsesResolvedTarget()
    {

        string root = Path.GetFullPath(_root);

        string file = Path.Combine(root, "plain.txt");

        File.WriteAllText(file, "ok");

        string redirected = Path.Combine(root, "redirected.txt");

        File.WriteAllText(redirected, "ok");

        try
        {

            ToolHelpers.SetSymlinkResolverForTests(path =>
                string.Equals(path, file, StringComparison.Ordinal)
                    ? (true, redirected)
                    : (true, null));

            bool allowed = ToolHelpers.IsPathUnderWorkspaceWithSymlinkCheck(root, file, out string? resolved);

            Assert.True(allowed);

            Assert.Equal(redirected, resolved);

        }
        finally
        {

            ToolHelpers.SetSymlinkResolverForTests(null);

        }

    }

    [Fact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_ResolveNullTargetViaTestSeam_UsesCandidatePath()
    {

        string root = Path.GetFullPath(_root);

        string file = Path.Combine(root, "plain.txt");

        File.WriteAllText(file, "ok");

        try
        {

            ToolHelpers.SetSymlinkResolverForTests(_ => (true, null));

            bool allowed = ToolHelpers.IsPathUnderWorkspaceWithSymlinkCheck(root, file, out string? resolved);

            Assert.True(allowed);

            Assert.Equal(Path.GetFullPath(file), resolved);

        }
        finally
        {

            ToolHelpers.SetSymlinkResolverForTests(null);

        }

    }

    [Fact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_ResolveFailureViaTestSeam_Rejects()
    {

        string root = Path.GetFullPath(_root);

        string nested = Path.Combine(root, "nested");

        Directory.CreateDirectory(nested);

        try
        {

            ToolHelpers.SetSymlinkResolverForTests(_ => (false, null));

            bool allowed = ToolHelpers.IsPathUnderWorkspaceWithSymlinkCheck(root, nested, out _);

            Assert.False(allowed);

        }
        finally
        {

            ToolHelpers.SetSymlinkResolverForTests(null);

        }

    }

    [Fact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_ResolveTargetViaTestSeam_UpdatesWalk()
    {

        string root = Path.GetFullPath(_root);

        string nested = Path.Combine(root, "nested");

        Directory.CreateDirectory(nested);

        string redirected = Path.Combine(root, "redirected");

        Directory.CreateDirectory(redirected);

        try
        {

            ToolHelpers.SetSymlinkResolverForTests(path =>
                string.Equals(path, nested, StringComparison.Ordinal)
                    ? (true, redirected)
                    : (true, null));

            bool allowed = ToolHelpers.IsPathUnderWorkspaceWithSymlinkCheck(root, nested, out string? resolved);

            Assert.True(allowed);

            Assert.Equal(redirected, resolved);

        }
        finally
        {

            ToolHelpers.SetSymlinkResolverForTests(null);

        }

    }

    [Fact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_PathThroughIntermediateFileSymlink_Allows()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            return;
        }

        string realFile = Path.Combine(_root, "real.txt");

        File.WriteAllText(realFile, "ok");

        string sub = Path.Combine(_root, "sub");

        Directory.CreateDirectory(sub);

        string linkInSub = Path.Combine(sub, "lnk");

        File.CreateSymbolicLink(linkInSub, realFile);

        string target = Path.Combine(sub, "lnk", "nested.txt");

        bool allowed = ToolHelpers.IsPathUnderWorkspaceWithSymlinkCheck(_root, target, out _);

        Assert.True(allowed);

    }

    [Fact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_ExistingDirectoryLeaf_ResolvesToCandidate()
    {

        string leafDir = Path.Combine(_root, "leaf-dir");

        Directory.CreateDirectory(leafDir);

        bool allowed = ToolHelpers.IsPathUnderWorkspaceWithSymlinkCheck(_root, leafDir, out string? resolved);

        Assert.True(allowed);

        Assert.Equal(Path.GetFullPath(leafDir), resolved);

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

        ToolHelpers.SetUseOrdinalIgnoreCasePathComparisonForTests(false);

        ToolHelpers.SetRelativePathResolverForTests(null);

        ToolHelpers.SetSymlinkResolverForTests(null);

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
