using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Core.Weave.Tapestry.Raptor;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Fixtures;
using Xunit.Sdk;
using Xunit.Abstractions;
using RetroDownfall.Arcanum.Tests.Weave.Tapestry.Raptor;

namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry;

[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed partial class TapestryRecursiveScaleProbe(GrimoireFixture fixture, ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly TapestryScope Scope = new(TapestryScopeKind.Workspace, "/synthetic/raptor-scale");

    private string _path = string.Empty;

    private ArcanumDbContext _db = null!;

    private TapestryStore _store = null!;

    private readonly SyntheticWeave _embedding = new();

    private readonly SyntheticSummary _summary = new();

    private readonly CountingClusterer _clusterer = new();

    private static EmbeddingSettings Settings => new()
    {
        Enabled = true,
        TapestryEnabled = true,
        Provider = "synthetic-offline",
        Model = "recursive-768-v1",
        Dimensions = 768,
        Tapestry = new(),
    };

    public Task InitializeAsync()
    {
        _path = fixture.CopyDatabase();

        _db = fixture.CreateContext(_path);

        _store = new(_db, new WeaveIndexAvailability());

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();

        File.Delete(_path);
    }

    [Fact]
    public async Task Publication_oracle_rejects_a_real_provider_refusal()
    {
        await SeedAsync(24, CancellationToken.None);

        _summary.Refuse = true;

        TapestryWeaveOutcome outcome = await BuildAsync(CancellationToken.None);

        Assert.Equal(TapestryWeaveStatus.Failed, outcome.Status);

        Assert.Null(await _store.GetCurrentGenerationAsync(Scope, CancellationToken.None));

        _ = await Assert.ThrowsAnyAsync<XunitException>(() => ValidatePublishedAsync(outcome, 24, CancellationToken.None));
    }

    [Fact]
    public async Task Publication_oracle_rejects_a_source_removed_after_real_publication()
    {
        await SeedAsync(24, CancellationToken.None);

        TapestryWeaveOutcome outcome = await BuildAsync(CancellationToken.None);

        Assert.Equal(TapestryWeaveStatus.Woven, outcome.Status);

        await SqlAsync("DELETE FROM workspace_file_chunks WHERE WorkspacePath=@scope AND ChunkId='scale-000000'", CancellationToken.None, ("@scope", Scope.Id));

        _ = await Assert.ThrowsAnyAsync<XunitException>(() => ValidatePublishedAsync(outcome, 24, CancellationToken.None));
    }

    [Fact]
    public async Task Unchanged_oracle_rejects_additional_embedding_work_after_up_to_date()
    {
        await SeedAsync(24, CancellationToken.None);

        _ = await BuildAsync(CancellationToken.None);

        Effects before = CaptureEffects();

        TapestryWeaveOutcome unchanged = await BuildAsync(CancellationToken.None);

        Assert.Equal(TapestryWeaveStatus.UpToDate, unchanged.Status);

        _ = await _embedding.EmbedAsync("unexpected-new-effect", CancellationToken.None);

        _ = Assert.ThrowsAny<XunitException>(() => ValidateNoNewEffects(before));
    }

    [Fact]
    public async Task Linked_build_cancellation_reaches_the_real_weaver_before_embedding()
    {
        await SeedAsync(24, CancellationToken.None);

        using CancellationTokenSource caller = new();

        caller.Cancel();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BuildAsync(caller.Token));

        Assert.Equal(0, _embedding.Calls);

        Assert.Null(await _store.GetCurrentGenerationAsync(Scope, CancellationToken.None));
    }

    [Fact]
    public async Task Publication_oracle_accepts_real_publication_and_unchanged_zero_effects()
    {
        await SeedAsync(24, CancellationToken.None);

        TapestryWeaveOutcome outcome = await BuildAsync(CancellationToken.None);

        Assert.Equal(TapestryWeaveStatus.Woven, outcome.Status);

        await ValidatePublishedAsync(outcome, 24, CancellationToken.None);

        Effects before = CaptureEffects();

        TapestryWeaveOutcome unchanged = await BuildAsync(CancellationToken.None);

        Assert.Equal(TapestryWeaveStatus.UpToDate, unchanged.Status);

        ValidateNoNewEffects(before);
    }

    [Theory]
    [InlineData("summary")]
    [InlineData("clustering")]
    public async Task Unchanged_oracle_rejects_additional_nonembedding_effects(string effect)
    {
        await SeedAsync(24, CancellationToken.None);

        _ = await BuildAsync(CancellationToken.None);

        Effects before = CaptureEffects();

        Assert.Equal(TapestryWeaveStatus.UpToDate, (await BuildAsync(CancellationToken.None)).Status);

        if (effect == "summary")
        {
            _ = await _summary.SummarizeAsync(new(Scope.Kind, Scope.Id, 1, ["a", "b"]), CancellationToken.None);
        }
        else
        {
            _ = _clusterer.Cluster([new("a", new float[] { 1, 0 }), new("b", new float[] { 0, 1 })],
                new(Scope, 1, RaptorStage.Split, "oracle-effect", ["a", "b"]), CancellationToken.None);
        }

        _ = Assert.ThrowsAny<XunitException>(() => ValidateNoNewEffects(before));
    }

    [Fact]
    public async Task Coverage_oracle_rejects_one_omitted_row_from_a_real_published_projection()
    {
        await SeedAsync(24, CancellationToken.None);

        TapestryWeaveOutcome outcome = await BuildAsync(CancellationToken.None);

        IReadOnlyList<TapestryNode> leaves = await _store.GetLayerNodesAsync(outcome.GenerationId!, 0, CancellationToken.None);

        Assert.Equal(24, leaves.Count);

        _ = Assert.ThrowsAny<XunitException>(() => AssertExactLeafCoverage(leaves.Skip(1).ToArray(), 24));
    }

    private static void AssertExactLeafCoverage(IReadOnlyList<TapestryNode> leaves, int expectedLeaves)
    {
        Assert.Equal(Enumerable.Range(0, expectedLeaves).Select(index => "scale-" + index.ToString("D6", CultureInfo.InvariantCulture)),
            leaves.Select(node => node.SourceId).Order(StringComparer.Ordinal));
    }

    private Task<TapestryWeaveOutcome> BuildAsync(CancellationToken caller) => new TapestryWeaver(
        _store, _embedding, _summary, new TapestryBuildBackoff(), TimeProvider.System,
        NullLogger<TapestryWeaver>.Instance, _clusterer).WeaveAsync(Scope, Settings, caller);

    private async Task ValidatePublishedAsync(TapestryWeaveOutcome outcome, int expectedLeaves, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        Assert.Equal(TapestryWeaveStatus.Woven, outcome.Status);

        TapestryGeneration generation = Assert.IsType<TapestryGeneration>(await _store.GetCurrentGenerationAsync(Scope, token));

        Assert.Equal(outcome.GenerationId, generation.GenerationId);

        Assert.Equal(TapestryGenerationStatus.Complete, generation.Status);

        Assert.Equal(RaptorAlgorithmManifest.Version, generation.AlgorithmVersion);

        Assert.Equal(768, generation.EmbeddingDimension);

        Assert.Equal(TapestryHash.OfEmbeddingModel(Settings.Provider, Settings.Model), generation.EmbeddingIdentity);

        TapestryBuildSnapshot snapshot = await _store.ReadBuildSnapshotAsync(Scope, token);

        Assert.Equal(expectedLeaves, snapshot.Corpus.LeafCount);

        Assert.Equal(snapshot.Corpus.Fingerprint, generation.CorpusFingerprint);

        Assert.Equal(snapshot.CorpusRevision, generation.SourceRevision);

        Assert.Equal(outcome.LayerCount, generation.LayerCount);

        Assert.Equal(outcome.NodeCount, generation.NodeCount);

        Assert.Equal(outcome.RootNodeCount, generation.RootNodeCount);

        Assert.Equal(outcome.TerminalReason, generation.TerminalReason);

        Assert.Equal(expectedLeaves, outcome.LeavesEmbedded);

        Assert.Equal(0, outcome.QuarantinedVectors);

        Assert.Equal(1, _embedding.BatchCalls);

        Assert.Equal(expectedLeaves, _embedding.BatchInputs);

        Assert.Equal(outcome.SummaryCallsMade, _summary.Calls);

        Assert.Equal(outcome.SummaryCallsMade, _embedding.SingleCalls);

        List<TapestryNode> nodes = [];

        for (int layer = 0; layer < generation.LayerCount; layer++)
        {
            token.ThrowIfCancellationRequested();

            nodes.AddRange(await _store.GetLayerNodesAsync(generation.GenerationId, layer, token));
        }

        Assert.Equal(generation.NodeCount, nodes.Count);

        Assert.Equal(expectedLeaves + outcome.SummaryCallsMade + outcome.SummariesReused, nodes.Count);

        Dictionary<string, TapestryNode> byId = nodes.ToDictionary(node => node.NodeId, StringComparer.Ordinal);

        TapestryNode[] leaves = nodes.Where(node => node.NodeKind == TapestryNodeKind.Leaf).ToArray();

        AssertExactLeafCoverage(leaves, expectedLeaves);

        foreach (TapestryNode node in nodes)
        {
            token.ThrowIfCancellationRequested();

            Assert.Equal(generation.GenerationId, node.GenerationId);

            Assert.Equal(Scope.Id, node.ScopeId);

            Assert.Equal(Scope.Kind, node.ScopeKind);

            Assert.Equal(768, node.EmbeddingDimension);

            Assert.Equal(TapestryHash.OfContent(node.NodeKind == TapestryNodeKind.Leaf ? node.SourceId : node.Content), node.ContentHash);
        }

        foreach (TapestryNode[] batch in nodes.Chunk(128))
        {
            IReadOnlyDictionary<string, float[]> vectors = await _store.GetNodeEmbeddingsAsync(batch.Select(node => node.NodeId).ToArray(), token);

            Assert.Equal(batch.Length, vectors.Count);

            foreach (float[] vector in vectors.Values)
            {
                Assert.Equal(768, vector.Length);

                Assert.All(vector, value => Assert.True(float.IsFinite(value)));

                Assert.Contains(vector, value => value != 0);
            }
        }

        Dictionary<string, List<string>> children = await ReadChildrenAsync(generation.GenerationId, token);

        foreach ((string parent, List<string> childIds) in children)
        {
            Assert.True(byId.TryGetValue(parent, out TapestryNode? parentNode));

            Assert.Equal(TapestryNodeKind.Summary, parentNode.NodeKind);

            Assert.InRange(childIds.Count, 2, 24);

            Assert.Equal(childIds.Count, childIds.Distinct(StringComparer.Ordinal).Count());

            foreach (string child in childIds)
            {
                Assert.True(byId.TryGetValue(child, out TapestryNode? childNode));

                Assert.True(childNode.Layer < parentNode.Layer);
            }
        }

        Assert.Equal(nodes.Count(node => node.NodeKind == TapestryNodeKind.Summary), children.Count);

        IReadOnlyList<string> roots = await _store.GetRootIdsAsync(generation.GenerationId, token);

        Assert.Equal(generation.RootNodeCount, roots.Count);

        HashSet<string> visited = new(StringComparer.Ordinal);

        Stack<string> pending = new(roots);

        while (pending.TryPop(out string? id))
        {
            token.ThrowIfCancellationRequested();

            Assert.True(byId.ContainsKey(id));

            if (!visited.Add(id)) continue;

            if (children.TryGetValue(id, out List<string>? childIds))
            {
                foreach (string child in childIds) pending.Push(child);
            }
        }

        Assert.Equal(nodes.Count, visited.Count);

        Assert.Equal(expectedLeaves, visited.Count(id => byId[id].NodeKind == TapestryNodeKind.Leaf));
    }

    private async Task<Dictionary<string, List<string>>> ReadChildrenAsync(string generation, CancellationToken token)
    {
        DbConnection connection = _db.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open) await connection.OpenAsync(token);

        await using DbCommand command = connection.CreateCommand();

        command.CommandText = "SELECT ParentNodeId,ChildNodeId,GlobalPosterior,LocalPosterior FROM tapestry_edges WHERE GenerationId=@generation ORDER BY ParentNodeId,ChildNodeId";

        DbParameter parameter = command.CreateParameter();

        parameter.ParameterName = "@generation";

        parameter.Value = generation;

        _ = command.Parameters.Add(parameter);

        Dictionary<string, List<string>> children = new(StringComparer.Ordinal);

        await using DbDataReader reader = await command.ExecuteReaderAsync(token);

        while (await reader.ReadAsync(token))
        {
            string parent = reader.GetString(0);

            if (!children.TryGetValue(parent, out List<string>? ids)) children[parent] = ids = [];

            ids.Add(reader.GetString(1));

            for (int ordinal = 2; ordinal <= 3; ordinal++)
            {
                if (!reader.IsDBNull(ordinal)) Assert.InRange(reader.GetDouble(ordinal), 0, 1);
            }
        }

        return children;
    }

    private Effects CaptureEffects() => new(_embedding.Calls, _summary.Calls, _clusterer.Calls);

    private void ValidateNoNewEffects(Effects before)
    {
        Assert.Equal(before, CaptureEffects());
    }

    private readonly record struct Effects(int EmbeddingCalls, int SummaryCalls, int ClusteringCalls);

    private sealed class CountingClusterer : ITapestryClusterer
    {
        private readonly object _gate = new();

        private int _calls;

        private int _maxLayer;

        private int _layer;

        private int _inputCount;

        private long _memberships;

        private string _digest = TapestryHash.OfParts([]);

        private string _state = "idle";

        internal int Calls { get { lock (_gate) return _calls; } }

        internal int MaxLayer { get { lock (_gate) return _maxLayer; } }

        internal string Describe()
        {
            lock (_gate) return FormattableString.Invariant($"cluster_calls={_calls} max_layer={_maxLayer} last_layer={_layer} last_inputs={_inputCount} cluster_state={_state} membership_paths={_memberships} call_digest={_digest} inner_phase=UNOBSERVED scalar=UNOBSERVED");
        }

        public Result<RaptorClusterResult> Cluster(IReadOnlyList<RaptorPoint> points, TapestryClusterIdentity identity, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (_gate)
            {
                _calls++;

                _maxLayer = Math.Max(_maxLayer, identity.Layer);

                _layer = identity.Layer;

                _inputCount = points.Count;

                _state = "running";

                _digest = TapestryHash.OfParts([_digest, identity.Stage.ToString(), identity.StageKey,
                    identity.MemberDigest, RaptorScaleProbe.CorpusHash(points)]);
            }

            try
            {
                Result<RaptorClusterResult> result = new RaptorClusterer().Cluster(points, identity, cancellationToken);

                lock (_gate)
                {
                    _state = result.IsSuccess ? "completed" : "refused";

                    if (result.IsSuccess)
                    {
                        _memberships = checked(_memberships + result.Value.Clusters.Sum(cluster => (long)cluster.Members.Length));

                        _digest = TapestryHash.OfParts(ResultParts(result.Value).Prepend(_digest));
                    }
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                lock (_gate) _state = "canceled";

                throw;
            }
        }

        private static IEnumerable<string> ResultParts(RaptorClusterResult result)
        {
            foreach (RaptorCluster cluster in result.Clusters)
            {
                yield return cluster.StableId;

                foreach (RaptorMembership member in cluster.Members)
                {
                    yield return FormattableString.Invariant($"{member.PointIndex}/{member.GlobalComponent}/{member.LocalComponent}/{BitConverter.DoubleToInt64Bits(member.GlobalPosterior):X16}/{BitConverter.DoubleToInt64Bits(member.LocalPosterior):X16}/{(int)member.Repair}");
                }
            }

            yield return "unassigned";

            foreach (int index in result.Unassigned) yield return index.ToString(CultureInfo.InvariantCulture);
        }
    }

    private async Task SeedAsync(int count, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        _embedding.Leaves = RaptorScaleProbe.Corpus(count);

        await SqlAsync(
            """
            WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i+1 FROM n WHERE i+1<@count)
            INSERT INTO workspace_file_chunks
                (ChunkId,WorkspacePath,RelativePath,ChunkIndex,Content,CharOffset,CharLength,StartLine,EndLine,FileLastWriteTime,IndexedAt)
            SELECT printf('scale-%06d',i),@scope,printf('scale-%06d.cs',i),0,printf('scale-%06d',i),0,12,1,1,
                '2026-01-01T00:00:00Z','2026-01-01T00:00:00Z' FROM n
            """, token, ("@count", count), ("@scope", Scope.Id));
    }

    private async Task SqlAsync(string sql, CancellationToken token, params (string Name, object Value)[] parameters)
    {
        DbConnection connection = _db.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open) await connection.OpenAsync(token);

        await using DbCommand command = connection.CreateCommand();

        command.CommandText = sql;

        foreach ((string name, object value) in parameters)
        {
            DbParameter parameter = command.CreateParameter();

            parameter.ParameterName = name;

            parameter.Value = value;

            _ = command.Parameters.Add(parameter);
        }

        _ = await command.ExecuteNonQueryAsync(token);
    }

    private sealed class SyntheticWeave : IWeaveService
    {
        internal int Calls { get; private set; }

        internal Action<CancellationToken>? BeforeBatch { get; set; }

        internal RaptorPoint[] Leaves { get; set; } = [];

        internal int BatchCalls { get; private set; }

        internal int BatchInputs { get; private set; }

        internal int SingleCalls { get; private set; }

        public bool IsAvailable => true;

        public Task<Result<Embedding<float>>> EmbedAsync(string text, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Calls++;

            SingleCalls++;

            return Task.FromResult(Result<Embedding<float>>.Success(Vector(text)));
        }

        public Task<Result<Embedding<float>[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
        {
            BeforeBatch?.Invoke(cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            Calls++;

            BatchCalls++;

            BatchInputs = checked(BatchInputs + texts.Count);

            Embedding<float>[] embeddings = new Embedding<float>[texts.Count];

            for (int index = 0; index < texts.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int source = int.Parse(texts[index].AsSpan(6), CultureInfo.InvariantCulture);

                Assert.Equal(Leaves[source].StableId, texts[index]);

                embeddings[index] = Stamp(Leaves[source].Vector);
            }

            return Task.FromResult(Result<Embedding<float>[]>.Success(embeddings));
        }

        public Task<Result<(string Chunk, int Offset)[]>> ChunkAsync(string text, CancellationToken cancellationToken) => throw new NotSupportedException();

        private static Embedding<float> Vector(string text)
        {
            float[] values = new float[768];

            for (int block = 0; block < 24; block++)
            {
                byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text + "#" + block.ToString(CultureInfo.InvariantCulture)));

                for (int offset = 0; offset < 32; offset++) values[(block * 32) + offset] = (hash[offset] - 127) / 128f;
            }

            return Stamp(values);
        }

        private static Embedding<float> Stamp(ReadOnlyMemory<float> values) => new(values)
        {
            AdditionalProperties = new() { [EmbeddingProvenance.MetadataKey] = new EmbeddingProvenance("synthetic-offline", "recursive-768-v1") },
        };
    }

    private sealed class SyntheticSummary : ITapestrySummarizer
    {
        internal bool Refuse { get; set; }

        internal int Calls { get; private set; }

        public string? ResolveSummaryModel() => "synthetic-summary-v1";

        public TapestrySummaryPreparation? Prepare(TapestrySummaryRequest request) => new(
            "synthetic-offline", "synthetic-summary-v1", "ordered-digest-v1", TapestryHash.OfParts(request.ChildTexts));

        public bool FitsOneRequest(TapestrySummaryRequest request) => request.ChildTexts.Count <= 24;

        public Task<Result<string>> SummarizeAsync(TapestrySummaryRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Calls++;

            return Task.FromResult(Refuse
                ? Result<string>.Failure(new Error("Synthetic.Refusal", "offline refusal"))
                : Result<string>.Success("summary-" + TapestryHash.OfParts(request.ChildTexts)));
        }
    }
}
