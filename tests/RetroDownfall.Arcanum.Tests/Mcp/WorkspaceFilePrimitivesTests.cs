using System.Text;

using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Mcp;

[Collection("WorkspacePathPolicy")]
public sealed class WorkspaceFilePrimitivesTests : IAsyncLifetime
{

    private TempWorkspace _workspace = null!;

    public async Task InitializeAsync()
    {

        _workspace = new TempWorkspace();

        await _workspace.InitializeAsync();

    }

    public async Task DisposeAsync()
    {

        FileHandleIdentityInterop.TryGetPathMetadataForTests = null;

        FileHandleIdentityInterop.TryGetHandleMetadataForTests = null;

        await _workspace.DisposeAsync();

    }

    [Fact]
    public async Task Fingerprint_changes_when_content_changes_and_represents_missing_files()
    {

        _workspace.WriteFile("existing.txt", "first");

        WorkspaceFileFingerprint first = await WorkspaceFileFingerprintService.CaptureForMutationAsync(
            _workspace.Root,
            "existing.txt",
            CancellationToken.None);

        await File.WriteAllTextAsync(
            Path.Combine(_workspace.Root, "existing.txt"),
            "second",
            CancellationToken.None);

        WorkspaceFileFingerprint second = await WorkspaceFileFingerprintService.CaptureForMutationAsync(
            _workspace.Root,
            "existing.txt",
            CancellationToken.None);

        WorkspaceFileFingerprint missing = await WorkspaceFileFingerprintService.CaptureForMutationAsync(
            _workspace.Root,
            "missing.txt",
            CancellationToken.None);

        Assert.True(first.Exists);

        Assert.True(second.Exists);

        Assert.NotEqual(first, second);

        Assert.False(missing.Exists);

    }

    [SkippableFact]
    public async Task Fingerprint_rejects_hard_links_even_when_alias_is_outside_workspace()
    {

        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux() && !OperatingSystem.IsWindows(),
            "Unsupported operating system.");

        string target = _workspace.WriteFile("linked.txt", "linked");

        string outsideAlias = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-hardlink-{Guid.NewGuid():N}.txt");

        try
        {

            Assert.True(HardLinkTestSupport.TryCreate(outsideAlias, target));

            WorkspaceMutationRejectedException exception =
                await Assert.ThrowsAsync<WorkspaceMutationRejectedException>(
                    () => WorkspaceFileFingerprintService.CaptureForMutationAsync(
                        _workspace.Root,
                        "linked.txt",
                        CancellationToken.None));

            Assert.Equal(WorkspaceMutationRejection.HardLinkedFile, exception.Rejection);

        }
        finally
        {

            File.Delete(outsideAlias);

        }

    }

    [Fact]
    public async Task Fingerprint_fails_closed_when_link_count_is_unavailable()
    {

        _workspace.WriteFile("unknown-links.txt", "linked?");

        FileHandleIdentityInterop.TryGetPathMetadataForTests = _ => null;

        WorkspaceMutationRejectedException exception =
            await Assert.ThrowsAsync<WorkspaceMutationRejectedException>(
                () => WorkspaceFileFingerprintService.CaptureForMutationAsync(
                    _workspace.Root,
                    "unknown-links.txt",
                    CancellationToken.None));

        Assert.Equal(WorkspaceMutationRejection.MetadataUnavailable, exception.Rejection);

    }

    [SkippableFact]
    public async Task Fingerprint_rejects_mutation_through_contained_directory_symlink()
    {

        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "This asserts POSIX behaviour and runs on macOS and Linux only.");

        string realDirectory = _workspace.CreateSubdir("real");

        string aliasDirectory = Path.Combine(_workspace.Root, "alias");

        Directory.CreateSymbolicLink(aliasDirectory, realDirectory);

        WorkspaceMutationRejectedException exception =
            await Assert.ThrowsAsync<WorkspaceMutationRejectedException>(
                () => WorkspaceFileFingerprintService.CaptureForMutationAsync(
                    _workspace.Root,
                    "alias/new.txt",
                    CancellationToken.None));

        Assert.Equal(WorkspaceMutationRejection.SymbolicLink, exception.Rejection);

    }

    [Fact]
    public void Traversal_is_deterministic_with_unsorted_provider_results()
    {

        FakeTraversalFileSystem fileSystem = new(
            new Dictionary<string, IReadOnlyList<WorkspaceTraversalNode>>(StringComparer.Ordinal)
            {
                [""] =
                [
                    new("z.txt", WorkspaceTraversalNodeKind.File),
                    new("alpha", WorkspaceTraversalNodeKind.Directory),
                    new("ignored-link", WorkspaceTraversalNodeKind.DirectorySymlink),
                ],
                ["alpha"] =
                [
                    new("b.txt", WorkspaceTraversalNodeKind.File),
                    new("a.txt", WorkspaceTraversalNodeKind.File),
                ],
            });

        WorkspaceTraversalResult result = DeterministicWorkspaceTraversal.Traverse(
            _workspace.Root,
            new WorkspaceTraversalLimits(MaxSteps: 10, MaxFiles: 10),
            fileSystem);

        Assert.False(result.Truncated);

        Assert.Equal(
            ["alpha/a.txt", "alpha/b.txt", "z.txt"],
            result.Files.Select(file => file.RelativePath));

    }

    [Fact]
    public void Traversal_stops_at_step_and_file_limits()
    {

        FakeTraversalFileSystem fileSystem = new(
            new Dictionary<string, IReadOnlyList<WorkspaceTraversalNode>>(StringComparer.Ordinal)
            {
                [""] =
                [
                    new("c.txt", WorkspaceTraversalNodeKind.File),
                    new("b.txt", WorkspaceTraversalNodeKind.File),
                    new("a.txt", WorkspaceTraversalNodeKind.File),
                ],
            });

        WorkspaceTraversalResult stepLimited = DeterministicWorkspaceTraversal.Traverse(
            _workspace.Root,
            new WorkspaceTraversalLimits(MaxSteps: 2, MaxFiles: 10),
            fileSystem);

        WorkspaceTraversalResult fileLimited = DeterministicWorkspaceTraversal.Traverse(
            _workspace.Root,
            new WorkspaceTraversalLimits(MaxSteps: 10, MaxFiles: 1),
            fileSystem);

        Assert.True(stepLimited.Truncated);

        Assert.Equal(2, stepLimited.Steps);

        Assert.Equal(["b.txt", "c.txt"], stepLimited.Files.Select(file => file.RelativePath));

        Assert.True(fileLimited.Truncated);

        Assert.Single(fileLimited.Files);

        Assert.Equal("a.txt", fileLimited.Files[0].RelativePath);

    }

    [Fact]
    public void Traversal_stops_streaming_a_huge_directory_at_the_step_limit()
    {

        StreamingTraversalFileSystem fileSystem = new(totalEntries: 1_000_000);

        WorkspaceTraversalResult result = DeterministicWorkspaceTraversal.Traverse(
            _workspace.Root,
            new WorkspaceTraversalLimits(MaxSteps: 3, MaxFiles: 10),
            fileSystem);

        Assert.True(result.Truncated);

        Assert.Equal(3, result.Steps);

        Assert.Equal(3, fileSystem.EntriesYielded);

        Assert.Equal(
            ["file-000000.txt", "file-000001.txt", "file-000002.txt"],
            result.Files.Select(file => file.RelativePath));

    }

    [Fact]
    public void Traversal_skips_only_the_entry_that_vanished_mid_enumeration()
    {

        string directory = _workspace.CreateSubdir("src");

        for (int i = 0; i < 6; i++)
        {

            _workspace.WriteFile($"src/f{i}.txt", "x");

        }

        string[] order = Directory
            .EnumerateFileSystemEntries(directory)
            .Select(Path.GetFileName)
            .Select(static name => name!)
            .ToArray();

        HashSet<string> seen = new(StringComparer.Ordinal);

        string? victim = null;

        WorkspaceTraversalResult result = DeterministicWorkspaceTraversal.Traverse(
            _workspace.Root,
            new WorkspaceTraversalLimits(MaxSteps: 100, MaxFiles: 100),
            includeFile: relativePath =>
            {

                seen.Add(Path.GetFileName(relativePath));

                if (victim is null)
                {

                    victim = order.First(
                        name => !seen.Contains(name)
                            && !string.Equals(name, order[^1], StringComparison.Ordinal));

                    File.Delete(Path.Combine(directory, victim));

                }

                return true;

            });

        Assert.Equal(
            order.Where(name => !string.Equals(name, victim, StringComparison.Ordinal))
                .Select(name => $"src/{name}")
                .Order(StringComparer.Ordinal),
            result.Files.Select(file => file.RelativePath));

    }

    [Fact]
    public void Traversal_honors_cancellation_before_enumerating()
    {

        StreamingTraversalFileSystem fileSystem = new(totalEntries: 10);

        using CancellationTokenSource cancellation = new();

        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => DeterministicWorkspaceTraversal.Traverse(
                _workspace.Root,
                new WorkspaceTraversalLimits(MaxSteps: 10, MaxFiles: 10),
                fileSystem,
                cancellation.Token));

        Assert.Equal(0, fileSystem.EntriesYielded);

    }

    private sealed class FakeTraversalFileSystem(
        IReadOnlyDictionary<string, IReadOnlyList<WorkspaceTraversalNode>> entries)
        : IWorkspaceTraversalFileSystem
    {

        public IEnumerable<WorkspaceTraversalNode> EnumerateDirectory(
            string workspaceRoot,
            string relativeDirectory,
            CancellationToken cancellationToken)
        {

            _ = workspaceRoot;

            cancellationToken.ThrowIfCancellationRequested();

            return entries.TryGetValue(relativeDirectory, out IReadOnlyList<WorkspaceTraversalNode>? nodes)
                ? nodes
                : [];

        }

    }

    private sealed class StreamingTraversalFileSystem(int totalEntries)
        : IWorkspaceTraversalFileSystem
    {

        internal int EntriesYielded { get; private set; }

        public IEnumerable<WorkspaceTraversalNode> EnumerateDirectory(
            string workspaceRoot,
            string relativeDirectory,
            CancellationToken cancellationToken)
        {

            _ = workspaceRoot;

            if (relativeDirectory.Length != 0)
            {

                yield break;

            }

            for (int index = 0; index < totalEntries; index++)
            {

                cancellationToken.ThrowIfCancellationRequested();

                EntriesYielded++;

                yield return new WorkspaceTraversalNode(
                    $"file-{index:D6}.txt",
                    WorkspaceTraversalNodeKind.File);

            }

        }

    }

}
