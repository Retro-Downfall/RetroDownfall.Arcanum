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
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// RAG Phase 3 — <c>POST /api/workspaces/{id}/files/index</c> (manual immediate re-index) integration
/// tests.
/// </summary>
[Collection("ApiHost")]
public sealed class WorkspaceReindexEndpointTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public WorkspaceReindexEndpointTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    [SkippableFact]
    public async Task Reindex_WhenDisabled_Returns503()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        WorkspaceInfo workspace = await RegisterWorkspaceAsync(client, _factory.TempHome);

        HttpResponseMessage response = await client.PostAsync($"/api/workspaces/{workspace.Id}/files/index", content: null);

        // FeatureDisabled means an operator turned this off in config, not that the caller lacks
        // permission, so it maps to 503 (retry later) rather than 403.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

    }

    [SkippableFact]
    public async Task Reindex_UnknownWorkspace_Returns404()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory enabled = CreateEnabledFactory(new FakeWeaveService());

        HttpClient client = enabled.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync("/api/workspaces/no-such-workspace/files/index", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

    }

    [SkippableFact]
    public async Task Reindex_TriggersIndexingAndPersistsChunks()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        FakeWeaveService weave = new();

        await using ArcanumWebApplicationFactory enabled = CreateEnabledFactory(weave);

        HttpClient client = enabled.CreateAuthenticatedClient();

        WorkspaceInfo workspace = await RegisterWorkspaceAsync(client, enabled.TempHome);

        File.WriteAllText(Path.Combine(workspace.Path, "notes.md"), "# Reindex smoke test\n\nSome content to chunk and embed.");

        HttpResponseMessage response = await client.PostAsync($"/api/workspaces/{workspace.Id}/files/index", content: null);

        // The re-index now runs in the background (see WorkspaceDivinationEndpoints) rather than being
        // awaited inline, so the endpoint acknowledges the request with 202 before indexing completes.
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<bool>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseBoolean);

        Assert.NotNull(body);

        Assert.True(body.IsSuccess);

        Assert.True(body.Data);

        int chunkCount = await PollUntilChunksPersistedAsync(enabled, workspace.Path);

        Assert.True(chunkCount > 0, "Expected the background re-index to have persisted at least one workspace_file_chunks row.");

    }

    [SkippableFact]
    public async Task Reindex_ReturnsBeforeBackgroundIndexingCompletes()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        // Blocks EmbedBatchAsync until the test explicitly releases it, so a response that arrives
        // before the release proves the endpoint did not await the re-index inline.
        SlowFakeWeaveService weave = new();

        await using ArcanumWebApplicationFactory enabled = CreateEnabledFactory(weave);

        HttpClient client = enabled.CreateAuthenticatedClient();

        WorkspaceInfo workspace = await RegisterWorkspaceAsync(client, enabled.TempHome);

        File.WriteAllText(Path.Combine(workspace.Path, "notes.md"), "# Slow reindex test\n\nSome content.");

        // If the endpoint still awaited IndexNowAsync inline, this call would hang until
        // ReleaseEmbedBatch() below runs — which only happens after the response is already in hand
        // — and the deadline would fire first.
        using CancellationTokenSource requestTimeout = new(TimeSpan.FromSeconds(10));

        HttpResponseMessage response = await client.PostAsync(
            $"/api/workspaces/{workspace.Id}/files/index",
            content: null,
            requestTimeout.Token);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        weave.ReleaseEmbedBatch();

        int chunkCount = await PollUntilChunksPersistedAsync(enabled, workspace.Path);

        Assert.True(chunkCount > 0, "Expected the background re-index to complete once released.");

    }

    private static ArcanumWebApplicationFactory CreateEnabledFactory(IWeaveService weaveService) =>
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

            },
        };

    private static async Task<WorkspaceInfo> RegisterWorkspaceAsync(HttpClient client, string tempHome)
    {

        string root = Path.Combine(tempHome, $"workspace-reindex-{Guid.NewGuid():N}");

        Directory.CreateDirectory(root);

        CreateWorkspaceRequest request = new(Name: $"test-reindex-{Guid.NewGuid():N}", Path: root, Type: WorkspaceType.Custom);

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

    private static async Task<int> PollUntilChunksPersistedAsync(ArcanumWebApplicationFactory factory, string workspacePath)
    {

        TimeSpan deadline = TimeSpan.FromSeconds(10);

        DateTime start = DateTime.UtcNow;

        while (DateTime.UtcNow - start < deadline)
        {

            int count = await CountChunksForWorkspaceAsync(factory, workspacePath);

            if (count > 0)
            {

                return count;

            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));

        }

        return await CountChunksForWorkspaceAsync(factory, workspacePath);

    }

    private static async Task<int> CountChunksForWorkspaceAsync(ArcanumWebApplicationFactory factory, string workspacePath)
    {

        using IServiceScope scope = factory.Services.CreateScope();

        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        DbConnection connection = db.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {

            await connection.OpenAsync();

        }

        await using DbCommand cmd = connection.CreateCommand();

        cmd.CommandText = """SELECT COUNT(*) FROM "workspace_file_chunks" WHERE "WorkspacePath" = @workspacePath;""";

        DbParameter param = cmd.CreateParameter();

        param.ParameterName = "@workspacePath";

        param.Value = workspacePath;

        cmd.Parameters.Add(param);

        object? result = await cmd.ExecuteScalarAsync();

        return Convert.ToInt32(result);

    }

    private sealed class FakeWeaveService : IWeaveService
    {

        public bool IsAvailable => true;

        public Task<Result<Embedding<float>>> EmbedAsync(string text, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by the manual re-index endpoint.");

        public Task<Result<Embedding<float>[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
        {

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
    /// Like <see cref="FakeWeaveService"/>, but <see cref="EmbedBatchAsync"/> blocks until
    /// <see cref="ReleaseEmbedBatch"/> is called — used to prove the manual re-index endpoint responds
    /// before the background indexing work completes (see
    /// <see cref="Reindex_ReturnsBeforeBackgroundIndexingCompletes"/>).
    /// </summary>
    private sealed class SlowFakeWeaveService : IWeaveService
    {

        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int EmbedBatchCallCount { get; private set; }

        public bool IsAvailable => true;

        public void ReleaseEmbedBatch() => _gate.TrySetResult();

        public Task<Result<Embedding<float>>> EmbedAsync(string text, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by the manual re-index endpoint.");

        public async Task<Result<Embedding<float>[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
        {

            EmbedBatchCallCount++;

            await _gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

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

}
