using System.Data;
using System.Data.Common;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// RAG Phase 3 — <c>POST /api/workspaces/{id}/files/divine</c> integration tests.
/// </summary>
[Collection("ApiHost")]
public sealed class WorkspaceDivinationEndpointTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public WorkspaceDivinationEndpointTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    [SkippableFact]
    public async Task Divine_WhenDisabled_Returns503()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        WorkspaceInfo workspace = await RegisterWorkspaceAsync(client, _factory.TempHome, "disabled");

        HttpResponseMessage response = await PostDivineAsync(client, workspace.Id, new WorkspaceSemanticSearchRequest("hello"));

        // FeatureDisabled means an operator turned this off in config, not that the caller lacks
        // permission, so it maps to 503 (retry later) rather than 403.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        await AssertErrorCodeAsync(response, "Embeddings.FeatureDisabled");

    }

    [SkippableFact]
    public async Task Divine_UnknownWorkspace_Returns404()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory enabled = CreateEnabledFactory(new FakeWeaveService());

        HttpClient client = enabled.CreateAuthenticatedClient();

        HttpResponseMessage response = await PostDivineAsync(client, "no-such-workspace", new WorkspaceSemanticSearchRequest("hello"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await AssertErrorCodeAsync(response, "Workspace.NotFound");

    }

    [SkippableFact]
    public async Task Divine_EmptyQuery_Returns400()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory enabled = CreateEnabledFactory(new FakeWeaveService());

        HttpClient client = enabled.CreateAuthenticatedClient();

        WorkspaceInfo workspace = await RegisterWorkspaceAsync(client, enabled.TempHome, "empty-query");

        HttpResponseMessage response = await PostDivineAsync(client, workspace.Id, new WorkspaceSemanticSearchRequest("   "));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await AssertErrorCodeAsync(response, "Validation.InvalidBody");

    }

    [SkippableFact]
    public async Task Divine_QueryTooLong_Returns400()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory enabled = CreateEnabledFactory(new FakeWeaveService());

        HttpClient client = enabled.CreateAuthenticatedClient();

        WorkspaceInfo workspace = await RegisterWorkspaceAsync(client, enabled.TempHome, "too-long-query");

        string oversizedQuery = new('x', 4_097);

        HttpResponseMessage response = await PostDivineAsync(client, workspace.Id, new WorkspaceSemanticSearchRequest(oversizedQuery));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await AssertErrorCodeAsync(response, "Validation.InvalidBody");

    }

    [SkippableFact]
    public async Task Divine_ProviderUnavailable_Returns503()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory enabled = CreateEnabledFactory(new FakeWeaveService { Available = false });

        HttpClient client = enabled.CreateAuthenticatedClient();

        WorkspaceInfo workspace = await RegisterWorkspaceAsync(client, enabled.TempHome, "unavailable");

        HttpResponseMessage response = await PostDivineAsync(client, workspace.Id, new WorkspaceSemanticSearchRequest("hello"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        await AssertErrorCodeAsync(response, "Embeddings.ProviderUnavailable");

    }

    [SkippableFact]
    public async Task Divine_NoIndexedChunks_ReturnsEmptyResultsWith200()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory enabled = CreateEnabledFactory(new FakeWeaveService());

        HttpClient client = enabled.CreateAuthenticatedClient();

        WorkspaceInfo workspace = await RegisterWorkspaceAsync(client, enabled.TempHome, "no-chunks");

        HttpResponseMessage response = await PostDivineAsync(client, workspace.Id, new WorkspaceSemanticSearchRequest("hello"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        WorkspaceSearchResult[] results = await ReadResultsAsync(response);

        Assert.Empty(results);

    }

    [SkippableFact]
    public async Task Divine_HappyPath_ReturnsOnlyChunksForTheRequestedWorkspace()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        FakeWeaveService weave = new();

        RecordingScopedOrdinaryConnectionFactory connections = new();

        await using ArcanumWebApplicationFactory enabled = CreateEnabledFactory(weave, connections);

        HttpClient client = enabled.CreateAuthenticatedClient();

        WorkspaceInfo targetWorkspace = await RegisterWorkspaceAsync(client, enabled.TempHome, "target");

        WorkspaceInfo otherWorkspace = await RegisterWorkspaceAsync(client, enabled.TempHome, "other");

        await SeedWorkspaceFileChunkAsync(
            enabled,
            targetWorkspace.Path,
            relativePath: "src/Foo.cs",
            chunkId: "chunk-target",
            content: "public class Foo { public void Bar() {} }",
            vector: [1f, 0f, 0f]);

        await SeedWorkspaceFileChunkAsync(
            enabled,
            otherWorkspace.Path,
            relativePath: "src/Baz.cs",
            chunkId: "chunk-other",
            content: "public class Baz {}",
            vector: [1f, 0f, 0f]);

        using ScopedConsumerPause pause = new("WorkspaceDivinationEndpoints.GetTotalChunksByPathAsync");

        Task<HttpResponseMessage> searching = PostDivineAsync(
            client,
            targetWorkspace.Id,
            new WorkspaceSemanticSearchRequest("how does Foo work?"));

        try
        {

            await pause.WaitUntilEnteredAsync();

            Assert.Equal(GrimoireScopedConsumerFinalUseKind.ReaderMaterialized, pause.FinalUse.Kind);

            Assert.Equal(1, pause.FinalUse.Observation);

            Assert.Equal(1, connections.LiveLeaseCountFor(CovenantSqliteConnectionMode.ReadOnly));

        }
        finally
        {

            pause.Release();

            _ = await searching.WaitAsync(TimeSpan.FromSeconds(10));

        }

        HttpResponseMessage response = await searching;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        WorkspaceSearchResult[] results = await ReadResultsAsync(response);

        WorkspaceSearchResult hit = Assert.Single(results);

        Assert.Equal("src/Foo.cs", hit.RelativePath);

        Assert.Contains("public class Foo", hit.ContentPreview, StringComparison.Ordinal);

        Assert.Equal(CovenantSqliteConnectionMode.ReadOnly, connections.Modes[^1]);

        Assert.Equal(0, connections.LiveLeaseCountFor(CovenantSqliteConnectionMode.ReadOnly));

    }

    [SkippableFact]
    public async Task Divine_LimitSmallerThanOtherWorkspacesChunks_StillFindsOwnWorkspaceMatch()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        // Query vector [1,0,0]. The "other" workspace has 3 perfect (similarity 1.0) matches, and the
        // target workspace has exactly 1 lower-similarity (but still above the 0-threshold) match.
        // With Limit=1, an unscoped global top-1 KNN would be entirely dominated by "other"'s perfect
        // matches, so the target's own match would never survive to the workspace-filtered join —
        // this pins that the search is scoped to the target workspace's chunks up front instead.
        FakeWeaveService weave = new() { QueryVector = [1f, 0f, 0f] };

        await using ArcanumWebApplicationFactory enabled = CreateEnabledFactory(weave);

        HttpClient client = enabled.CreateAuthenticatedClient();

        WorkspaceInfo targetWorkspace = await RegisterWorkspaceAsync(client, enabled.TempHome, "target-scoped");

        WorkspaceInfo otherWorkspace = await RegisterWorkspaceAsync(client, enabled.TempHome, "other-dominant");

        for (int i = 0; i < 3; i++)
        {

            await SeedWorkspaceFileChunkAsync(
                enabled,
                otherWorkspace.Path,
                relativePath: $"src/Other{i}.cs",
                chunkId: $"chunk-other-{i}",
                content: $"public class Other{i} {{}}",
                vector: [1f, 0f, 0f]);

        }

        await SeedWorkspaceFileChunkAsync(
            enabled,
            targetWorkspace.Path,
            relativePath: "src/Target.cs",
            chunkId: "chunk-target",
            content: "public class Target { public void OnlyMethod() {} }",
            vector: [0.5f, 0.5f, 0f]);

        HttpResponseMessage response = await PostDivineAsync(
            client,
            targetWorkspace.Id,
            new WorkspaceSemanticSearchRequest("how does OnlyMethod work?", Limit: 1));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        WorkspaceSearchResult[] results = await ReadResultsAsync(response);

        WorkspaceSearchResult hit = Assert.Single(results);

        Assert.Equal("src/Target.cs", hit.RelativePath);

    }

    private static ArcanumWebApplicationFactory CreateEnabledFactory(
        IWeaveService weaveService,
        RecordingScopedOrdinaryConnectionFactory? connections = null) =>
        new()
        {
            SettingsOverride = settings => settings with
            {
                Features = settings.Features with
                {
                    Embeddings = true,
                    CodebaseRetrieval = true,
                },
                Integrations = settings.Integrations with
                {
                    Embeddings = settings.Integrations.Embeddings with
                    {
                        Provider = "test",
                        Model = "test-embed",
                    },
                },
            },
            ServiceOverrides = services =>
            {
                services.RemoveAll<IWeaveService>();

                services.AddSingleton(weaveService);

                if (connections is not null)
                {

                    services.RemoveAll<IGrimoireOrdinaryConnectionFactory>();

                    services.AddSingleton<IGrimoireOrdinaryConnectionFactory>(connections);

                }

            },
        };

    private static async Task<HttpResponseMessage> PostDivineAsync(HttpClient client, string workspaceId, WorkspaceSemanticSearchRequest request)
    {

        string payload = JsonSerializer.Serialize(request, ArcanumJsonContext.Default.WorkspaceSemanticSearchRequest);

        return await client.PostAsync(
            $"/api/workspaces/{workspaceId}/files/divine",
            new StringContent(payload, Encoding.UTF8, "application/json"));

    }

    private static async Task<WorkspaceInfo> RegisterWorkspaceAsync(HttpClient client, string tempHome, string suffix)
    {

        string root = Path.Combine(tempHome, $"workspace-divine-{suffix}-{Guid.NewGuid():N}");

        Directory.CreateDirectory(root);

        CreateWorkspaceRequest request = new(Name: $"test-divine-{suffix}-{Guid.NewGuid():N}", Path: root, Type: WorkspaceType.Custom);

        HttpResponseMessage response = await client.PostAsync(
            "/api/workspaces",
            new StringContent(
                JsonSerializer.Serialize(request, ArcanumJsonContext.Default.CreateWorkspaceRequest),
                Encoding.UTF8,
                "application/json"));

        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<WorkspaceInfo>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseWorkspaceInfo);

        return body!.Data!;

    }

    private static async Task<WorkspaceSearchResult[]> ReadResultsAsync(HttpResponseMessage response)
    {

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<WorkspaceSearchResult[]>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseWorkspaceSearchResultArray);

        Assert.NotNull(body);

        Assert.True(body.IsSuccess);

        Assert.NotNull(body.Data);

        return body.Data!;

    }

    private static async Task AssertErrorCodeAsync(HttpResponseMessage response, string expectedCode)
    {

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<WorkspaceSearchResult[]>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseWorkspaceSearchResultArray);

        Assert.NotNull(body);

        Assert.False(body.IsSuccess);

        Assert.Equal(expectedCode, body.Error?.Code);

    }

    private static async Task SeedWorkspaceFileChunkAsync(
        ArcanumWebApplicationFactory factory,
        string workspacePath,
        string relativePath,
        string chunkId,
        string content,
        float[] vector)
    {

        using IServiceScope scope = factory.Services.CreateScope();

        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        DbConnection connection = db.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {

            await connection.OpenAsync();

        }

        await using DbCommand chunkCmd = connection.CreateCommand();

        chunkCmd.CommandText =
            """
            INSERT INTO "workspace_file_chunks"
                ("ChunkId", "WorkspacePath", "RelativePath", "ChunkIndex", "Content", "CharOffset", "CharLength", "FileLastWriteTime", "IndexedAt")
            VALUES
                (@chunkId, @workspacePath, @relativePath, 0, @content, 0, @charLength, @now, @now)
            """;

        DateTimeOffset now = DateTimeOffset.UtcNow;

        AddParameter(chunkCmd, "@chunkId", chunkId);

        AddParameter(chunkCmd, "@workspacePath", workspacePath);

        AddParameter(chunkCmd, "@relativePath", relativePath);

        AddParameter(chunkCmd, "@content", content);

        AddParameter(chunkCmd, "@charLength", content.Length);

        AddParameter(chunkCmd, "@now", now.ToString("o"));

        _ = await chunkCmd.ExecuteNonQueryAsync();

        await using DbCommand embeddingCmd = connection.CreateCommand();

        embeddingCmd.CommandText =
            """
            INSERT INTO "workspace_file_embeddings" ("ChunkId", "Embedding", "Dim")
            VALUES (@chunkId, @embedding, @dim)
            """;

        AddParameter(embeddingCmd, "@chunkId", chunkId);

        AddParameter(embeddingCmd, "@embedding", System.Runtime.InteropServices.MemoryMarshal.AsBytes<float>(vector).ToArray());

        AddParameter(embeddingCmd, "@dim", vector.Length);

        _ = await embeddingCmd.ExecuteNonQueryAsync();

    }

    private static void AddParameter(DbCommand cmd, string name, object value)
    {

        DbParameter parameter = cmd.CreateParameter();

        parameter.ParameterName = name;

        parameter.Value = value;

        cmd.Parameters.Add(parameter);

    }

    private sealed class FakeWeaveService : IWeaveService
    {

        public bool Available { get; set; } = true;

        public float[] QueryVector { get; set; } = [1f, 0f, 0f];

        public bool IsAvailable => Available;

        public Task<Result<Embedding<float>>> EmbedAsync(string text, CancellationToken cancellationToken) =>
            Task.FromResult(Result<Embedding<float>>.Success(new Embedding<float>(QueryVector)));

        public Task<Result<Embedding<float>[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by the workspace divination endpoint.");

        public Task<Result<(string Chunk, int Offset)[]>> ChunkAsync(string text, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by the workspace divination endpoint.");

    }

}
