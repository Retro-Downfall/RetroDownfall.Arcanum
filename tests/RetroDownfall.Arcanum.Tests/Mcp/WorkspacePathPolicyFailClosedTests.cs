using System.Runtime.InteropServices;

using Microsoft.Win32.SafeHandles;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Mcp;

/// <summary>
/// The refusal and edge arms of <see cref="WorkspacePathPolicy"/>: paths the runtime cannot normalise, roots
/// that cannot be canonicalised or opened, entries the walk cannot classify, link targets it cannot follow,
/// and the directory-creation diagnosis. Each one must fail closed rather than fall through to a default.
/// </summary>
[Collection("WorkspacePathPolicy")]
public sealed class WorkspacePathPolicyFailClosedTests : IDisposable
{
    private readonly string _root;

    public WorkspacePathPolicyFailClosedTests()
    {
        _root = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "arcanum-wpp-failclosed-" + Guid.NewGuid().ToString("N")));

        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_CandidateWithNulCharacter_FailsClosed()
    {
        Assert.False(WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
            _root,
            Path.Combine(_root, "bad\0name.txt"),
            out string? resolved));

        Assert.Null(resolved);
    }

    [Fact]
    public void ContainmentCheckObserver_SeesTheNormalizedCandidate_OnlyAfterTheLexicalCheckPasses()
    {
        List<string> observed = [];

        WorkspacePathPolicy.ContainmentCheckObserverForTests = observed.Add;

        Assert.True(WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
            _root,
            Path.Combine(_root, "sub", "..", "file.txt"),
            out _));

        Assert.False(WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
            _root,
            Path.Combine(_root, "..", "outside.txt"),
            out _));

        Assert.Equal(Path.Combine(_root, "file.txt"), Assert.Single(observed));
    }

    [SkippableFact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_RootThatIsALinkLoop_FailsClosed()
    {
        SkipUnlessPosix();

        string root = CreateLinkLoop();

        Assert.False(WorkspacePathPolicy.TryCanonicalize(root, out _, out _));

        Assert.False(WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
            root,
            Path.Combine(root, "file.txt"),
            out string? resolved));

        Assert.Null(resolved);
    }

    /// <summary>
    /// The canonical walk resolves each component on its own, so a candidate spelled through the same
    /// <c>-> .</c> link many times reaches the existing file. The candidate's own spelling is longer than
    /// the kernel accepts, though, so its leaf cannot be classified and the check refuses rather than
    /// guessing whether the leaf is a link.
    /// </summary>
    [SkippableFact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_CandidateSpellingTheKernelRefuses_FailsClosed()
    {
        SkipUnlessPosix();

        string selfLink = new('s', 120);

        File.CreateSymbolicLink(Path.Combine(_root, selfLink), ".");

        File.WriteAllText(Path.Combine(_root, "file.txt"), "ok");

        Assert.True(WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
            _root,
            Path.Combine(_root, selfLink, "file.txt"),
            out string? shortResolved));

        Assert.Equal(Path.Combine(_root, selfLink, "file.txt"), shortResolved);

        string[] parts = [_root, .. Enumerable.Repeat(selfLink, 38), "file.txt"];

        string candidate = Path.Combine(parts);

        // Longer than PATH_MAX on macOS (1024) and Linux (4096), within the link budget.
        Assert.True(candidate.Length > 4096);

        Assert.False(WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
            _root,
            candidate,
            out string? resolved));

        Assert.Null(resolved);
    }

    [SkippableFact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_LeafLinkToItsOwnDirectory_ResolvesToTheRootAsSpelled()
    {
        SkipUnlessPosix();

        File.CreateSymbolicLink(Path.Combine(_root, "self"), ".");

        Assert.True(WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
            _root + Path.DirectorySeparatorChar,
            Path.Combine(_root, "self"),
            out string? resolved));

        Assert.Equal(_root, resolved);
    }

    /// <summary>
    /// <c>..</c> at the filesystem root stays at the root, as the kernel does, so an absolute link target
    /// spelled <c>/../&lt;canonical root&gt;/sub</c> resolves into the workspace.
    /// </summary>
    [SkippableFact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_LinkClimbingAboveTheFilesystemRoot_StaysAtTheRoot()
    {
        SkipUnlessPosix();

        Directory.CreateDirectory(Path.Combine(_root, "sub"));

        Assert.True(WorkspacePathPolicy.TryCanonicalize(_root, out string? canonicalRoot, out bool exists));

        Assert.True(exists);

        string target = "/../.." + canonicalRoot + "/sub";

        Directory.CreateSymbolicLink(Path.Combine(_root, "up"), target);

        Assert.True(WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
            _root,
            Path.Combine(_root, "up"),
            out string? resolved));

        Assert.Equal(Path.Combine(_root, "sub"), resolved);
    }

    [SkippableFact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_LinkWithAnEmptyTarget_FailsClosed()
    {
        SkipUnlessPosix();

        string link = Path.Combine(_root, "empty-link");

        Skip.If(
            UnixNative.CreateSymbolicLink(string.Empty, link) != 0,
            "This filesystem refuses a symbolic link with an empty target.");

        Assert.Equal(string.Empty, new FileInfo(link).LinkTarget);

        Assert.False(WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(_root, link, out _));

        Assert.False(WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
            _root,
            Path.Combine(link, "file.txt"),
            out _));
    }

    [SkippableFact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_LinkTargetReadFails_FailsClosed()
    {
        SkipUnlessPosix();

        string candidate = CreateLinkedDirectoryFixture("unreadable-link");

        Assert.True(WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(_root, candidate, out _));

        WorkspacePathPolicy.LinkTargetReaderForTests = path =>
            Path.GetFileName(path) == "unreadable-link"
                ? throw new IOException("readlink failed")
                : new FileInfo(path).LinkTarget;

        Assert.False(WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(_root, candidate, out _));
    }

    /// <summary>
    /// The link-target seam is flow-local, like <see cref="SecureFileReader.AfterOpenForTests"/>: a reader
    /// a test installs answers only the walks of that test's own flow, so it needs no reset and cannot
    /// reach a walk another flow runs. Nothing in production writes it, not even a reset, which is what
    /// lets the hosted-producer analysis prove that a production walk never invokes it.
    /// </summary>
    [SkippableFact]
    public async Task LinkTargetReader_DoesNotReachAnotherFlow()
    {
        SkipUnlessPosix();

        string candidate = CreateLinkedDirectoryFixture("other-flow-link");

        WorkspacePathPolicy.LinkTargetReaderForTests = path =>
            Path.GetFileName(path) == "other-flow-link"
                ? throw new IOException("readlink failed")
                : new FileInfo(path).LinkTarget;

        Task<bool> otherFlow;

        using (ExecutionContext.SuppressFlow())
        {
            otherFlow = Task.Run(() => WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(_root, candidate, out _));
        }

        Assert.True(await otherFlow);

        Assert.False(WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(_root, candidate, out _));
    }

    /// <summary>
    /// A reparse point that names no other location is a link that changed under the walk on Unix, so the
    /// walk refuses; on Windows it is an ordinary entry (a cloud placeholder) and the walk continues on the
    /// entry's own name.
    /// </summary>
    [SkippableFact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_ReparsePointNamingNoLocation_IsClassifiedPerPlatformRule()
    {
        SkipUnlessPosix();

        string candidate = CreateLinkedDirectoryFixture("placeholder");

        WorkspacePathPolicy.LinkTargetReaderForTests = path =>
            Path.GetFileName(path) == "placeholder" ? null : new FileInfo(path).LinkTarget;

        Assert.False(WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(_root, candidate, out _));

        WorkspacePathPolicy.SetUseWindowsReparsePointClassificationForTests(true);

        Assert.True(WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
            _root,
            candidate,
            out string? resolved));

        Assert.Equal(candidate, resolved);
    }

    /// <summary>
    /// The first missing component and everything after it are kept lexically; a tail the runtime cannot
    /// normalise (here a link target carrying a NUL, which only a non-POSIX reparse point can hold) fails
    /// closed instead of escaping as an exception.
    /// </summary>
    [SkippableFact]
    public void IsPathUnderWorkspaceWithSymlinkCheck_UnnormalisableTailAfterAMissingComponent_FailsClosed()
    {
        SkipUnlessPosix();

        Directory.CreateSymbolicLink(Path.Combine(_root, "odd-link"), "placeholder-target");

        string candidate = Path.Combine(_root, "odd-link");

        WorkspacePathPolicy.LinkTargetReaderForTests = path =>
            Path.GetFileName(path) == "odd-link" ? "missing/ok" : new FileInfo(path).LinkTarget;

        Assert.True(WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(_root, candidate, out _));

        WorkspacePathPolicy.LinkTargetReaderForTests = path =>
            Path.GetFileName(path) == "odd-link" ? "missing/bad\0name" : new FileInfo(path).LinkTarget;

        Assert.False(WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(_root, candidate, out _));
    }

    /// <summary>
    /// Under the Windows rule a relative target with a <c>..</c> segment is also interpreted as text; a
    /// target that text interpretation cannot normalise fails closed.
    /// </summary>
    [SkippableFact]
    public void IsPathUnderWorkspace_WindowsRule_UnnormalisableTextualInterpretation_FailsClosed()
    {
        SkipUnlessPosix();

        Directory.CreateSymbolicLink(Path.Combine(_root, "odd-link"), "placeholder-target");

        WorkspacePathPolicy.LinkTargetReaderForTests = path =>
            Path.GetFileName(path) == "odd-link" ? "bad\0name/.." : new FileInfo(path).LinkTarget;

        Assert.False(WorkspacePathPolicy.IsPathUnderWorkspaceUnderLinkSemantics(
            _root,
            Path.Combine(_root, "odd-link", "file.txt"),
            windowsLinkSemantics: true,
            out _));
    }

    /// <summary>
    /// <c>l -> x/../loop-a</c> with <c>x</c> missing: the physical walk stops at <c>x</c> and keeps the rest
    /// lexically, but the textual interpretation collapses <c>x/..</c> and walks into a link loop, which it
    /// cannot resolve, so the Windows rule refuses.
    /// </summary>
    [SkippableFact]
    public void IsPathUnderWorkspace_WindowsRule_TextualInterpretationThatCannotResolve_FailsClosed()
    {
        SkipUnlessPosix();

        CreateLinkLoop();

        Directory.CreateSymbolicLink(Path.Combine(_root, "l"), Path.Join("x", "..", "loop-a"));

        string candidate = Path.Combine(_root, "l", "file.txt");

        Assert.True(WorkspacePathPolicy.IsPathUnderWorkspaceUnderLinkSemantics(
            _root,
            candidate,
            windowsLinkSemantics: false,
            out _));

        Assert.False(WorkspacePathPolicy.IsPathUnderWorkspaceUnderLinkSemantics(
            _root,
            candidate,
            windowsLinkSemantics: true,
            out _));
    }

    /// <summary>
    /// The Windows dual interpretation also holds when the link is the candidate's last component, with
    /// nothing left to walk after the target.
    /// </summary>
    [SkippableFact]
    public void IsPathUnderWorkspace_WindowsRule_LeafLinkWithDotDotTarget_ResolvesUnderTheRoot()
    {
        SkipUnlessPosix();

        Directory.CreateDirectory(Path.Combine(_root, "a", "b"));

        Directory.CreateSymbolicLink(Path.Combine(_root, "e"), Path.Join("a", "..", "a", "b"));

        Assert.True(WorkspacePathPolicy.IsPathUnderWorkspaceUnderLinkSemantics(
            _root,
            Path.Combine(_root, "e"),
            windowsLinkSemantics: true,
            out string? resolved));

        Assert.Equal(Path.Combine(_root, "a", "b"), resolved);
    }

    [Fact]
    public void TryCanonicalize_RelativePath_FailsClosed()
    {
        Assert.False(WorkspacePathPolicy.TryCanonicalize(
            Path.Combine("relative", "path"),
            out string? canonicalPath,
            out bool exists));

        Assert.Null(canonicalPath);

        Assert.False(exists);
    }

    [Fact]
    public void HasEntryBlockingDirectoryCreation_PathWithNulCharacter_ReportsNoBlocker()
    {
        Assert.False(WorkspacePathPolicy.HasEntryBlockingDirectoryCreation(
            _root,
            Path.Combine(_root, "bad\0name")));
    }

    [Fact]
    public void HasEntryBlockingDirectoryCreation_DirectoryOutsideTheWorkspace_ReportsNoBlocker()
    {
        string workspace = Path.Combine(_root, "ws");

        File.WriteAllText(Path.Combine(_root, "file.txt"), "not a directory");

        Assert.False(WorkspacePathPolicy.HasEntryBlockingDirectoryCreation(
            workspace,
            Path.Combine(_root, "file.txt", "sub")));
    }

    [Fact]
    public void HasEntryBlockingDirectoryCreation_TheRootItself_ReportsNoBlocker()
    {
        Assert.False(WorkspacePathPolicy.HasEntryBlockingDirectoryCreation(_root, _root));
    }

    [Fact]
    public void HasEntryBlockingDirectoryCreation_EveryComponentAnExistingDirectory_ReportsNoBlocker()
    {
        string existing = Path.Combine(_root, "a", "b");

        Directory.CreateDirectory(existing);

        Assert.False(WorkspacePathPolicy.HasEntryBlockingDirectoryCreation(_root, existing));
    }

    [Fact]
    public void HasEntryBlockingDirectoryCreation_FirstMissingComponent_ReportsNoBlocker()
    {
        Directory.CreateDirectory(Path.Combine(_root, "a"));

        File.WriteAllText(Path.Combine(_root, "file.txt"), "beside, not on the path");

        Assert.False(WorkspacePathPolicy.HasEntryBlockingDirectoryCreation(
            _root,
            Path.Combine(_root, "a", "missing", "file.txt", "deeper")));
    }

    [Fact]
    public void HasEntryBlockingDirectoryCreation_RegularFileOnThePath_ReportsTheBlocker()
    {
        Directory.CreateDirectory(Path.Combine(_root, "a"));

        File.WriteAllText(Path.Combine(_root, "a", "file.txt"), "in the way");

        Assert.True(WorkspacePathPolicy.HasEntryBlockingDirectoryCreation(
            _root,
            Path.Combine(_root, "a", "file.txt", "sub")));
    }

    [SkippableFact]
    public void HasEntryBlockingDirectoryCreation_DanglingLinkOnThePath_ReportsTheBlocker()
    {
        SkipUnlessPosix();

        Directory.CreateSymbolicLink(Path.Combine(_root, "dangling"), "nowhere");

        Assert.True(WorkspacePathPolicy.HasEntryBlockingDirectoryCreation(
            _root,
            Path.Combine(_root, "dangling", "sub")));
    }

    [SkippableFact]
    public void HasEntryBlockingDirectoryCreation_LinkToADirectoryOnThePath_ReportsNoBlocker()
    {
        SkipUnlessPosix();

        Directory.CreateDirectory(Path.Combine(_root, "real"));

        Directory.CreateSymbolicLink(Path.Combine(_root, "linked"), "real");

        Assert.False(WorkspacePathPolicy.HasEntryBlockingDirectoryCreation(
            _root,
            Path.Combine(_root, "linked", "new")));
    }

    /// <summary>
    /// A component the kernel refuses to look up (a name longer than <c>NAME_MAX</c>) cannot be classified,
    /// so the diagnosis names no blocker rather than guessing.
    /// </summary>
    [SkippableFact]
    public void HasEntryBlockingDirectoryCreation_UnclassifiableComponent_ReportsNoBlocker()
    {
        SkipUnlessPosix();

        Assert.False(WorkspacePathPolicy.HasEntryBlockingDirectoryCreation(
            _root,
            Path.Combine(_root, new string('n', 300), "sub")));
    }

    [Fact]
    public void TryGetOpenedHandleRelativePath_ClosedHandle_FailsClosed()
    {
        SafeFileHandle handle = File.OpenHandle(CreateFile("closed.txt"));

        handle.Dispose();

        Assert.False(WorkspacePathPolicy.TryGetOpenedHandleRelativePath(_root, handle, out string? relativePath));

        Assert.Null(relativePath);
    }

    [SkippableFact]
    public void TryGetOpenedHandleRelativePath_RootWithNulCharacter_FailsClosed()
    {
        SkipUnlessKernelPathQuery();

        using SafeFileHandle handle = File.OpenHandle(CreateFile("inside.txt"));

        Assert.False(WorkspacePathPolicy.TryGetOpenedHandleRelativePath(
            _root + "\0",
            handle,
            out string? relativePath));

        Assert.Null(relativePath);
    }

    [SkippableFact]
    public void TryGetOpenedHandleRelativePath_RootThatIsALinkLoop_FailsClosed()
    {
        SkipUnlessPosix();

        string root = CreateLinkLoop();

        using SafeFileHandle handle = File.OpenHandle(CreateFile("inside.txt"));

        Assert.False(WorkspacePathPolicy.TryGetOpenedHandleRelativePath(root, handle, out string? relativePath));

        Assert.Null(relativePath);
    }

    [SkippableFact]
    public void TryGetOpenedHandleRelativePath_MissingRoot_FailsClosed()
    {
        SkipUnlessKernelPathQuery();

        using SafeFileHandle handle = File.OpenHandle(CreateFile("inside.txt"));

        Assert.False(WorkspacePathPolicy.TryGetOpenedHandleRelativePath(
            Path.Combine(_root, "missing-root"),
            handle,
            out string? relativePath));

        Assert.Null(relativePath);
    }

    [SkippableFact]
    public void TryGetOpenedHandleRelativePath_RootThatIsARegularFile_FailsClosed()
    {
        SkipUnlessKernelPathQuery();

        string file = CreateFile("inside.txt");

        using SafeFileHandle handle = File.OpenHandle(file);

        Assert.False(WorkspacePathPolicy.TryGetOpenedHandleRelativePath(file, handle, out string? relativePath));

        Assert.Null(relativePath);
    }

    /// <summary>
    /// The opened file's kernel path is known, but the kernel cannot name the workspace root's own
    /// directory handle, so there is nothing to compare against and the check refuses.
    /// </summary>
    [SkippableFact]
    public void TryGetOpenedHandleRelativePath_RootKernelPathUnavailable_FailsClosed()
    {
        SkipUnlessKernelPathQuery();

        using SafeFileHandle handle = File.OpenHandle(CreateFile("inside.txt"));

        Assert.True(WorkspacePathPolicy.TryGetOpenedHandleRelativePath(_root, handle, out _));

        Func<SafeFileHandle, string?>? previous = FileHandleIdentityInterop.TryGetHandleKernelPathForTests;

        List<SafeFileHandle> queried = [];

        FileHandleIdentityInterop.TryGetHandleKernelPathForTests = candidate =>
        {
            queried.Add(candidate);

            return ReferenceEquals(candidate, handle) ? Path.Combine(_root, "inside.txt") : null;
        };

        try
        {
            Assert.False(WorkspacePathPolicy.TryGetOpenedHandleRelativePath(
                _root,
                handle,
                out string? relativePath));

            Assert.Null(relativePath);

            Assert.Equal(2, queried.Count);

            Assert.Same(handle, queried[0]);

            Assert.True(queried[1].IsClosed);
        }
        finally
        {
            FileHandleIdentityInterop.TryGetHandleKernelPathForTests = previous;
        }
    }

    public void Dispose()
    {
        WorkspacePathPolicy.ResetTestSeams();

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // Best-effort temp cleanup.
        }
    }

    private static void SkipUnlessPosix() =>
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "This asserts POSIX behaviour and runs on macOS and Linux only.");

    private static void SkipUnlessKernelPathQuery() =>
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux() && !OperatingSystem.IsWindows(),
            "The kernel path query is implemented for macOS, Linux and Windows.");

    private string CreateFile(string name)
    {
        string path = Path.Combine(_root, name);

        File.WriteAllText(path, "ok");

        return path;
    }

    /// <summary>
    /// <c>loop-a -> loop-b -> loop-a</c> under this test's root; returns the path of <c>loop-a</c>.
    /// </summary>
    private string CreateLinkLoop()
    {
        File.CreateSymbolicLink(Path.Combine(_root, "loop-a"), "loop-b");

        File.CreateSymbolicLink(Path.Combine(_root, "loop-b"), "loop-a");

        return Path.Combine(_root, "loop-a");
    }

    /// <summary>
    /// <c>real/file.txt</c> and <c>&lt;linkName&gt; -> real</c>; returns <c>&lt;linkName&gt;/file.txt</c>.
    /// </summary>
    private string CreateLinkedDirectoryFixture(string linkName)
    {
        Directory.CreateDirectory(Path.Combine(_root, "real"));

        File.WriteAllText(Path.Combine(_root, "real", "file.txt"), "ok");

        Directory.CreateSymbolicLink(Path.Combine(_root, linkName), "real");

        return Path.Combine(_root, linkName, "file.txt");
    }

    private static class UnixNative
    {
        [DllImport(
            "libc",
            EntryPoint = "symlink",
            SetLastError = true)]
        internal static extern int CreateSymbolicLink(string target, string linkPath);
    }
}
