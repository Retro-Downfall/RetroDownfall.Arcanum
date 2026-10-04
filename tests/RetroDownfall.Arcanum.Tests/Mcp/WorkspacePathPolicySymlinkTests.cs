namespace RetroDownfall.Arcanum.Tests.Mcp;

using RetroDownfall.Arcanum.Infrastructure.Security;
using Xunit;

[Collection("WorkspacePathPolicy")]
public sealed class WorkspacePathPolicySymlinkTests : IDisposable
{
    private readonly string _root;

    private readonly List<string> _cleanup = [];

    public WorkspacePathPolicySymlinkTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "arcanum-toolhelpers-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(_root);

        _cleanup.Add(_root);
    }

    [Fact]
    public void IsPathUnderWorkspace_CandidateEqualsRoot_Allows()
    {
        string root = Path.GetFullPath(_root);

        Assert.True(WorkspacePathPolicy.IsPathUnderWorkspace(root, root));
    }

    [SkippableFact]
    public void IsPathUnderWorkspace_OnWindows_IgnoresDirectoryNameCase()
    {
        Skip.IfNot(OperatingSystem.IsWindows());

        string root = Path.Combine(_root, "CaseDir");

        Directory.CreateDirectory(root);

        string child = Path.Combine(root.ToUpperInvariant(), "file.txt");

        Assert.True(WorkspacePathPolicy.IsPathUnderWorkspace(root, child));
    }

    [SkippableFact]
    public void IsPathUnderWorkspace_OnNonWindows_WithTestSeam_IgnoresDirectoryNameCase()
    {
        Skip.If(OperatingSystem.IsWindows());

        WorkspacePathPolicy.SetUseOrdinalIgnoreCasePathComparisonForTests(true);

        try
        {
            string root = Path.Combine(_root, "CaseDir");

            Directory.CreateDirectory(root);

            string child = Path.Combine(root.ToUpperInvariant(), "file.txt");

            Assert.True(WorkspacePathPolicy.IsPathUnderWorkspace(root, child));
        }
        finally
        {
            WorkspacePathPolicy.SetUseOrdinalIgnoreCasePathComparisonForTests(false);
        }
    }

    [SkippableFact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_OnNonWindows_WithTestSeam_IgnoresDirectoryNameCase()
    {
        Skip.If(OperatingSystem.IsWindows());

        WorkspacePathPolicy.SetUseOrdinalIgnoreCasePathComparisonForTests(true);

        try
        {
            string root = Path.GetFullPath(Path.Combine(_root, "CaseRoot"));

            Directory.CreateDirectory(root);

            string nestedDir = Path.Combine(root, "nested");

            Directory.CreateDirectory(nestedDir);

            // Write at the real casing — Linux volumes are case-sensitive, so an uppercased
            // path would DirectoryNotFoundException before the policy is exercised.
            string realChild = Path.Combine(nestedDir, "file.txt");

            File.WriteAllText(realChild, "ok");

            string child = Path.Combine(root.ToUpperInvariant(), "nested", "file.txt");

            bool allowed = WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(root, child, out string? resolved);

            Assert.True(allowed);

            // The part below the root is walked from the root as spelled, so the entry is found on
            // case-sensitive volumes too; it is not a link, so the candidate comes back as given.
            Assert.Equal(Path.GetFullPath(child), resolved);
        }
        finally
        {
            WorkspacePathPolicy.ResetTestSeams();
        }
    }

    [Fact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_WorkspaceRoot_ReturnsResolvedRoot()
    {
        string root = Path.GetFullPath(_root);

        bool allowed = WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(root, root, out string? resolved);

        Assert.True(allowed);

        Assert.Equal(root, resolved);
    }

    [Fact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_ExistingRegularFile_ReturnsResolvedPath()
    {
        string file = Path.Combine(_root, "plain.txt");

        File.WriteAllText(file, "ok");

        bool allowed = WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(_root, file, out string? resolved);

        Assert.True(allowed);

        Assert.Equal(Path.GetFullPath(file), resolved);
    }

    [SkippableFact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_ExistingFileSymlinkOutsideRoot_Rejects()
    {
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "This asserts POSIX behaviour and runs on macOS and Linux only.");

        string outside = Path.Combine(Path.GetTempPath(), "arcanum-outside-" + Guid.NewGuid().ToString("N"));

        File.WriteAllText(outside, "secret");

        _cleanup.Add(outside);

        try
        {
            string linkPath = Path.Combine(_root, "outside-file-link");

            File.CreateSymbolicLink(linkPath, outside);

            bool allowed = WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(_root, linkPath, out _);

            Assert.False(allowed);
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [SkippableFact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_ExistingFileSymlinkInsideRoot_Allows()
    {
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "This asserts POSIX behaviour and runs on macOS and Linux only.");

        string innerFile = Path.Combine(_root, "inner.txt");

        File.WriteAllText(innerFile, "ok");

        string linkPath = Path.Combine(_root, "inner-link");

        File.CreateSymbolicLink(linkPath, innerFile);

        bool allowed = WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(_root, linkPath, out string? resolved);

        Assert.True(allowed);

        Assert.Equal(Path.GetFullPath(innerFile), resolved);
    }

    [SkippableFact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_ExistingDirectorySymlinkOutsideRoot_Rejects()
    {
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "This asserts POSIX behaviour and runs on macOS and Linux only.");

        string outside = Path.Combine(Path.GetTempPath(), "arcanum-outside-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(outside);

        _cleanup.Add(outside);

        string linkPath = Path.Combine(_root, "outside-dir-link");

        Directory.CreateSymbolicLink(linkPath, outside);

        string targetFile = Path.Combine(linkPath, "child.txt");

        bool allowed = WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(_root, targetFile, out _);

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

        bool allowed = WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(root, lexical, out _);

        Assert.False(allowed);
    }

    [SkippableFact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_ExistingDirectorySymlinkInsideRoot_ResolvesTarget()
    {
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "This asserts POSIX behaviour and runs on macOS and Linux only.");

        string inner = Path.Combine(_root, "inner-dir");

        Directory.CreateDirectory(inner);

        string linkPath = Path.Combine(_root, "dir-link");

        Directory.CreateSymbolicLink(linkPath, inner);

        string targetFile = Path.Combine(linkPath, "notes.txt");

        bool allowed = WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(_root, targetFile, out _);

        Assert.True(allowed);
    }

    [SkippableFact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_CandidateIsExistingDirectorySymlink_ResolvesLeafTarget()
    {
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "This asserts POSIX behaviour and runs on macOS and Linux only.");

        string inner = Path.Combine(_root, "inner-target-dir");

        Directory.CreateDirectory(inner);

        string linkPath = Path.Combine(_root, "dir-symlink-leaf");

        Directory.CreateSymbolicLink(linkPath, inner);

        bool allowed = WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(_root, linkPath, out string? resolved);

        Assert.True(allowed);

        Assert.Equal(Path.GetFullPath(inner), resolved);
    }

    [SkippableFact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_RejectsWriteThroughSymlinkedParent()
    {
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "This asserts POSIX behaviour and runs on macOS and Linux only.");

        string outside = Path.Combine(Path.GetTempPath(), "arcanum-outside-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(outside);

        _cleanup.Add(outside);

        string linkPath = Path.Combine(_root, "escape-link");

        File.CreateSymbolicLink(linkPath, outside);

        string targetFile = Path.Combine(linkPath, "newfile.txt");

        bool allowed = WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(_root, targetFile, out _);

        Assert.False(allowed);
    }

    [Fact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_AllowsNormalRelativePath()
    {
        string nestedDir = Path.Combine(_root, "docs");

        Directory.CreateDirectory(nestedDir);

        string targetFile = Path.Combine(nestedDir, "readme.md");

        bool allowed = WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(_root, targetFile, out _);

        Assert.True(allowed);
    }

    [SkippableFact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_IntermediateDirectorySymlink_UpdatesWalkTarget()
    {
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "This asserts POSIX behaviour and runs on macOS and Linux only.");

        string realDir = Path.Combine(_root, "real-dir");

        Directory.CreateDirectory(realDir);

        string linkDir = Path.Combine(_root, "link-dir");

        Directory.CreateSymbolicLink(linkDir, realDir);

        string targetFile = Path.Combine(linkDir, "inside.txt");

        File.WriteAllText(Path.Combine(realDir, "inside.txt"), "ok");

        bool allowed = WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(_root, targetFile, out string? resolved);

        Assert.True(allowed);

        Assert.Equal(Path.GetFullPath(targetFile), resolved);
    }

    [SkippableFact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_CandidateFileSymlinkInsideRoot_ResolvesFinalTarget()
    {
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "This asserts POSIX behaviour and runs on macOS and Linux only.");

        string realFile = Path.Combine(_root, "real.txt");

        File.WriteAllText(realFile, "ok");

        string linkFile = Path.Combine(_root, "link.txt");

        File.CreateSymbolicLink(linkFile, realFile);

        bool allowed = WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(_root, linkFile, out string? resolved);

        Assert.True(allowed);

        Assert.Equal(Path.GetFullPath(realFile), resolved);
    }

    [SkippableFact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_IntermediateFileSymlink_ResolvesInsideRoot()
    {
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "This asserts POSIX behaviour and runs on macOS and Linux only.");

        string realFile = Path.Combine(_root, "real.txt");

        File.WriteAllText(realFile, "ok");

        string sub = Path.Combine(_root, "sub");

        Directory.CreateDirectory(sub);

        string linkInSub = Path.Combine(sub, "lnk");

        File.CreateSymbolicLink(linkInSub, realFile);

        string target = Path.Combine(sub, "lnk");

        bool allowed = WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(_root, target, out string? resolved);

        Assert.True(allowed);

        Assert.Equal(Path.GetFullPath(realFile), resolved);
    }

    [SkippableFact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_RealDirectorySymlink_UsesNativeResolver()
    {
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "This asserts POSIX behaviour and runs on macOS and Linux only.");

        string inner = Path.Combine(_root, "native-inner");

        Directory.CreateDirectory(inner);

        string linkPath = Path.Combine(_root, "native-link");

        Directory.CreateSymbolicLink(linkPath, inner);

        bool allowed = WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(_root, linkPath, out string? resolved);

        Assert.True(allowed);

        Assert.Equal(Path.GetFullPath(inner), resolved);
    }

    [SkippableFact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_PathThroughIntermediateFileSymlink_Allows()
    {
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "This asserts POSIX behaviour and runs on macOS and Linux only.");

        string realFile = Path.Combine(_root, "real.txt");

        File.WriteAllText(realFile, "ok");

        string sub = Path.Combine(_root, "sub");

        Directory.CreateDirectory(sub);

        string linkInSub = Path.Combine(sub, "lnk");

        File.CreateSymbolicLink(linkInSub, realFile);

        string target = Path.Combine(sub, "lnk", "nested.txt");

        bool allowed = WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(_root, target, out _);

        Assert.True(allowed);
    }

    [Fact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_ExistingDirectoryLeaf_ResolvesToCandidate()
    {
        string leafDir = Path.Combine(_root, "leaf-dir");

        Directory.CreateDirectory(leafDir);

        bool allowed = WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(_root, leafDir, out string? resolved);

        Assert.True(allowed);

        Assert.Equal(Path.GetFullPath(leafDir), resolved);
    }

    /// <summary>
    /// R-007: <c>ws/b -> ../outside</c> escapes, and <c>ws/d -> b/sub</c> is a second link whose target is
    /// spelled through the first. Resolving <c>d</c> yields the string <c>ws/b/sub</c>, which sits lexically
    /// under the root, so a walk that splices that string in without re-walking it never visits <c>b</c>.
    /// </summary>
    [SkippableFact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_LinkTargetPassesThroughEscapingDirectorySymlink_Rejects()
    {
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "This asserts POSIX behaviour and runs on macOS and Linux only.");

        string workspace = CreateChainedLinkFixture(out _);

        Directory.CreateSymbolicLink(Path.Combine(workspace, "d"), Path.Combine("b", "sub"));

        Assert.False(WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
            workspace,
            Path.Combine(workspace, "d", "file"),
            out _));

        Assert.False(WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
            workspace,
            Path.Combine(workspace, "d"),
            out _));
    }

    /// <summary>
    /// Control for R-007: a pure link-to-link chain (<c>chain -> b</c>, <c>b -> ../outside</c>) was already
    /// rejected before the canonicalising rewrite and must stay rejected.
    /// </summary>
    [SkippableFact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_PureLinkChain_Rejects()
    {
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "This asserts POSIX behaviour and runs on macOS and Linux only.");

        string workspace = CreateChainedLinkFixture(out _);

        Directory.CreateSymbolicLink(Path.Combine(workspace, "chain"), "b");

        Assert.False(WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
            workspace,
            Path.Combine(workspace, "chain", "sub", "file"),
            out _));

        Assert.False(WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
            workspace,
            Path.Combine(workspace, "chain"),
            out _));
    }

    /// <summary>
    /// The workspace root is canonicalised the same way as the candidate, so a root spelled through the
    /// macOS <c>/var -> /private/var</c> alias still contains an in-workspace link whose absolute target is
    /// spelled through the other side of that alias.
    /// </summary>
    [SkippableFact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_RootSpelledThroughSystemAlias_AllowsLinkSpelledThroughTarget()
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "The /var -> /private/var alias exists on macOS only.");

        string workspace = Path.Combine(_root, "alias-ws");

        Directory.CreateDirectory(Path.Combine(workspace, "real"));

        File.WriteAllText(Path.Combine(workspace, "real", "notes.txt"), "ok");

        string aliasSpelledRoot = Path.GetFullPath(workspace);

        string targetSpelledRoot = NoFollowPathTopology.NormalizeMacOsSystemAlias(aliasSpelledRoot);

        Skip.If(
            string.Equals(aliasSpelledRoot, targetSpelledRoot, StringComparison.Ordinal),
            "The temp directory is not spelled through a macOS system alias on this host.");

        Directory.CreateSymbolicLink(
            Path.Combine(workspace, "abs-link"),
            Path.Combine(targetSpelledRoot, "real"));

        string candidate = Path.Combine(aliasSpelledRoot, "abs-link", "notes.txt");

        Assert.True(WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(aliasSpelledRoot, candidate, out _));

        // A leaf link's resolved path is re-expressed under the root as the caller spelled it, so
        // workspace-relative names computed from it keep working.
        Assert.True(WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
            aliasSpelledRoot,
            Path.Combine(aliasSpelledRoot, "abs-link"),
            out string? resolved));

        Assert.Equal(Path.Combine(aliasSpelledRoot, "real"), resolved);
    }

    [SkippableFact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_LinkLoop_Rejects()
    {
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "This asserts POSIX behaviour and runs on macOS and Linux only.");

        File.CreateSymbolicLink(Path.Combine(_root, "loop-a"), "loop-b");

        File.CreateSymbolicLink(Path.Combine(_root, "loop-b"), "loop-a");

        Assert.False(WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
            _root,
            Path.Combine(_root, "loop-a", "file.txt"),
            out _));

        Assert.False(WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
            _root,
            Path.Combine(_root, "loop-a"),
            out _));
    }

    [SkippableFact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_ChainAtDepthCap_Allows_AndOneBeyond_Rejects()
    {
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "This asserts POSIX behaviour and runs on macOS and Linux only.");

        Directory.CreateDirectory(Path.Combine(_root, "chain-end"));

        // hop-0 -> hop-1 -> ... -> hop-(N-1) -> chain-end: following hop-0 resolves N links.
        int cap = WorkspacePathPolicy.MaxSymbolicLinkResolutions;

        for (int index = 0; index <= cap; index++)
        {
            string target = index == cap ? "chain-end" : $"hop-{index + 1}";

            Directory.CreateSymbolicLink(Path.Combine(_root, $"hop-{index}"), target);
        }

        Assert.True(WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
            _root,
            Path.Combine(_root, "hop-1", "file.txt"),
            out _));

        Assert.False(WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
            _root,
            Path.Combine(_root, "hop-0", "file.txt"),
            out _));
    }

    [SkippableFact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_UnreadableIntermediateDirectory_Rejects()
    {
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "This asserts POSIX behaviour and runs on macOS and Linux only.");

        Skip.If(
            string.Equals(System.Environment.UserName, "root", StringComparison.Ordinal),
            "root bypasses directory search permission.");

        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string locked = Path.Combine(_root, "locked");

        Directory.CreateDirectory(locked);

        File.WriteAllText(Path.Combine(locked, "inner.txt"), "ok");

        File.SetUnixFileMode(locked, UnixFileMode.None);

        try
        {
            Assert.False(WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
                _root,
                Path.Combine(locked, "inner.txt"),
                out _));
        }
        finally
        {
            File.SetUnixFileMode(
                locked,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [SkippableFact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_ChainedLeafLink_ResolvesCanonicalTarget()
    {
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "This asserts POSIX behaviour and runs on macOS and Linux only.");

        string realFile = Path.Combine(_root, "real.txt");

        File.WriteAllText(realFile, "ok");

        File.CreateSymbolicLink(Path.Combine(_root, "second.txt"), "real.txt");

        File.CreateSymbolicLink(Path.Combine(_root, "first.txt"), "second.txt");

        Assert.True(WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
            _root,
            Path.Combine(_root, "first.txt"),
            out string? resolved));

        Assert.Equal(Path.GetFullPath(realFile), resolved);
    }

    /// <summary>
    /// The in-workspace shape of R-007: <c>d -> b/sub</c> with <c>b -> real-dir</c>. Containment holds, and
    /// the leaf link resolves through <c>b</c> to its canonical location.
    /// </summary>
    [SkippableFact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_LinkTargetThroughContainedDirectoryLink_ResolvesCanonicalTarget()
    {
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "This asserts POSIX behaviour and runs on macOS and Linux only.");

        string realSub = Path.Combine(_root, "real-dir", "sub");

        Directory.CreateDirectory(realSub);

        File.WriteAllText(Path.Combine(realSub, "file.txt"), "ok");

        Directory.CreateSymbolicLink(Path.Combine(_root, "b"), "real-dir");

        Directory.CreateSymbolicLink(Path.Combine(_root, "d"), Path.Combine("b", "sub"));

        Assert.True(WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
            _root,
            Path.Combine(_root, "d"),
            out string? resolvedLink));

        Assert.Equal(Path.GetFullPath(realSub), resolvedLink);

        string throughLink = Path.Combine(_root, "d", "file.txt");

        Assert.True(WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
            _root,
            throughLink,
            out string? resolvedFile));

        Assert.Equal(Path.GetFullPath(throughLink), resolvedFile);
    }

    /// <summary>
    /// A dangling link whose target lies outside the root is resolved like any other: a write through it
    /// would create the missing target outside the workspace.
    /// </summary>
    [SkippableFact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_DanglingLinkToOutside_Rejects()
    {
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "This asserts POSIX behaviour and runs on macOS and Linux only.");

        string workspace = CreateChainedLinkFixture(out _);

        Directory.CreateSymbolicLink(
            Path.Combine(workspace, "dangling"),
            Path.Combine("..", "outside", "not-yet-created"));

        Assert.False(WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
            workspace,
            Path.Combine(workspace, "dangling"),
            out _));

        Assert.False(WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
            workspace,
            Path.Combine(workspace, "dangling", "new.txt"),
            out _));
    }

    [SkippableFact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_LinkTargetClimbingOutThroughRealDirectory_Rejects()
    {
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "This asserts POSIX behaviour and runs on macOS and Linux only.");

        string workspace = CreateChainedLinkFixture(out _);

        Directory.CreateDirectory(Path.Combine(workspace, "real"));

        Directory.CreateSymbolicLink(
            Path.Combine(workspace, "climb"),
            Path.Combine("real", "..", "..", "outside"));

        Assert.False(WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
            workspace,
            Path.Combine(workspace, "climb", "sub", "file"),
            out _));
    }

    /// <summary>
    /// Canonical, not merely conservative: <c>up -> ..</c> leaves the root, but <c>up/&lt;ws&gt;/file</c>
    /// names a file inside it, exactly as the kernel would resolve it.
    /// </summary>
    [SkippableFact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_LinkOutAndBackIn_Allows()
    {
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "This asserts POSIX behaviour and runs on macOS and Linux only.");

        string workspace = CreateChainedLinkFixture(out _);

        File.WriteAllText(Path.Combine(workspace, "inside.txt"), "ok");

        Directory.CreateSymbolicLink(Path.Combine(workspace, "up"), "..");

        Assert.False(WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
            workspace,
            Path.Combine(workspace, "up", "outside", "sub", "file"),
            out _));

        Assert.True(WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
            workspace,
            Path.Combine(workspace, "up", "ws", "inside.txt"),
            out _));
    }

    [Fact]
    public void TextualLinkTargetInterpretation_CollapsesDotDotAsTextAgainstTheLinkDirectory()
    {
        string linkDirectory = Path.Combine(_root, "outer", "ws");

        string target = Path.Join("b", "..", "..", "..", "escape");

        Assert.Equal(
            Path.GetFullPath(Path.Combine(_root, "escape")),
            WorkspacePathPolicy.TextualLinkTargetInterpretation(linkDirectory, target));
    }

    /// <summary>
    /// <c>b -> a/b/c</c> (inside) and <c>d -> b/../../../escape</c>. Re-walking physically from <c>a/b/c</c>
    /// lands on <c>ws/escape</c>, inside; collapsing the target as text against <c>ws</c> lands two levels
    /// above it. POSIX uses the physical walk; under the Windows rule both interpretations must be contained,
    /// so the textual escape fails closed. Runs the Windows rule against real links on any POSIX host.
    /// </summary>
    [SkippableFact]
    public void IsPathUnderWorkspace_WindowsRule_RejectsLinkWhoseTextualInterpretationEscapes()
    {
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "Creates real links with POSIX behaviour; the Windows lane runs the native test below.");

        string workspace = CreateDualInterpretationFixture();

        string candidate = Path.Combine(workspace, "d", "new.txt");

        Assert.True(WorkspacePathPolicy.IsPathUnderWorkspaceUnderLinkSemantics(
            workspace,
            candidate,
            windowsLinkSemantics: false,
            out _));

        Assert.False(WorkspacePathPolicy.IsPathUnderWorkspaceUnderLinkSemantics(
            workspace,
            candidate,
            windowsLinkSemantics: true,
            out _));
    }

    [SkippableFact]
    public void IsPathUnderWorkspace_WindowsRule_AllowsDotDotTargetContainedUnderBothInterpretations()
    {
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "Creates real links with POSIX behaviour; the Windows lane runs the native test below.");

        string workspace = CreateDualInterpretationFixture();

        Directory.CreateSymbolicLink(
            Path.Combine(workspace, "e"),
            Path.Join("a", "..", "a", "b"));

        Assert.True(WorkspacePathPolicy.IsPathUnderWorkspaceUnderLinkSemantics(
            workspace,
            Path.Combine(workspace, "e", "c", "new.txt"),
            windowsLinkSemantics: true,
            out _));
    }

    [SkippableFact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_OnWindows_RejectsLinkWhoseTextualInterpretationEscapes()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Native Windows reparse-point lane.");

        string workspace = CreateDualInterpretationFixture();

        Assert.False(WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
            workspace,
            Path.Combine(workspace, "d", "new.txt"),
            out _));
    }

    [Fact]
    public void RevalidatePathBeforeIo_MatchesSymlinkCheck()
    {
        string nestedDir = Path.Combine(_root, "src");

        Directory.CreateDirectory(nestedDir);

        string targetFile = Path.Combine(nestedDir, "Program.cs");

        Assert.True(WorkspacePathPolicy.RevalidatePathBeforeIo(_root, targetFile));
    }

    /// <summary>
    /// Builds <c>outside/sub/file</c> (real) and <c>ws/b -> ../outside</c> under this test's temp root and
    /// returns the workspace path.
    /// </summary>
    private string CreateChainedLinkFixture(out string outside)
    {
        string fixture = Path.Combine(_root, "chain-" + Guid.NewGuid().ToString("N"));

        outside = Path.Combine(fixture, "outside");

        string workspace = Path.Combine(fixture, "ws");

        Directory.CreateDirectory(Path.Combine(outside, "sub"));

        File.WriteAllText(Path.Combine(outside, "sub", "file"), "outside secret");

        Directory.CreateDirectory(workspace);

        Directory.CreateSymbolicLink(Path.Combine(workspace, "b"), Path.Combine("..", "outside"));

        return workspace;
    }

    /// <summary>
    /// <c>outer/ws/a/b/c</c> (real), <c>ws/b -> a/b/c</c> and <c>ws/d -> b/../../../escape</c>; returns
    /// <c>outer/ws</c>.
    /// </summary>
    private string CreateDualInterpretationFixture()
    {
        string workspace = Path.Combine(_root, "dual-" + Guid.NewGuid().ToString("N"), "ws");

        Directory.CreateDirectory(Path.Combine(workspace, "a", "b", "c"));

        Directory.CreateSymbolicLink(Path.Combine(workspace, "b"), Path.Join("a", "b", "c"));

        Directory.CreateSymbolicLink(
            Path.Combine(workspace, "d"),
            Path.Join("b", "..", "..", "..", "escape"));

        return workspace;
    }

    public void Dispose()
    {
        WorkspacePathPolicy.SetUseOrdinalIgnoreCasePathComparisonForTests(false);

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
