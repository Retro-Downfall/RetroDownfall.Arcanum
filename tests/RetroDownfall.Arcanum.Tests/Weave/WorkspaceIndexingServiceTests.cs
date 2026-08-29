using System.Data;
using System.Data.Common;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Weave;

[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class WorkspaceIndexingServiceTests : IAsyncLifetime
{

    [Fact]

    public void Candidate_walk_has_no_total_entry_ceiling()
    {

        Type serviceType = typeof(WorkspaceIndexingService);

        Assert.Null(
            serviceType.GetField(
                "MaxWalkEntries",
                System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Static));

        System.Reflection.MethodInfo method = Assert.Single(
            serviceType.GetMethods(
                    System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Static),
            static candidate =>
                string.Equals(
                    candidate.Name,
                    "EnumerateCandidateFiles",
                    StringComparison.Ordinal));

        Assert.DoesNotContain(
            method.GetParameters(),
            static parameter => string.Equals(
                parameter.Name,
                "maxWalkEntries",
                StringComparison.Ordinal));

    }

    /// <summary>
    /// A directory link whose target resolves back under the workspace root passes the containment
    /// pre-check, so containment alone admits a cycle. Only a canonical visited set — the guard
    /// <c>EyeOfTheWorldService.ScanWorkspace</c> and <c>SpellScanner</c> already carry — stops the walk
    /// re-reaching every real file through <c>docs/latest/docs/latest/…</c> at every depth, re-embedding
    /// it under alias paths until the accumulated path finally trips PATH_MAX.
    /// </summary>
    [SkippableFact]
    public void Candidate_walk_terminates_on_an_in_workspace_directory_symlink_cycle()
    {

        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "This asserts POSIX behaviour and runs on macOS and Linux only.");

        _workspace.WriteFile("docs/note.md", "the only real candidate");

        string cyclePath = Path.Combine(_workspace.Root, "docs", "latest");

        Directory.CreateSymbolicLink(cyclePath, _workspace.Root);

        List<string> candidates;

        try
        {

            candidates = EnumerateCandidateFiles(_workspace.Root, [".md"], take: 64);

        }
        finally
        {

            Directory.Delete(cyclePath);

        }

        Assert.Equal(
            [Path.Combine(_workspace.Root, "docs", "note.md")],
            candidates);

    }

    private static List<string> EnumerateCandidateFiles(
        string workspacePath,
        string[] extensions,
        int take)
    {

        System.Reflection.MethodInfo? method = typeof(WorkspaceIndexingService)
            .GetMethod(
                "EnumerateCandidateFiles",
                System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);

        IEnumerable<string> walk = (IEnumerable<string>)method!.Invoke(
            null,
            [workspacePath, new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase)])!;

        // Bounded so an unterminated walk fails the assertion instead of hanging the suite.
        return [.. walk.Take(take)];

    }

    private readonly GrimoireFixture _fixture;

    private readonly TempWorkspace _workspace = new();

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    public WorkspaceIndexingServiceTests(GrimoireFixture fixture)
    {

        _fixture = fixture;

    }

    public async Task InitializeAsync()
    {

        await _workspace.InitializeAsync();

        _dbPath = _fixture.CopyDatabase();

        _db = _fixture.CreateContext(_dbPath);

    }

    public async Task DisposeAsync()
    {

        if (_db is not null)
        {

            await _db.DisposeAsync();

        }

        if (File.Exists(_dbPath))
        {

            File.Delete(_dbPath);

        }

        await _workspace.DisposeAsync();

    }

    [SkippableFact]
    public async Task IndexWorkspaceAsync_OnlyIndexesConfiguredExtensions()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _workspace.WriteFile("src/Foo.cs", "public class Foo {}");

        _workspace.WriteFile("assets/logo.png", "not really a png but has the wrong extension");

        FakeWeaveService weave = new();

        WorkspaceIndexingService service = CreateService(weave, out EmbeddingSettings embeddings);

        await service.IndexWorkspaceAsync(_workspace.Root, embeddings, CancellationToken.None);

        List<string> indexedPaths = await GetIndexedRelativePathsAsync();

        Assert.Contains("src/Foo.cs".Replace('/', Path.DirectorySeparatorChar), indexedPaths);

        Assert.DoesNotContain("assets/logo.png".Replace('/', Path.DirectorySeparatorChar), indexedPaths);

    }

    [SkippableFact]
    public async Task IndexWorkspaceAsync_IndexesFilesBeyondFormerTotalFileSizeCeiling()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _workspace.WriteFile("small.txt", "short content");

        _workspace.WriteFile("big.txt", new string('x', 50_001));

        FakeWeaveService weave = new();

        WorkspaceIndexingService service = CreateService(weave, out EmbeddingSettings embeddings);

        await service.IndexWorkspaceAsync(_workspace.Root, embeddings, CancellationToken.None);

        List<string> indexedPaths = await GetIndexedRelativePathsAsync();

        Assert.Contains("small.txt", indexedPaths);

        Assert.Contains("big.txt", indexedPaths);

        Assert.True(
            weave.EmbedBatchCallCount > 1,
            "Large files should be embedded through bounded streaming pages.");

    }

    [SkippableFact]

    public async Task IndexWorkspaceAsync_ContinuesAcrossInternalCheckpointsUntilEveryFileIsIndexed()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _workspace.WriteFile("one.txt", "one");

        _workspace.WriteFile("two.txt", "two");

        _workspace.WriteFile("three.txt", "three");

        WorkspaceIndexingService service = CreateService(
            new FakeWeaveService(),
            out EmbeddingSettings embeddings);

        embeddings.Codebase.MaxFilesToIndex = 1;

        for (int checkpoint = 0; checkpoint < 3; checkpoint++)
        {

            Assert.True(
                await service.IndexWorkspaceAsync(
                    _workspace.Root,
                    embeddings,
                    CancellationToken.None));

        }

        Assert.Equal(
            ["one.txt", "three.txt", "two.txt"],
            (await GetIndexedRelativePathsAsync())
                .OrderBy(static path => path, StringComparer.Ordinal)
                .ToArray());

    }

    [SkippableFact]
    public async Task IndexWorkspaceAsync_SkipsUnchangedFiles_ReindexesModifiedFiles()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string fullPath = _workspace.WriteFile("notes.md", "version one");

        FakeWeaveService weave = new();

        WorkspaceIndexingService service = CreateService(weave, out EmbeddingSettings embeddings);

        await service.IndexWorkspaceAsync(_workspace.Root, embeddings, CancellationToken.None);

        Assert.Equal(1, weave.EmbedBatchCallCount);

        // Unchanged: a second tick performs no new embedding work.
        await service.IndexWorkspaceAsync(_workspace.Root, embeddings, CancellationToken.None);

        Assert.Equal(1, weave.EmbedBatchCallCount);

        // Ensure the filesystem's LastWriteTimeUtc resolution (some platforms are 1s-granular)
        // actually advances before rewriting.
        await Task.Delay(1100);

        File.WriteAllText(fullPath, "version two, with different content");

        await service.IndexWorkspaceAsync(_workspace.Root, embeddings, CancellationToken.None);

        Assert.Equal(2, weave.EmbedBatchCallCount);

        List<string> contents = await GetChunkContentsAsync("notes.md");

        Assert.Contains("version two", string.Join(' ', contents));

    }

    [SkippableFact]
    public async Task IndexWorkspaceAsync_ChunksEmbedsAndPersistsFile()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _workspace.WriteFile("README.md", "# Title\n\nSome documentation content.");

        FakeWeaveService weave = new();

        WorkspaceIndexingService service = CreateService(weave, out EmbeddingSettings embeddings);

        await service.IndexWorkspaceAsync(_workspace.Root, embeddings, CancellationToken.None);

        int chunkCount = await CountRowsAsync("workspace_file_chunks");

        int embeddingCount = await CountRowsAsync("workspace_file_embeddings");

        Assert.Equal(1, chunkCount);

        Assert.Equal(1, embeddingCount);

    }

    [SkippableFact]
    public async Task IndexWorkspaceAsync_EmbeddingFailureForOneFile_ContinuesWithOthers()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _workspace.WriteFile("a.txt", "alpha content");

        _workspace.WriteFile("b.txt", "beta content");

        FakeWeaveService weave = new() { FailForContentContaining = "alpha" };

        WorkspaceIndexingService service = CreateService(weave, out EmbeddingSettings embeddings);

        await service.IndexWorkspaceAsync(_workspace.Root, embeddings, CancellationToken.None);

        List<string> indexedPaths = await GetIndexedRelativePathsAsync();

        Assert.DoesNotContain("a.txt", indexedPaths);

        Assert.Contains("b.txt", indexedPaths);

    }

    [SkippableFact]
    public async Task IndexWorkspaceAsync_PrunesIgnoredDirectories_NeverDescendsIntoThem()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _workspace.WriteFile("src/Foo.cs", "public class Foo {}");

        // Matches the configured extension but lives under an ignored directory segment — the walk
        // must prune node_modules before recursing into it, not just filter this file out afterward.
        _workspace.WriteFile("node_modules/pkg/index.js".Replace('/', Path.DirectorySeparatorChar), "console.log('nope');");

        FakeWeaveService weave = new();

        WorkspaceIndexingService service = CreateService(weave, out EmbeddingSettings embeddings);

        await service.IndexWorkspaceAsync(_workspace.Root, embeddings, CancellationToken.None);

        List<string> indexedPaths = await GetIndexedRelativePathsAsync();

        Assert.Contains("src/Foo.cs".Replace('/', Path.DirectorySeparatorChar), indexedPaths);

        Assert.DoesNotContain("node_modules/pkg/index.js".Replace('/', Path.DirectorySeparatorChar), indexedPaths);

    }

    [SkippableFact]
    public async Task IndexWorkspaceAsync_FileDeletedSinceLastIndex_RemovesOrphanedChunks()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _workspace.WriteFile("kept.md", "still here");

        string removedFile = _workspace.WriteFile("removed.md", "will be deleted");

        FakeWeaveService weave = new();

        WorkspaceIndexingService service = CreateService(weave, out EmbeddingSettings embeddings);

        await service.IndexWorkspaceAsync(_workspace.Root, embeddings, CancellationToken.None);

        List<string> indexedAfterFirstTick = await GetIndexedRelativePathsAsync();

        Assert.Contains("kept.md", indexedAfterFirstTick);

        Assert.Contains("removed.md", indexedAfterFirstTick);

        File.Delete(removedFile);

        await service.IndexWorkspaceAsync(_workspace.Root, embeddings, CancellationToken.None);

        List<string> indexedAfterSecondTick = await GetIndexedRelativePathsAsync();

        Assert.Contains("kept.md", indexedAfterSecondTick);

        Assert.DoesNotContain("removed.md", indexedAfterSecondTick);

        // The BLOB/vec embedding rows must be cleaned up alongside the chunk row (no orphaned
        // embeddings left pointing at a ChunkId whose chunk metadata no longer exists).
        Assert.Equal(1, await CountRowsAsync("workspace_file_embeddings"));

    }

    [SkippableFact]
    public async Task IndexWorkspaceAsync_NeverIndexesSymlinkEscapingWorkspace()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {

            return;

        }

        string outsideFile = Path.Combine(Path.GetTempPath(), $"arcanum-outside-{Guid.NewGuid():N}.md");

        await File.WriteAllTextAsync(outsideFile, "outside secret content");

        try
        {

            string linkPath = Path.Combine(_workspace.Root, "escape-link.md");

            File.CreateSymbolicLink(linkPath, outsideFile);

            FakeWeaveService weave = new();

            WorkspaceIndexingService service = CreateService(weave, out EmbeddingSettings embeddings);

            await service.IndexWorkspaceAsync(_workspace.Root, embeddings, CancellationToken.None);

            // Rejected at the pre-check (WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck) —
            // the outside file's content must never reach the embedding provider or persisted chunks.
            Assert.Equal(0, weave.EmbedBatchCallCount);

            Assert.Equal(0, await CountRowsAsync("workspace_file_chunks"));

        }
        finally
        {

            File.Delete(outsideFile);

        }

    }

    [SkippableFact]
    public async Task ExecuteAsync_IdlesWhenDisabled_NeverIndexesRegisteredWorkspace()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _workspace.WriteFile("idle.txt", "should not be indexed while disabled");

        FakeWeaveService weave = new();

        ArcanumSettings disabledSettings = new()
        {
            Features = new FeatureSettings
            {
                Embeddings = false,
                CodebaseRetrieval = false,
            },
        };

        IServiceScopeFactory scopeFactory = BuildScopeFactory();

        WorkspaceIndexingService service = new(
            new TestOptionsMonitor<ArcanumSettings>(disabledSettings),
            weave,
            new WeaveIndexAvailability(),
            scopeFactory,
            new FakeWorkspaceFileWatcherFactory(),
            NullLogger<WorkspaceIndexingService>.Instance);

        service.RegisterWorkspace(_workspace.Root);

        IHostedService hosted = service;

        await hosted.StartAsync(CancellationToken.None);

        await Task.Delay(TimeSpan.FromMilliseconds(300));

        await hosted.StopAsync(CancellationToken.None);

        Assert.Equal(0, weave.EmbedBatchCallCount);

        Assert.Equal(0, await CountRowsAsync("workspace_file_chunks"));

    }

    [SkippableFact]
    public async Task IndexNowAsync_IndexesFile_WhenWorkspaceUnderAllowedCampaignRoot()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _workspace.WriteFile("allowed.md", "content under an allowed root");

        FakeWeaveService weave = new();

        WorkspaceIndexingService service = CreateService(weave, out _, campaignAllowedRoots: [_workspace.Root]);

        await service.IndexNowAsync(_workspace.Root, CancellationToken.None);

        List<string> indexedPaths = await GetIndexedRelativePathsAsync();

        Assert.Contains("allowed.md", indexedPaths);

    }

    [SkippableFact]
    public async Task IndexNowAsync_RejectsWorkspace_WhenNotUnderAnyAllowedCampaignRoot()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _workspace.WriteFile("secret.md", "should never be indexed");

        FakeWeaveService weave = new();

        // Empty Security.CampaignRoots is secure-by-default (WorkspaceRootPolicy.EnforceAllowedRoots
        // denies everything), so this call must be a graceful no-op rather than indexing an
        // unvalidated directory — see CampaignPathPolicy.ValidateAndNormalizePath.
        WorkspaceIndexingService service = CreateService(weave, out _, campaignAllowedRoots: []);

        await service.IndexNowAsync(_workspace.Root, CancellationToken.None);

        Assert.Equal(0, weave.EmbedBatchCallCount);

        Assert.Equal(0, await CountRowsAsync("workspace_file_chunks"));

    }

    [SkippableFact]
    public async Task IndexNowAsync_CoalescesASecondRequestWhileTheSameWorkspaceIsAlreadyReconciling()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _workspace.WriteFile("coalesce.md", "content that must only be embedded once per in-flight run");

        GatedWeaveService weave = new();

        WorkspaceIndexingService service = CreateService(weave, out _);

        Task first = service.IndexNowAsync(_workspace.Root, CancellationToken.None);

        await weave.EmbedEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Task second = service.IndexNowAsync(_workspace.Root, CancellationToken.None);

        Task settled = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(5)));

        weave.Release();

        await first.WaitAsync(TimeSpan.FromSeconds(30));

        await second.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(
            ReferenceEquals(settled, second),
            "A re-index requested while the same workspace is still reconciling must coalesce onto the in-flight run instead of starting a duplicate scan.");

        Assert.Equal(1, weave.EmbedBatchCallCount);

    }

    [SkippableFact]
    public async Task ExecuteAsync_NeverIndexesRegisteredWorkspace_WhenNotUnderAnyAllowedCampaignRoot()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _workspace.WriteFile("secret.md", "should never be indexed via the background tick either");

        FakeWeaveService weave = new();

        WorkspaceIndexingService service = CreateService(weave, out _, campaignAllowedRoots: []);

        // RegisterWorkspace must silently reject the path (Security.CampaignRoots is empty above),
        // so the background tick never has this workspace queued for indexing.
        service.RegisterWorkspace(_workspace.Root);

        IHostedService hosted = service;

        await hosted.StartAsync(CancellationToken.None);

        await Task.Delay(TimeSpan.FromMilliseconds(300));

        await hosted.StopAsync(CancellationToken.None);

        Assert.Equal(0, weave.EmbedBatchCallCount);

        Assert.Equal(0, await CountRowsAsync("workspace_file_chunks"));

    }

    [SkippableFact]
    public async Task ExecuteAsync_ReconciliationThrowsRepeatedly_BacksOffInsteadOfTightLooping()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _workspace.WriteFile("spin.txt", "the failing reconciliation never gets this far");

        FailingScopeFactory scopes = new();

        WorkspaceIndexingService service = CreateService(new FakeWeaveService(), out _, scopeFactory: scopes);

        service.RegisterWorkspace(_workspace.Root);

        IHostedService hosted = service;

        await hosted.StartAsync(CancellationToken.None);

        await Task.Delay(TimeSpan.FromMilliseconds(300));

        await hosted.StopAsync(CancellationToken.None);

        // A Grimoire failure raised outside IndexWorkspaceAsync's per-file try/catch (opening the
        // scope, the one-query FileLastWriteTime load, or orphaned-chunk cleanup) unwinds the whole
        // tick before it can stamp the next reconciliation time, so without a backoff the loop
        // re-reconciles as fast as the CPU allows — thousands of attempts inside 300ms.
        Assert.True(
            scopes.ScopeCount <= 2,
            $"Expected at most 2 reconciliation attempts within 300ms given a 1s backoff after failure; got {scopes.ScopeCount}.");

    }

    [SkippableFact]
    public async Task RegisterWorkspace_IsThreadSafe_UnderConcurrentCalls()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        FakeWeaveService weave = new();

        FakeWorkspaceFileWatcherFactory watchers = new();

        WorkspaceIndexingService service = CreateService(weave, out _, watcherFactory: watchers);

        // The directories have to exist on disk: CampaignPathPolicy.ValidateAndNormalizePath rejects a
        // path that is not an existing directory, and RegisterWorkspace then returns before it ever
        // touches the registry — so a path-only array never exercises the concurrent-write path at all.
        string[] paths = Enumerable.Range(0, 50)
            .Select(i => _workspace.CreateSubdir($"workspace-{i}"))
            .ToArray();

        int watcherCapacity = ArcanumRuntimeDefaults.Embeddings.Codebase.MaxWatchers;

        int expectedWatchers = Math.Min(paths.Length, watcherCapacity);

        Parallel.ForEach(paths, service.RegisterWorkspace);

        // Re-registering an already-known path (idempotent) exercises the concurrent-write path once more.
        Parallel.ForEach(paths, service.RegisterWorkspace);

        // Every root must land in the registry exactly once: watched up to the capacity bound and
        // degraded beyond it, never absent (a lost update) and never watched twice (a duplicate add).
        Assert.Equal(expectedWatchers, service.ActiveWatcherCount);

        Assert.Equal(expectedWatchers, watchers.Created.Count);

        Assert.Equal(
            expectedWatchers,
            watchers.Created
                .Select(static watcher => watcher.WorkspacePath)
                .Distinct(StringComparer.Ordinal)
                .Count());

        WorkspaceIndexRuntimeStatus[] statuses = [.. paths.Select(service.GetRuntimeStatus)];

        Assert.All(statuses, static status => Assert.True(status.Watching || status.Degraded));

        Assert.Equal(expectedWatchers, statuses.Count(static status => status.Watching));

        Assert.Equal(
            paths.Length - expectedWatchers,
            statuses.Count(static status => status.Degraded && !status.Watching));

        await service.DisposeAsync();

    }

    [SkippableFact]
    public async Task WatcherRegistry_IsBounded_AndUnwatchedWorkspaceStaysOnDegradedPollingFallback()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        int watcherCapacity = ArcanumRuntimeDefaults.Embeddings.Codebase.MaxWatchers;

        string[] workspaces = Enumerable.Range(0, watcherCapacity + 1)
            .Select(index =>
                Directory.CreateDirectory(
                        Path.Combine(_workspace.Root, $"workspace-{index}"))
                    .FullName)
            .ToArray();

        FakeWorkspaceFileWatcherFactory watchers = new();

        WorkspaceIndexingService service = CreateService(
            new FakeWeaveService(),
            out _,
            watcherFactory: watchers);

        foreach (string workspace in workspaces)
        {

            service.RegisterWorkspace(workspace);

        }

        Assert.Equal(watcherCapacity, service.ActiveWatcherCount);

        Assert.True(service.GetRuntimeStatus(workspaces[^1]).Degraded);

        Assert.False(service.GetRuntimeStatus(workspaces[^1]).Watching);

        await service.DisposeAsync();

    }

    [SkippableFact]
    public async Task WatcherEventStorm_IsDebouncedAndCoalescedToOneIncrementalReindex()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string fullPath = _workspace.WriteFile("storm.cs", "public class Storm {}");

        FakeWorkspaceFileWatcherFactory watchers = new();

        FakeWeaveService weave = new();

        WorkspaceIndexingService service = CreateService(weave, out _, watcherFactory: watchers);

        service.RegisterWorkspace(_workspace.Root);

        for (int i = 0; i < 500; i++)
        {

            watchers.Single.TriggerChanged(fullPath);

        }

        await service.ProcessPendingWatcherEventsAsync(_workspace.Root, CancellationToken.None);

        Assert.Equal(1, weave.EmbedBatchCallCount);

        Assert.NotNull(service.GetRuntimeStatus(_workspace.Root).LastEventAt);

        await service.DisposeAsync();

    }

    [SkippableFact]
    public async Task WatcherRenameSave_ReplacesTargetAndRemovesTemporaryFileChunks()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string target = _workspace.WriteFile("notes.md", "old content");

        FakeWorkspaceFileWatcherFactory watchers = new();

        WorkspaceIndexingService service = CreateService(new FakeWeaveService(), out EmbeddingSettings embeddings, watcherFactory: watchers);

        await service.IndexWorkspaceAsync(_workspace.Root, embeddings, CancellationToken.None);

        string temporary = _workspace.WriteFile(".notes.md.tmp", "new replacement content");

        File.Move(temporary, target, overwrite: true);

        service.RegisterWorkspace(_workspace.Root);

        watchers.Single.TriggerRenamed(temporary, target);

        watchers.Single.TriggerDeleted(target);

        await service.ProcessPendingWatcherEventsAsync(_workspace.Root, CancellationToken.None);

        Assert.Contains("new replacement content", await GetChunkContentsAsync("notes.md"));

        Assert.DoesNotContain(".notes.md.tmp", await GetIndexedRelativePathsAsync());

        await service.DisposeAsync();

    }

    [SkippableFact]
    public async Task WatcherOverflow_MarksStaleAndRunsBoundedReconciliation()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string fullPath = _workspace.WriteFile("overflow.txt", "before overflow");

        FakeWorkspaceFileWatcherFactory watchers = new();

        WorkspaceIndexingService service = CreateService(new FakeWeaveService(), out EmbeddingSettings embeddings, watcherFactory: watchers);

        await service.IndexWorkspaceAsync(_workspace.Root, embeddings, CancellationToken.None);

        // Change detection compares LastWriteTimeUtc for exact equality, and Windows stamps file
        // times from the coarse system-clock tick, so a rewrite this soon after the indexing run
        // that recorded the old timestamp can land on that very value — the reconciliation would
        // then skip the file for a reason that has nothing to do with the overflow under test.
        // Capturing what was recorded and stamping a distinctly later time keeps "modified after
        // indexing" unambiguous on every filesystem, including the 2-second-granular ones.
        DateTime indexedWriteTime = File.GetLastWriteTimeUtc(fullPath);

        File.WriteAllText(fullPath, "after overflow");

        File.SetLastWriteTimeUtc(fullPath, indexedWriteTime.AddSeconds(2));

        service.RegisterWorkspace(_workspace.Root);

        watchers.Single.TriggerError(new InternalBufferOverflowException("simulated overflow"));

        WorkspaceIndexRuntimeStatus stale = service.GetRuntimeStatus(_workspace.Root);

        Assert.True(stale.Degraded);

        Assert.True(stale.Overflowed);

        await service.ProcessPendingWatcherEventsAsync(_workspace.Root, CancellationToken.None);

        WorkspaceIndexRuntimeStatus recovered = service.GetRuntimeStatus(_workspace.Root);

        Assert.False(recovered.Degraded);

        Assert.False(recovered.Overflowed);

        Assert.False(recovered.Reconciling);

        Assert.NotNull(recovered.LastSuccessfulIndexAt);

        Assert.Contains("after overflow", await GetChunkContentsAsync("overflow.txt"));

        await service.DisposeAsync();

    }

    [SkippableFact]
    public async Task WatcherSymlinkReplacement_RemovesPreviouslyIndexedContent()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {

            return;

        }

        string target = _workspace.WriteFile("linked.md", "safe original");

        string outside = Path.Combine(Path.GetTempPath(), $"arcanum-outside-{Guid.NewGuid():N}.md");

        await File.WriteAllTextAsync(outside, "outside secret");

        try
        {

            FakeWorkspaceFileWatcherFactory watchers = new();

            FakeWeaveService weave = new();

            WorkspaceIndexingService service = CreateService(weave, out EmbeddingSettings embeddings, watcherFactory: watchers);

            await service.IndexWorkspaceAsync(_workspace.Root, embeddings, CancellationToken.None);

            File.Delete(target);

            File.CreateSymbolicLink(target, outside);

            service.RegisterWorkspace(_workspace.Root);

            watchers.Single.TriggerChanged(target);

            await service.ProcessPendingWatcherEventsAsync(_workspace.Root, CancellationToken.None);

            Assert.DoesNotContain("linked.md", await GetIndexedRelativePathsAsync());

            Assert.DoesNotContain(weave.EmbeddedTexts, text => text.Contains("outside secret", StringComparison.Ordinal));

            await service.DisposeAsync();

        }
        finally
        {

            File.Delete(outside);

        }

    }

    [SkippableFact]
    public async Task WatcherEvents_IgnoreExcludedFoldersAndIndexLargeFiles()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string ignored = _workspace.WriteFile("node_modules/pkg/index.js", "console.log('ignored');");

        string large = _workspace.WriteFile("large.txt", new string('x', 50_001));

        FakeWorkspaceFileWatcherFactory watchers = new();

        FakeWeaveService weave = new();

        WorkspaceIndexingService service = CreateService(weave, out _, watcherFactory: watchers);

        service.RegisterWorkspace(_workspace.Root);

        watchers.Single.TriggerCreated(ignored);

        watchers.Single.TriggerCreated(large);

        await service.ProcessPendingWatcherEventsAsync(_workspace.Root, CancellationToken.None);

        Assert.True(weave.EmbedBatchCallCount > 0);

        Assert.Contains("large.txt", await GetIndexedRelativePathsAsync());

        await service.DisposeAsync();

    }

    [SkippableFact]
    public async Task WatcherProcessing_HonorsCancellation()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string fullPath = _workspace.WriteFile("cancel.cs", "public class Cancel {}");

        FakeWorkspaceFileWatcherFactory watchers = new();

        WorkspaceIndexingService service = CreateService(new FakeWeaveService(), out _, watcherFactory: watchers);

        service.RegisterWorkspace(_workspace.Root);

        watchers.Single.TriggerChanged(fullPath);

        using CancellationTokenSource cancellation = new();

        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.ProcessPendingWatcherEventsAsync(_workspace.Root, cancellation.Token));

        await service.DisposeAsync();

    }

    [SkippableFact]
    public async Task StopAsync_DisposesEveryWorkspaceWatcher()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        FakeWorkspaceFileWatcherFactory watchers = new();

        WorkspaceIndexingService service = CreateService(new FakeWeaveService(), out _, watcherFactory: watchers);

        service.RegisterWorkspace(_workspace.Root);

        IHostedService hosted = service;

        await hosted.StartAsync(CancellationToken.None);

        await hosted.StopAsync(CancellationToken.None);

        Assert.True(watchers.Single.IsDisposed);

        Assert.Equal(0, service.ActiveWatcherCount);

    }

    [SkippableFact]
    public async Task UnregisterWorkspace_DisposesWatcherForInactiveWorkspace()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        FakeWorkspaceFileWatcherFactory watchers = new();

        WorkspaceIndexingService service = CreateService(new FakeWeaveService(), out _, watcherFactory: watchers);

        service.RegisterWorkspace(_workspace.Root);

        service.UnregisterWorkspace(_workspace.Root);

        Assert.True(watchers.Single.IsDisposed);

        Assert.Equal(0, service.ActiveWatcherCount);

        Assert.False(service.GetRuntimeStatus(_workspace.Root).Watching);

        await service.DisposeAsync();

    }

    [SkippableFact]
    public async Task StableChunkIds_ReuseUnchangedChunkEmbeddingsAfterSmallEdit()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string fullPath = _workspace.WriteFile(
            "Stable.cs",
            """
            public class First
            {
                public string Value => "before";
            }

            public class Second
            {
                public string Value => "unchanged";
            }
            """);

        FakeWeaveService weave = new();

        WorkspaceIndexingService service = CreateService(weave, out EmbeddingSettings embeddings);

        await service.IndexWorkspaceAsync(_workspace.Root, embeddings, CancellationToken.None);

        Dictionary<string, string> before = await GetChunkIdsByContentAsync("Stable.cs");

        await Task.Delay(1100);

        File.WriteAllText(
            fullPath,
            """
            public class First
            {
                public string Value => "after";
            }

            public class Second
            {
                public string Value => "unchanged";
            }
            """);

        await service.IndexWorkspaceAsync(_workspace.Root, embeddings, CancellationToken.None);

        Dictionary<string, string> after = await GetChunkIdsByContentAsync("Stable.cs");

        string unchangedBefore = Assert.Single(
            before,
            static pair => pair.Key.StartsWith("public class Second", StringComparison.Ordinal)).Value;

        string unchangedAfter = Assert.Single(
            after,
            static pair => pair.Key.StartsWith("public class Second", StringComparison.Ordinal)).Value;

        Assert.Equal(unchangedBefore, unchangedAfter);

        Assert.Equal(2, weave.EmbedBatchCallCount);

        Assert.Single(weave.EmbedBatches[^1]);

    }

    [SkippableFact]
    public async Task IndexedChunks_PersistAccurateOneBasedLineRanges()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _workspace.WriteFile(
            "lines.md",
            """
            # One
            alpha

            # Two
            beta
            """);

        WorkspaceIndexingService service = CreateService(new FakeWeaveService(), out EmbeddingSettings embeddings);

        await service.IndexWorkspaceAsync(_workspace.Root, embeddings, CancellationToken.None);

        (int StartLine, int EndLine)[] ranges = await GetLineRangesAsync("lines.md");

        Assert.Equal([(1, 3), (4, 5)], ranges);

    }

    private WorkspaceIndexingService CreateService(
        IWeaveService weave,
        out EmbeddingSettings embeddings,
        string[]? campaignAllowedRoots = null,
        FakeWorkspaceFileWatcherFactory? watcherFactory = null,
        IServiceScopeFactory? scopeFactory = null)
    {

        embeddings = ArcanumRuntimeDefaults.Embeddings;

        IServiceScopeFactory scopes = scopeFactory ?? BuildScopeFactory();

        ArcanumSettings settings = new()
        {
            Features = new FeatureSettings
            {
                Embeddings = true,
                CodebaseRetrieval = true,
            },
            Integrations = new IntegrationSettings
            {
                Embeddings = new EmbeddingIntegrationSettings
                {
                    Provider = "test",
                    Model = "test-embed",
                },
            },
            Security = new SecuritySettings
            {
                CampaignRoots = campaignAllowedRoots ?? [_workspace.Root],
            },
        };

        embeddings = settings.ResolveEmbeddings();

        return new WorkspaceIndexingService(
            new TestOptionsMonitor<ArcanumSettings>(settings),
            weave,
            new WeaveIndexAvailability(),
            scopes,
            watcherFactory ?? new FakeWorkspaceFileWatcherFactory(),
            NullLogger<WorkspaceIndexingService>.Instance);

    }

    private IServiceScopeFactory BuildScopeFactory()
    {

        ServiceCollection services = new();

        services.AddSingleton(_db!);

        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

    }

    private async Task<List<string>> GetIndexedRelativePathsAsync()
    {

        DbConnection connection = _db!.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {

            await connection.OpenAsync();

        }

        await using DbCommand cmd = connection.CreateCommand();

        cmd.CommandText = """SELECT DISTINCT "RelativePath" FROM "workspace_file_chunks";""";

        List<string> results = [];

        await using DbDataReader reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {

            results.Add(reader.GetString(0));

        }

        return results;

    }

    private async Task<List<string>> GetChunkContentsAsync(string relativePath)
    {

        DbConnection connection = _db!.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {

            await connection.OpenAsync();

        }

        await using DbCommand cmd = connection.CreateCommand();

        cmd.CommandText = """SELECT "Content" FROM "workspace_file_chunks" WHERE "RelativePath" = @relativePath;""";

        DbParameter param = cmd.CreateParameter();

        param.ParameterName = "@relativePath";

        param.Value = relativePath;

        cmd.Parameters.Add(param);

        List<string> results = [];

        await using DbDataReader reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {

            results.Add(reader.GetString(0));

        }

        return results;

    }

    [SkippableFact]
    public async Task IndexWorkspaceAsync_NeverSplitsASurrogatePairAcrossReadPages()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        int chunkSize = ArcanumSettingClamps.EmbeddingsChunkSizeChars(
            ArcanumRuntimeDefaults.Embeddings.ChunkSizeChars);

        // The file is paged eight chunks at a time; ReadBlockAsync fills to a character count and knows
        // nothing about UTF-16 pairs, so an astral character whose first code unit is the last of a page
        // is split in half, and both fragments are persisted with the character replaced by U+FFFD.
        int readPageCharacters = chunkSize * 8;

        _workspace.WriteFile(
            "emoji.md",
            new string('a', readPageCharacters - 1) + "\U0001F600" + new string('b', 200));

        FakeWeaveService weave = new();

        WorkspaceIndexingService service = CreateService(weave, out EmbeddingSettings embeddings);

        await service.IndexWorkspaceAsync(_workspace.Root, embeddings, CancellationToken.None);

        List<string> chunks = await GetChunkContentsAsync("emoji.md");

        Assert.NotEmpty(chunks);

        foreach (string chunk in chunks)
        {

            Assert.DoesNotContain(
                '�',
                Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(chunk)));

        }

        Assert.Contains(chunks, static chunk => chunk.Contains("\U0001F600", StringComparison.Ordinal));

    }

    private async Task<int> CountRowsAsync(string tableName)
    {

        DbConnection connection = _db!.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {

            await connection.OpenAsync();

        }

        await using DbCommand cmd = connection.CreateCommand();

        cmd.CommandText = $"""SELECT COUNT(*) FROM "{tableName}";""";

        object? result = await cmd.ExecuteScalarAsync();

        return Convert.ToInt32(result);

    }

    private async Task<Dictionary<string, string>> GetChunkIdsByContentAsync(string relativePath)
    {

        DbConnection connection = _db!.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {

            await connection.OpenAsync();

        }

        await using DbCommand cmd = connection.CreateCommand();

        cmd.CommandText = """SELECT "Content", "ChunkId" FROM "workspace_file_chunks" WHERE "RelativePath" = @relativePath;""";

        DbParameter param = cmd.CreateParameter();

        param.ParameterName = "@relativePath";

        param.Value = relativePath;

        cmd.Parameters.Add(param);

        Dictionary<string, string> results = new(StringComparer.Ordinal);

        await using DbDataReader reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {

            results[reader.GetString(0)] = reader.GetString(1);

        }

        return results;

    }

    private async Task<(int StartLine, int EndLine)[]> GetLineRangesAsync(string relativePath)
    {

        DbConnection connection = _db!.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {

            await connection.OpenAsync();

        }

        await using DbCommand cmd = connection.CreateCommand();

        cmd.CommandText =
            """
            SELECT "StartLine", "EndLine"
            FROM "workspace_file_chunks"
            WHERE "RelativePath" = @relativePath
            ORDER BY "ChunkIndex";
            """;

        DbParameter param = cmd.CreateParameter();

        param.ParameterName = "@relativePath";

        param.Value = relativePath;

        cmd.Parameters.Add(param);

        List<(int StartLine, int EndLine)> results = [];

        await using DbDataReader reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {

            results.Add((reader.GetInt32(0), reader.GetInt32(1)));

        }

        return [.. results];

    }

    private sealed class FakeWeaveService : IWeaveService
    {

        public string? FailForContentContaining { get; set; }

        public int EmbedBatchCallCount { get; private set; }

        public List<IReadOnlyList<string>> EmbedBatches { get; } = [];

        public IEnumerable<string> EmbeddedTexts => EmbedBatches.SelectMany(static batch => batch);

        public bool IsAvailable => true;

        public Task<Result<Embedding<float>>> EmbedAsync(string text, CancellationToken cancellationToken) =>
            throw new NotSupportedException("WorkspaceIndexingService only calls EmbedBatchAsync.");

        public Task<Result<Embedding<float>[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
        {

            EmbedBatchCallCount++;

            EmbedBatches.Add([.. texts]);

            if (FailForContentContaining is { } needle && texts.Any(t => t.Contains(needle, StringComparison.Ordinal)))
            {

                return Task.FromResult(Result<Embedding<float>[]>.Failure(
                    new Error(ErrorCodes.Embeddings.ProviderUnavailable, "Simulated embedding failure.")));

            }

            Embedding<float>[] generated = new Embedding<float>[texts.Count];

            for (int i = 0; i < texts.Count; i++)
            {

                generated[i] = new Embedding<float>(new float[] { 1f, 0f, 0f });

            }

            return Task.FromResult(Result<Embedding<float>[]>.Success(generated));

        }

        public Task<Result<(string Chunk, int Offset)[]>> ChunkAsync(string text, CancellationToken cancellationToken) =>
            Task.FromResult(Result<(string Chunk, int Offset)[]>.Success(
                string.IsNullOrEmpty(text) ? [] : [(text, 0)]));

    }

    /// <summary>
    /// Fails every reconciliation the way an unavailable Grimoire would — the failure is raised
    /// outside <c>IndexWorkspaceAsync</c>'s per-file try/catch, so it unwinds the whole tick — while
    /// counting attempts so a test can tell a backed-off retry from a tight spin.
    /// </summary>
    private sealed class FailingScopeFactory : IServiceScopeFactory
    {

        private int _scopeCount;

        public int ScopeCount => Volatile.Read(ref _scopeCount);

        public IServiceScope CreateScope()
        {

            Interlocked.Increment(ref _scopeCount);

            throw new InvalidOperationException("Simulated Grimoire failure during workspace reconciliation.");

        }

    }

    /// <summary>
    /// Parks every embedding call until <see cref="Release"/> is called, so a test can hold one
    /// reconciliation run in flight while it issues a second one.
    /// </summary>
    private sealed class GatedWeaveService : IWeaveService
    {

        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _embedBatchCallCount;

        public TaskCompletionSource EmbedEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int EmbedBatchCallCount => Volatile.Read(ref _embedBatchCallCount);

        public bool IsAvailable => true;

        public void Release() => _release.TrySetResult();

        public Task<Result<Embedding<float>>> EmbedAsync(string text, CancellationToken cancellationToken) =>
            throw new NotSupportedException("WorkspaceIndexingService only calls EmbedBatchAsync.");

        public async Task<Result<Embedding<float>[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
        {

            Interlocked.Increment(ref _embedBatchCallCount);

            EmbedEntered.TrySetResult();

            await _release.Task.ConfigureAwait(false);

            Embedding<float>[] generated = new Embedding<float>[texts.Count];

            for (int i = 0; i < texts.Count; i++)
            {

                generated[i] = new Embedding<float>(new float[] { 1f, 0f, 0f });

            }

            return Result<Embedding<float>[]>.Success(generated);

        }

        public Task<Result<(string Chunk, int Offset)[]>> ChunkAsync(string text, CancellationToken cancellationToken) =>
            Task.FromResult(Result<(string Chunk, int Offset)[]>.Success(
                string.IsNullOrEmpty(text) ? [] : [(text, 0)]));

    }

    private sealed class FakeWorkspaceFileWatcherFactory : IWorkspaceFileWatcherFactory
    {

        private readonly List<FakeWorkspaceFileWatcher> _watchers = [];

        public FakeWorkspaceFileWatcher Single => Assert.Single(_watchers);

        public IReadOnlyList<FakeWorkspaceFileWatcher> Created => _watchers;

        public IWorkspaceFileWatcher Create(
            string workspacePath,
            Action<WorkspaceFileChange> onChange,
            Action<Exception> onError)
        {

            FakeWorkspaceFileWatcher watcher = new(workspacePath, onChange, onError);

            _watchers.Add(watcher);

            return watcher;

        }

    }

    private sealed class FakeWorkspaceFileWatcher(
        string workspacePath,
        Action<WorkspaceFileChange> onChange,
        Action<Exception> onError) : IWorkspaceFileWatcher
    {

        public bool IsDisposed { get; private set; }

        public string WorkspacePath => workspacePath;

        public void TriggerCreated(string path) =>
            onChange(new WorkspaceFileChange(workspacePath, WorkspaceFileChangeKind.Created, path));

        public void TriggerChanged(string path) =>
            onChange(new WorkspaceFileChange(workspacePath, WorkspaceFileChangeKind.Changed, path));

        public void TriggerDeleted(string path) =>
            onChange(new WorkspaceFileChange(workspacePath, WorkspaceFileChangeKind.Deleted, path));

        public void TriggerRenamed(string oldPath, string path) =>
            onChange(new WorkspaceFileChange(workspacePath, WorkspaceFileChangeKind.Renamed, path, oldPath));

        public void TriggerError(Exception exception) => onError(exception);

        public void Dispose() => IsDisposed = true;

    }

}
