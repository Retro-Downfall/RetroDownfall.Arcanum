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

namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry;

[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class TapestryRecursiveScaleProbe(GrimoireFixture fixture) : IAsyncLifetime
{
    private static readonly TapestryScope Scope = new(TapestryScopeKind.Workspace, "/synthetic/raptor-scale");

    private string _path = string.Empty;

    private ArcanumDbContext _db = null!;

    private TapestryStore _store = null!;

    private readonly SyntheticWeave _embedding = new();

    private readonly SyntheticSummary _summary = new();

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

        int before = _embedding.Calls;

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

        int before = _embedding.Calls;

        TapestryWeaveOutcome unchanged = await BuildAsync(CancellationToken.None);

        Assert.Equal(TapestryWeaveStatus.UpToDate, unchanged.Status);

        ValidateNoNewEffects(before);
    }

    private Task<TapestryWeaveOutcome> BuildAsync(CancellationToken caller) => new TapestryWeaver(
        _store, _embedding, _summary, new TapestryBuildBackoff(), TimeProvider.System,
        NullLogger<TapestryWeaver>.Instance, new RaptorClusterer()).WeaveAsync(Scope, Settings, caller);

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

        List<TapestryNode> nodes = [];

        for (int layer = 0; layer < generation.LayerCount; layer++)
        {
            token.ThrowIfCancellationRequested();

            nodes.AddRange(await _store.GetLayerNodesAsync(generation.GenerationId, layer, token));
        }

        Assert.Equal(generation.NodeCount, nodes.Count);

        Dictionary<string, TapestryNode> byId = nodes.ToDictionary(node => node.NodeId, StringComparer.Ordinal);

        TapestryNode[] leaves = nodes.Where(node => node.NodeKind == TapestryNodeKind.Leaf).ToArray();

        Assert.Equal(Enumerable.Range(0, expectedLeaves).Select(index => "scale-" + index.ToString("D6", CultureInfo.InvariantCulture)),
            leaves.Select(node => node.SourceId).Order(StringComparer.Ordinal));

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

    private void ValidateNoNewEffects(int before)
    {
        Assert.Equal(before, _embedding.Calls);
    }

    private async Task SeedAsync(int count, CancellationToken token)
    {
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

        public bool IsAvailable => true;

        public Task<Result<Embedding<float>>> EmbedAsync(string text, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Calls++;

            return Task.FromResult(Result<Embedding<float>>.Success(Vector(text)));
        }

        public Task<Result<Embedding<float>[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Calls++;

            return Task.FromResult(Result<Embedding<float>[]>.Success(texts.Select(Vector).ToArray()));
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

            return new(values)
            {
                AdditionalProperties = new() { [EmbeddingProvenance.MetadataKey] = new EmbeddingProvenance("synthetic-offline", "recursive-768-v1") },
            };
        }
    }

    private sealed class SyntheticSummary : ITapestrySummarizer
    {
        internal bool Refuse { get; set; }

        public string? ResolveSummaryModel() => "synthetic-summary-v1";

        public TapestrySummaryPreparation? Prepare(TapestrySummaryRequest request) => new(
            "synthetic-offline", "synthetic-summary-v1", "ordered-digest-v1", TapestryHash.OfParts(request.ChildTexts));

        public bool FitsOneRequest(TapestrySummaryRequest request) => request.ChildTexts.Count <= 24;

        public Task<Result<string>> SummarizeAsync(TapestrySummaryRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(Refuse
                ? Result<string>.Failure(new Error("Synthetic.Refusal", "offline refusal"))
                : Result<string>.Success("summary-" + TapestryHash.OfParts(request.ChildTexts)));
        }
    }
}
