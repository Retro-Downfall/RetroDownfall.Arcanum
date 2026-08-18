using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry;

/// <summary>
/// The weaving pipeline (DESIGN §21.11): embed → cluster → summarize → recurse into an immutable
/// generation. These tests assert deterministic <b>membership and lineage</b> plus summary
/// provenance — never that the model produced identical prose, which the design explicitly does not
/// promise.
/// </summary>
[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class TapestryWeaverTests : IAsyncLifetime
{

    // Must be at or above ArcanumSettingClamps.EmbeddingsDimensions' floor (64): the weaver clamps
    // the configured dimension, and a narrower test vector would be quarantined as a mismatch.
    private const int TestDimensions = 64;

    private static readonly TapestryScope Scope = new(TapestryScopeKind.Workspace, "/repo");

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    private TapestryStore? _store;

    private FakeWeaveService? _weave;

    private FakeSummarizer? _summarizer;

    public TapestryWeaverTests(GrimoireFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {

        _dbPath = _fixture.CopyDatabase();

        _db = _fixture.CreateContext(_dbPath);

        _store = new TapestryStore(_db, new WeaveIndexAvailability());

        _weave = new FakeWeaveService();

        _summarizer = new FakeSummarizer();

        return Task.CompletedTask;

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

    }

    private readonly CapturingLogger _logger = new();

    private TapestryWeaver CreateWeaver() =>
        new(_store!, _weave!, _summarizer!, TimeProvider.System, _logger);

    /// <summary>Surfaces the weaver's own diagnostics in assertion messages so a build failure is legible.</summary>
    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<TapestryWeaver>
    {

        private readonly List<string> _entries = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _entries.Add($"{logLevel}: {formatter(state, exception)}{(exception is null ? string.Empty : " | " + exception)}");

        public override string ToString() => string.Join("\n", _entries);

    }

    private static EmbeddingSettings Settings(
        int maxTreeDepth = 5,
        int target = 2,
        int maxChildren = 3,
        bool workspaceTrees = true) =>
        new()
        {
            Enabled = true,
            TapestryEnabled = true,
            Dimensions = TestDimensions,
            Tapestry = new TapestryEmbeddingSettings
            {
                MaxTreeDepth = maxTreeDepth,
                TargetChildrenPerSummary = target,
                MaxChildrenPerSummary = maxChildren,
                MaxClustersPerLayer = 64,
                WorkspaceTreesEnabled = workspaceTrees,
            },
        };

    [SkippableFact]
    public async Task RunSweepAsync_DeletesSupersededGenerationsOnEverySweep()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedChunksAsync(
            ("c00", "a.cs", "alpha body"),
            ("c01", "b.cs", "bravo body"),
            ("c02", "c.cs", "charlie body"));

        EmbeddingSettings embeddings = Settings();

        ServiceCollection services = new();

        _ = services.AddSingleton<ITapestryStore>(_store!);

        _ = services.AddSingleton(CreateWeaver());

        await using ServiceProvider provider = services.BuildServiceProvider();

        TapestryWeavingService sweeper = new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()),
            NullLogger<TapestryWeavingService>.Instance);

        _ = await sweeper.RunSweepAsync(embeddings, CancellationToken.None);

        // Change the corpus so the next sweep publishes a new generation and supersedes the first.
        await SeedChunksAsync(("c03", "d.cs", "delta body"));

        _ = await sweeper.RunSweepAsync(embeddings, CancellationToken.None);

        // Publishing only marks the predecessor Superseded; reconciliation is what deletes it. Doing
        // that once per process would leave a full orphaned copy of the scope's nodes and node
        // embeddings behind on every rebuild until restart.
        Assert.Equal(0, await CountGenerationsWithStatusAsync("Superseded"));

        Assert.Equal(1, await CountGenerationsWithStatusAsync("Complete"));

    }

    private async Task<int> CountGenerationsWithStatusAsync(string status)
    {

        System.Data.Common.DbConnection connection =
            Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.GetDbConnection(_db!.Database);

        if (connection.State != System.Data.ConnectionState.Open)
        {

            await connection.OpenAsync();

        }

        await using System.Data.Common.DbCommand command = connection.CreateCommand();

        command.CommandText = "SELECT COUNT(*) FROM tapestry_generations WHERE Status = @status";

        AddParameter(command, "@status", status);

        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);

    }

    private async Task SeedChunksAsync(params (string ChunkId, string Path, string Content)[] chunks)
    {

        System.Data.Common.DbConnection connection =
            Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.GetDbConnection(_db!.Database);

        if (connection.State != System.Data.ConnectionState.Open)
        {

            await connection.OpenAsync();

        }

        foreach ((string chunkId, string path, string content) in chunks)
        {

            await using System.Data.Common.DbCommand command = connection.CreateCommand();

            command.CommandText =
                """
                INSERT OR REPLACE INTO workspace_file_chunks
                    (ChunkId, WorkspacePath, RelativePath, ChunkIndex, Content, CharOffset, CharLength,
                     StartLine, EndLine, FileLastWriteTime, IndexedAt)
                VALUES (@chunkId, '/repo', @path, 0, @content, 0, 10, 1, 3,
                        '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z')
                """;

            AddParameter(command, "@chunkId", chunkId);

            AddParameter(command, "@path", path);

            AddParameter(command, "@content", content);

            _ = await command.ExecuteNonQueryAsync();

        }

    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {

        System.Data.Common.DbParameter parameter = command.CreateParameter();

        parameter.ParameterName = name;

        parameter.Value = value;

        command.Parameters.Add(parameter);

    }

    private static float[] UnitVector()
    {

        float[] vector = new float[TestDimensions];

        vector[0] = 1f;

        return vector;

    }

    private static async Task SeedTenChunksAsync(TapestryWeaverTests test) =>
        await test.SeedChunksAsync(
            [.. Enumerable.Range(0, 10).Select(index =>
                ($"c{index:D2}", $"file{index % 3}.cs", $"content body number {index}"))]);

    [SkippableFact]
    public async Task WeaveAsync_NoCorpusProducesNoGeneration()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        TapestryWeaveOutcome outcome = await CreateWeaver().WeaveAsync(Scope, Settings(), CancellationToken.None);

        Assert.Equal(TapestryWeaveStatus.NoCorpus, outcome.Status);

        Assert.Null(await _store!.GetCurrentGenerationAsync(Scope, CancellationToken.None));

    }

    [SkippableFact]
    public async Task WeaveAsync_CancelledInsideThePlanPhase_StopsBeforeTheNextWholeClusterEstimate()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedTenChunksAsync(this);

        using CancellationTokenSource cancellation = new();

        _summarizer!.OnFitEstimate = cancellation.Cancel;

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateWeaver().WeaveAsync(Scope, Settings(), cancellation.Token));

        // Splitting, merging, and the per-candidate fit check are one long synchronous stretch with no
        // await in it, and every fit estimate concatenates and tokenizes a whole cluster's text. DESIGN
        // §21.11 promises the sweep "stays cancellable throughout rather than only at scope and layer
        // boundaries", so the estimate that requested the stop has to be the last one this layer runs —
        // not merely the last one before the next layer boundary.
        Assert.Equal(1, _summarizer.FitEstimateCalls);

        Assert.Null(await _store!.GetCurrentGenerationAsync(Scope, CancellationToken.None));

    }

    [SkippableFact]
    public async Task WeaveAsync_BuildsAndPublishesAHierarchy()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedTenChunksAsync(this);

        TapestryWeaveOutcome outcome = await CreateWeaver().WeaveAsync(Scope, Settings(), CancellationToken.None);

        Assert.True(outcome.Status == TapestryWeaveStatus.Woven, $"expected Woven, got {outcome.Status}. Log:\n{_logger}");

        Assert.True(outcome.LayerCount >= 2, $"expected a summary layer, got {outcome.LayerCount}");

        Assert.True(outcome.NodeCount > 10, $"expected summaries above 10 leaves, got {outcome.NodeCount}");

        Assert.True(outcome.SummaryCallsMade > 0);

        TapestryGeneration current = (await _store!.GetCurrentGenerationAsync(Scope, CancellationToken.None))!;

        Assert.Equal(TapestryGenerationStatus.Complete, current.Status);

        Assert.Equal(10, (await _store.GetLayerNodesAsync(current.GenerationId, 0, CancellationToken.None)).Count);

        Assert.NotEmpty(await _store.GetLayerNodesAsync(current.GenerationId, 1, CancellationToken.None));

    }

    [SkippableFact]
    public async Task WeaveAsync_EveryLeafGetsAParentBelowTheTerminalLayer()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedTenChunksAsync(this);

        _ = await CreateWeaver().WeaveAsync(Scope, Settings(), CancellationToken.None);

        TapestryGeneration current = (await _store!.GetCurrentGenerationAsync(Scope, CancellationToken.None))!;

        IReadOnlyList<TapestryNode> leaves = await _store.GetLayerNodesAsync(
            current.GenerationId,
            0,
            CancellationToken.None);

        // Hard membership in v1: each child has exactly one parent per generation, and no leaf is
        // orphaned unless it is itself a terminal root.
        Assert.All(leaves, leaf => Assert.NotNull(leaf.ParentNodeId));

    }

    [SkippableFact]
    public async Task WeaveAsync_IsUpToDateOnAnUnchangedCorpus()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedTenChunksAsync(this);

        TapestryWeaver weaver = CreateWeaver();

        _ = await weaver.WeaveAsync(Scope, Settings(), CancellationToken.None);

        int callsAfterFirst = _summarizer!.CallCount;

        TapestryWeaveOutcome second = await weaver.WeaveAsync(Scope, Settings(), CancellationToken.None);

        Assert.Equal(TapestryWeaveStatus.UpToDate, second.Status);

        Assert.Equal(callsAfterFirst, _summarizer.CallCount);

    }

    [SkippableFact]
    public async Task WeaveAsync_ALeafEditRebuildsTheWholeScopeButReusesUnchangedSummaries()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedTenChunksAsync(this);

        TapestryWeaver weaver = CreateWeaver();

        _ = await weaver.WeaveAsync(Scope, Settings(), CancellationToken.None);

        string firstGeneration = (await _store!.GetCurrentGenerationAsync(Scope, CancellationToken.None))!.GenerationId;

        _summarizer!.ResetCounters();

        await SeedChunksAsync(("c00", "file0.cs", "content body number 0 — edited"));

        TapestryWeaveOutcome outcome = await weaver.WeaveAsync(Scope, Settings(), CancellationToken.None);

        Assert.True(outcome.Status == TapestryWeaveStatus.Woven, $"expected Woven, got {outcome.Status}. Log:\n{_logger}");

        string secondGeneration = (await _store.GetCurrentGenerationAsync(Scope, CancellationToken.None))!.GenerationId;

        Assert.NotEqual(firstGeneration, secondGeneration);

        // Summary reuse is by exact membership/recipe/model/input identity, so clusters untouched by
        // the edit cost no new model call — but the whole scope is still re-clustered, because
        // K-Means centroids are relative to the complete layer.
        Assert.True(
            outcome.SummariesReused > 0,
            $"expected at least one reused summary, got {outcome.SummariesReused}");

    }

    [SkippableFact]
    public async Task WeaveAsync_ProducesTheSameMembershipsAcrossRebuilds()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedTenChunksAsync(this);

        TapestryWeaver weaver = CreateWeaver();

        _ = await weaver.WeaveAsync(Scope, Settings(), CancellationToken.None);

        string[] firstMemberships = await ReadMembershipHashesAsync();

        // Force a rebuild without changing the corpus by changing a tree-shaping bound and back.
        _ = await weaver.WeaveAsync(Scope, Settings(maxTreeDepth: 4), CancellationToken.None);

        _ = await weaver.WeaveAsync(Scope, Settings(), CancellationToken.None);

        string[] secondMemberships = await ReadMembershipHashesAsync();

        Assert.Equal(firstMemberships, secondMemberships);

    }

    private async Task<string[]> ReadMembershipHashesAsync()
    {

        TapestryGeneration current = (await _store!.GetCurrentGenerationAsync(Scope, CancellationToken.None))!;

        List<string> hashes = [];

        for (int layer = 1; layer <= current.LayerCount; layer++)
        {

            IReadOnlyList<TapestryNode> nodes = await _store.GetLayerNodesAsync(
                current.GenerationId,
                layer,
                CancellationToken.None);

            hashes.AddRange(nodes.Select(static node => node.ChildMembershipHash ?? string.Empty));

        }

        hashes.Sort(StringComparer.Ordinal);

        return [.. hashes];

    }

    [SkippableFact]
    public async Task WeaveAsync_NeverExceedsTheChildCountBound()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedChunksAsync(
            [.. Enumerable.Range(0, 24).Select(index =>
                ($"c{index:D2}", "same.cs", $"body {index}"))]);

        _ = await CreateWeaver().WeaveAsync(
            Scope,
            Settings(target: 2, maxChildren: 3),
            CancellationToken.None);

        TapestryGeneration current = (await _store!.GetCurrentGenerationAsync(Scope, CancellationToken.None))!;

        for (int layer = 1; layer <= current.LayerCount; layer++)
        {

            foreach (TapestryNode node in await _store.GetLayerNodesAsync(
                current.GenerationId,
                layer,
                CancellationToken.None))
            {

                int children = await CountChildrenAsync(node.NodeId);

                Assert.True(children <= 3, $"summary {node.NodeId} had {children} children");

            }

        }

    }

    private async Task<int> CountChildrenAsync(string parentNodeId)
    {

        System.Data.Common.DbConnection connection =
            Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.GetDbConnection(_db!.Database);

        await using System.Data.Common.DbCommand command = connection.CreateCommand();

        command.CommandText = "SELECT COUNT(*) FROM tapestry_nodes WHERE ParentNodeId = @parent";

        AddParameter(command, "@parent", parentNodeId);

        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);

    }

    [SkippableFact]
    public async Task WeaveAsync_IdenticalVectorsFallBackToAStableIdPartition()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        // Every chunk embeds to the same direction, so semantic splitting is impossible.
        _weave!.ConstantVector = UnitVector();

        await SeedChunksAsync(
            [.. Enumerable.Range(0, 9).Select(index => ($"c{index:D2}", "same.cs", $"identical body {index}"))]);

        TapestryWeaveOutcome outcome = await CreateWeaver().WeaveAsync(
            Scope,
            Settings(target: 2, maxChildren: 3),
            CancellationToken.None);

        Assert.True(outcome.Status == TapestryWeaveStatus.Woven, $"expected Woven, got {outcome.Status}. Log:\n{_logger}");

        TapestryGeneration current = (await _store!.GetCurrentGenerationAsync(Scope, CancellationToken.None))!;

        IReadOnlyList<TapestryNode> summaries = await _store.GetLayerNodesAsync(
            current.GenerationId,
            1,
            CancellationToken.None);

        Assert.NotEmpty(summaries);

        Assert.Contains(
            summaries,
            static node => node.PartitionReason == TapestryPartitionReason.IdenticalVectorPartition);

    }

    [SkippableFact]
    public async Task WeaveAsync_NeverEstimatesTheFitOfAClusterTheChildCountBoundAlreadyRulesOut()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        // Identical directions collapse k to 1, so the whole layer arrives at the splitter as one
        // cluster. Estimating that cluster's fit means concatenating, hashing, and tokenizing every
        // member's text — synchronous, uncancellable work whose answer the child-count bound has
        // already decided (DESIGN §21.11: "the child-count check comes first").
        _weave!.ConstantVector = UnitVector();

        await SeedChunksAsync(
            [.. Enumerable.Range(0, 9).Select(index => ($"c{index:D2}", "same.cs", $"identical body {index}"))]);

        TapestryWeaveOutcome outcome = await CreateWeaver().WeaveAsync(
            Scope,
            Settings(target: 2, maxChildren: 3),
            CancellationToken.None);

        Assert.True(outcome.Status == TapestryWeaveStatus.Woven, $"expected Woven, got {outcome.Status}. Log:\n{_logger}");

        Assert.True(
            _summarizer!.LargestFitEstimate <= 3,
            $"a fit estimate was built for {_summarizer.LargestFitEstimate} children, above the bound of 3");

    }

    [SkippableFact]
    public async Task WeaveAsync_ByteIdenticalLeavesDoNotCollideOnNodeId()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _weave!.ConstantVector = UnitVector();

        // Byte-identical content, so two summary clusters hash to the same child-membership hash. If
        // that hash alone were the node's identity, the second INSERT would violate the tapestry_nodes
        // primary key and fail the whole generation — permanently, since the corpus fingerprint never
        // changes, re-billing every summary computed before the collision on every sweep.
        await SeedChunksAsync(
            [.. Enumerable.Range(0, 8).Select(index => ($"c{index:D2}", "same.cs", "identical body"))]);

        TapestryWeaveOutcome outcome = await CreateWeaver().WeaveAsync(
            Scope,
            Settings(target: 2, maxChildren: 3),
            CancellationToken.None);

        Assert.True(outcome.Status == TapestryWeaveStatus.Woven, $"expected Woven, got {outcome.Status}. Log:\n{_logger}");

        TapestryGeneration current = (await _store!.GetCurrentGenerationAsync(Scope, CancellationToken.None))!;

        IReadOnlyList<TapestryNode> summaries = await _store.GetLayerNodesAsync(
            current.GenerationId,
            1,
            CancellationToken.None);

        Assert.True(summaries.Count >= 2, $"expected at least two layer-1 summaries, got {summaries.Count}.");

        Assert.Equal(summaries.Count, summaries.Select(static node => node.NodeId).Distinct(StringComparer.Ordinal).Count());

    }

    [SkippableFact]
    public async Task WeaveAsync_StopsAtMaxDepthWithAnExplicitMultiRootTerminalLayer()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        // Sixteen leaves against a child-count bound of three: the whole-layer root shortcut cannot
        // fire, so layer 1 is written as several summaries and the depth cap stops recursion there
        // with an honest multi-root terminal layer.
        await SeedChunksAsync(
            [.. Enumerable.Range(0, 16).Select(index => ($"c{index:D2}", "f.cs", $"body {index}"))]);

        TapestryWeaveOutcome outcome = await CreateWeaver().WeaveAsync(
            Scope,
            Settings(maxTreeDepth: 1, target: 2, maxChildren: 3),
            CancellationToken.None);

        Assert.True(outcome.Status == TapestryWeaveStatus.Woven, $"expected Woven, got {outcome.Status}. Log:\n{_logger}");

        Assert.Equal(TapestryTerminalReason.MaxDepth, outcome.TerminalReason);

        Assert.True(outcome.RootNodeCount > 1, "a max-depth terminal layer must report its real root count");

        Assert.Equal(2, outcome.LayerCount);

    }

    [SkippableFact]
    public async Task WeaveAsync_ASummaryFailureLeavesThePriorGenerationCurrent()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedTenChunksAsync(this);

        TapestryWeaver weaver = CreateWeaver();

        _ = await weaver.WeaveAsync(Scope, Settings(), CancellationToken.None);

        string published = (await _store!.GetCurrentGenerationAsync(Scope, CancellationToken.None))!.GenerationId;

        await SeedChunksAsync(("c99", "new.cs", "a brand new chunk that forces a rebuild"));

        _summarizer!.FailEverySummary = true;

        TapestryWeaveOutcome outcome = await weaver.WeaveAsync(Scope, Settings(), CancellationToken.None);

        Assert.Equal(TapestryWeaveStatus.Failed, outcome.Status);

        Assert.Equal(
            published,
            (await _store.GetCurrentGenerationAsync(Scope, CancellationToken.None))!.GenerationId);

        Assert.Equal(0, await _store.ReconcileGenerationsAsync(CancellationToken.None));

    }

    [SkippableFact]
    public async Task WeaveAsync_EmbeddingProviderDownLeavesThePriorGenerationCurrent()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedTenChunksAsync(this);

        _weave!.Available = false;

        TapestryWeaveOutcome outcome = await CreateWeaver().WeaveAsync(Scope, Settings(), CancellationToken.None);

        Assert.Equal(TapestryWeaveStatus.EmbeddingUnavailable, outcome.Status);

        Assert.Null(await _store!.GetCurrentGenerationAsync(Scope, CancellationToken.None));

    }

    [SkippableFact]
    public async Task WeaveAsync_NoSummaryModelContributesNoTree()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedTenChunksAsync(this);

        _summarizer!.Model = null;

        TapestryWeaveOutcome outcome = await CreateWeaver().WeaveAsync(Scope, Settings(), CancellationToken.None);

        Assert.Equal(TapestryWeaveStatus.SummaryModelUnavailable, outcome.Status);

        Assert.Null(await _store!.GetCurrentGenerationAsync(Scope, CancellationToken.None));

    }

    [SkippableFact]
    public async Task WeaveAsync_QuarantinesUnusableLeafVectorsWithoutFailingTheBuild()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedTenChunksAsync(this);

        _weave!.PoisonContentSubstring = "number 3";

        TapestryWeaveOutcome outcome = await CreateWeaver().WeaveAsync(Scope, Settings(), CancellationToken.None);

        Assert.True(outcome.Status == TapestryWeaveStatus.Woven, $"expected Woven, got {outcome.Status}. Log:\n{_logger}");

        Assert.Equal(1, outcome.QuarantinedVectors);

        TapestryGeneration current = (await _store!.GetCurrentGenerationAsync(Scope, CancellationToken.None))!;

        Assert.Equal(9, (await _store.GetLayerNodesAsync(current.GenerationId, 0, CancellationToken.None)).Count);

    }

    [SkippableFact]
    public async Task WeaveAsync_AShortEmbeddingBatchAbandonsTheBuildInsteadOfPairingVectorsPositionally()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedTenChunksAsync(this);

        // The provider answers a 10-input batch with 9 vectors. Zipping the response against the
        // request by index would hand every leaf from the omission onward its neighbour's vector, and
        // those wrong-but-well-formed vectors pass the quarantine check and are persisted as this
        // generation's leaf embeddings — poisoning clustering, retrieval, and summary provenance.
        _weave!.OmitBatchIndex = 3;

        TapestryWeaveOutcome outcome = await CreateWeaver().WeaveAsync(Scope, Settings(), CancellationToken.None);

        Assert.Equal(TapestryWeaveStatus.EmbeddingUnavailable, outcome.Status);

        Assert.Null(await _store!.GetCurrentGenerationAsync(Scope, CancellationToken.None));

        Assert.Equal(0, await _store.ReconcileGenerationsAsync(CancellationToken.None));

    }

    [SkippableFact]
    public async Task WeaveAsync_ACancelledBuildLeavesNoVisibleGeneration()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedTenChunksAsync(this);

        using CancellationTokenSource cancellation = new();

        _summarizer!.OnSummarize = () => cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateWeaver().WeaveAsync(Scope, Settings(), cancellation.Token));

        Assert.Null(await _store!.GetCurrentGenerationAsync(Scope, CancellationToken.None));

        Assert.Equal(0, await _store.ReconcileGenerationsAsync(CancellationToken.None));

    }

    [SkippableFact]
    public async Task WeaveAsync_SingleLeafCorpusPublishesALeafOnlyTree()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedChunksAsync(("only", "solo.cs", "the only chunk"));

        TapestryWeaveOutcome outcome = await CreateWeaver().WeaveAsync(Scope, Settings(), CancellationToken.None);

        Assert.True(outcome.Status == TapestryWeaveStatus.Woven, $"expected Woven, got {outcome.Status}. Log:\n{_logger}");

        Assert.Equal(TapestryTerminalReason.LeafOnly, outcome.TerminalReason);

        Assert.Equal(1, outcome.LayerCount);

        Assert.Equal(0, outcome.SummaryCallsMade);

    }

    [SkippableFact]
    public async Task WeaveAsync_RootSummaryNeverExceedsTheChildCountBound()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        // A summarizer that accepts any layer would otherwise let the whole-layer root shortcut mint a
        // root with more children than the bound allows.
        _summarizer!.AlwaysFits = true;

        await SeedChunksAsync(
            [.. Enumerable.Range(0, 12).Select(index => ($"c{index:D2}", "f.cs", $"body {index}"))]);

        _ = await CreateWeaver().WeaveAsync(
            Scope,
            Settings(target: 2, maxChildren: 3),
            CancellationToken.None);

        TapestryGeneration current = (await _store!.GetCurrentGenerationAsync(Scope, CancellationToken.None))!;

        for (int layer = 1; layer <= current.LayerCount; layer++)
        {

            foreach (TapestryNode node in await _store.GetLayerNodesAsync(
                current.GenerationId,
                layer,
                CancellationToken.None))
            {

                int children = await CountChildrenAsync(node.NodeId);

                Assert.True(children <= 3, $"summary {node.NodeId} at layer {layer} had {children} children");

            }

        }

    }

    [SkippableFact]
    public async Task WeaveAsync_ANodeTooLargeToSummarizeIsCarriedRatherThanBlockingTheTree()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        // One excerpt that cannot fit any summary request must not permanently prevent the scope's
        // tree from building — Arcanum never re-chunks source material to make a call fit.
        _summarizer!.OversizedContentSubstring = "gigantic";

        await SeedChunksAsync(
            [
                ("huge", "big.cs", "a gigantic excerpt that no summary request could ever hold"),
                .. Enumerable.Range(0, 8).Select(index => ($"c{index:D2}", "f.cs", $"ordinary body {index}")),
            ]);

        TapestryWeaveOutcome outcome = await CreateWeaver().WeaveAsync(
            Scope,
            Settings(target: 2, maxChildren: 3),
            CancellationToken.None);

        Assert.True(outcome.Status == TapestryWeaveStatus.Woven, $"expected Woven, got {outcome.Status}. Log:\n{_logger}");

        TapestryGeneration current = (await _store!.GetCurrentGenerationAsync(Scope, CancellationToken.None))!;

        Assert.Equal(9, (await _store.GetLayerNodesAsync(current.GenerationId, 0, CancellationToken.None)).Count);

        Assert.NotEmpty(await _store.GetLayerNodesAsync(current.GenerationId, 1, CancellationToken.None));

    }

    [SkippableFact]
    public async Task WeaveAsync_MergingAnUndersizedClusterNeverCrossesTheTokenBound()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        // Undersized-cluster merging weighs only the child-count bound, so the oversized excerpt's
        // singleton can be absorbed into a sibling before the carry check ever sees it. That both
        // defeats the documented escape hatch for a node whose own text exceeds one summary request
        // and hands the summarizer a cluster that is guaranteed not to fit — which, against a real
        // provider, fails the generation permanently and re-bills every summary before it.
        _summarizer!.OversizedContentSubstring = "gigantic";

        await SeedChunksAsync(
            [
                ("huge", "big.cs", "a gigantic excerpt that no summary request could ever hold"),
                .. Enumerable.Range(0, 8).Select(index => ($"c{index:D2}", "f.cs", $"ordinary body {index}")),
            ]);

        TapestryWeaveOutcome outcome = await CreateWeaver().WeaveAsync(
            Scope,
            Settings(target: 2, maxChildren: 3),
            CancellationToken.None);

        Assert.True(outcome.Status == TapestryWeaveStatus.Woven, $"expected Woven, got {outcome.Status}. Log:\n{_logger}");

        Assert.Equal(0, _summarizer.OversizedSummaryCalls);

    }

    /// <summary>
    /// A deterministic stand-in for The Weave: content-derived vectors so the same corpus always
    /// yields the same directions, with switches for the degradation paths.
    /// </summary>
    private sealed class FakeWeaveService : IWeaveService
    {

        public bool Available { get; set; } = true;

        public float[]? ConstantVector { get; set; }

        public string? PoisonContentSubstring { get; set; }

        /// <summary>Drops one input from the middle of every batch response, shortening it by one.</summary>
        public int? OmitBatchIndex { get; set; }

        public bool IsAvailable => Available;

        public Task<Result<Embedding<float>>> EmbedAsync(string text, CancellationToken cancellationToken) =>
            Task.FromResult(
                Available
                    ? Result<Embedding<float>>.Success(new Embedding<float>(Vector(text)))
                    : Result<Embedding<float>>.Failure(new Error(
                        ErrorCodes.Embeddings.ProviderUnavailable,
                        "unavailable")));

        public Task<Result<Embedding<float>[]>> EmbedBatchAsync(
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken)
        {

            if (!Available)
            {

                return Task.FromResult(Result<Embedding<float>[]>.Failure(new Error(
                    ErrorCodes.Embeddings.ProviderUnavailable,
                    "unavailable")));

            }

            IEnumerable<string> answered = OmitBatchIndex is { } omitted
                ? texts.Where((_, index) => index != omitted)
                : texts;

            return Task.FromResult(Result<Embedding<float>[]>.Success(
                [.. answered.Select(text => new Embedding<float>(Vector(text)))]));

        }

        public Task<Result<(string Chunk, int Offset)[]>> ChunkAsync(
            string text,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result<(string Chunk, int Offset)[]>.Success([(text, 0)]));

        private float[] Vector(string text)
        {

            if (PoisonContentSubstring is { } poison
                && text.Contains(poison, StringComparison.Ordinal))
            {

                float[] poisoned = new float[TestDimensions];

                poisoned[0] = float.NaN;

                return poisoned;

            }

            if (ConstantVector is { } constant)
            {

                return [.. constant];

            }

            // A stable hash-derived direction: deterministic per content and spread across the
            // sphere. SHA-256 yields 32 bytes, so successive blocks are hashed until the vector is
            // full rather than indexing past the digest.
            float[] vector = new float[TestDimensions];

            for (int block = 0; block * 32 < TestDimensions; block++)
            {

                byte[] digest = System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes($"{text}#{block}"));

                for (int offset = 0; offset < digest.Length; offset++)
                {

                    int index = (block * 32) + offset;

                    if (index >= TestDimensions)
                    {

                        break;

                    }

                    vector[index] = (digest[offset] / 255f) - 0.5f + 0.01f;

                }

            }

            return vector;

        }

    }

    /// <summary>
    /// A summarizer whose output is deterministic only so the tests stay readable — the production
    /// contract explicitly does not promise reproducible prose, which is why every assertion above is
    /// about membership, lineage, and hashes rather than words.
    /// </summary>
    private sealed class FakeSummarizer : ITapestrySummarizer
    {

        public string? Model { get; set; } = "fake-summary-model";

        public bool FailEverySummary { get; set; }

        public bool AlwaysFits { get; set; }

        public string? OversizedContentSubstring { get; set; }

        public int CallCount { get; private set; }

        /// <summary>The largest child count any fit estimate was asked about.</summary>
        public int LargestFitEstimate { get; private set; }

        /// <summary>Summary calls the summarizer's own admission check would have rejected.</summary>
        public int OversizedSummaryCalls { get; private set; }

        public Action? OnSummarize { get; set; }

        /// <summary>Fit estimates asked for — the whole-cluster tokenization the plan phase pays for.</summary>
        public int FitEstimateCalls { get; private set; }

        /// <summary>Runs inside every fit estimate, so a test can stop the sweep mid-plan.</summary>
        public Action? OnFitEstimate { get; set; }

        public string? ResolveSummaryModel() => Model;

        public bool FitsOneRequest(TapestrySummaryRequest request)
        {

            FitEstimateCalls++;

            OnFitEstimate?.Invoke();

            LargestFitEstimate = Math.Max(LargestFitEstimate, request.ChildTexts.Count);

            if (OversizedContentSubstring is { } oversized
                && request.ChildTexts.Any(text => text.Contains(oversized, StringComparison.Ordinal)))
            {

                return false;

            }

            return AlwaysFits || request.ChildTexts.Count <= 3;

        }

        public Task<Result<string>> SummarizeAsync(
            TapestrySummaryRequest request,
            CancellationToken cancellationToken)
        {

            OnSummarize?.Invoke();

            cancellationToken.ThrowIfCancellationRequested();

            CallCount++;

            // The real summarizer does not re-check the fit here: an over-budget request goes to the
            // provider, fails there, and fails the whole generation. Recording it is how a cluster
            // that escaped repartitioning becomes visible to a test.
            if (!FitsOneRequest(request))
            {

                OversizedSummaryCalls++;

            }

            return Task.FromResult(
                FailEverySummary
                    ? Result<string>.Failure(new Error(
                        ErrorCodes.Embeddings.ProviderUnavailable,
                        "summary model unavailable"))
                    : Result<string>.Success(
                        $"summary of {request.ChildTexts.Count} item(s): "
                        + string.Join(" | ", request.ChildTexts.Select(static text =>
                            text.Length <= 24 ? text : text[..24]))));

        }

        public void ResetCounters() => CallCount = 0;

    }

}
