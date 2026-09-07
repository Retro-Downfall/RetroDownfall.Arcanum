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
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class WorkspaceIndexAliasEndpointTests
{
    [SkippableFact]
    public async Task Closed_indexing_intake_returns_the_typed_unavailable_error_as_503()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateFactory();

        using HttpClient client = factory.CreateAuthenticatedClient();

        string root = Path.Combine(factory.TempHome, "ClosedIntake");

        Directory.CreateDirectory(root);

        WorkspaceInfo workspace = await RegisterAsync(client, "closed", root);

        WorkspaceIndexingService indexing = factory.Services.GetRequiredService<WorkspaceIndexingService>();

        await indexing.StopAsync(CancellationToken.None);

        using HttpResponseMessage response = await client.PostAsync($"/api/workspaces/{workspace.Id}/files/index", content: null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        ApiResponse<bool>? body = JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(), ArcanumJsonContext.Default.ApiResponseBoolean);

        Assert.NotNull(body);

        Assert.False(body.IsSuccess);

        Assert.Equal(ErrorCodes.Workspace.IndexingUnavailable, body.Error?.Code);
    }

    [SkippableTheory]
    [InlineData("separator-first", "status")]
    [InlineData("separator-first", "chunks")]
    [InlineData("separator-first", "divine")]
    [InlineData("separator-second", "status")]
    [InlineData("separator-second", "chunks")]
    [InlineData("separator-second", "divine")]
    [InlineData("windows-case", "status")]
    [InlineData("windows-case", "chunks")]
    [InlineData("windows-case", "divine")]
    [InlineData("cold-durable", "status")]
    [InlineData("cold-durable", "chunks")]
    [InlineData("cold-durable", "divine")]
    public async Task Indexed_alias_is_visible_without_changing_persisted_identity_or_crossing_workspace_scope(string alias, string reader)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Skip.If(alias == "windows-case" && !OperatingSystem.IsWindows(), "Windows path comparison only.");

        await using ArcanumWebApplicationFactory factory = CreateFactory();

        using HttpClient client = factory.CreateAuthenticatedClient();

        string root = Path.Combine(factory.TempHome, "AliasTarget");

        Directory.CreateDirectory(root);

        File.WriteAllText(Path.Combine(root, "target.cs"), "public class AliasTarget {}");

        string indexedPath = alias is "separator-first" or "cold-durable" ? root + Path.DirectorySeparatorChar : root;

        string requestedPath = alias switch
        {
            "separator-first" => root,
            "separator-second" => root + Path.DirectorySeparatorChar,
            "cold-durable" => indexedPath,
            _ => root.ToUpperInvariant(),
        };

        WorkspaceIndexingService indexing = factory.Services.GetRequiredService<WorkspaceIndexingService>();

        indexing.RegisterWorkspace(indexedPath);

        WorkspaceInfo target = await RegisterAsync(client, "target", requestedPath);

        string otherPath = Path.Combine(factory.TempHome, "OtherWorkspace");

        Directory.CreateDirectory(otherPath);

        File.WriteAllText(Path.Combine(otherPath, "other.cs"), "public class OtherWorkspace {}");

        _ = await RegisterAsync(client, "other", otherPath);

        Assert.True(indexing.QueueIndexNow(requestedPath).IsSuccess);

        Assert.True(indexing.QueueIndexNow(otherPath).IsSuccess);

        await DrainAsync(indexing);

        (string Path, string Id)[] before = await ReadStoredIdentityAsync(factory, "target.cs");

        Assert.Equal(indexedPath, Assert.Single(before).Path);

        Assert.True(indexing.QueueIndexNow(requestedPath).IsSuccess);

        await DrainAsync(indexing);

        Assert.Equal(before, await ReadStoredIdentityAsync(factory, "target.cs"));

        if (alias == "cold-durable")
        {
            indexing.UnregisterWorkspace(indexedPath);

            Assert.Equal(indexedPath, indexing.ResolveIndexedWorkspacePath(target.Path));
        }

        if (reader == "status")
        {
            string json = await client.GetStringAsync($"/api/workspaces/{target.Id}/files/index/status");

            WorkspaceIndexStatusDto status = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseWorkspaceIndexStatusDto)!.Data!;

            Assert.Equal(requestedPath, status.WorkspacePath);

            Assert.Equal(1, status.TotalChunks);

            Assert.Equal(1, status.TotalIndexedFiles);

            Assert.Equal(3, status.EmbeddingsDimensions);
        }
        else if (reader == "chunks")
        {
            string json = await client.GetStringAsync($"/api/workspaces/{target.Id}/files/chunks?limit=1&offset=0");

            WorkspaceFileChunkPage page = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseWorkspaceFileChunkPage)!.Data!;

            Assert.Equal(1, page.Total);

            Assert.Equal(before[0].Id, Assert.Single(page.Chunks).ChunkId);

            Assert.Equal(1, page.Chunks[0].TotalChunksForFile);

            Assert.Equal("target.cs", page.Chunks[0].RelativePath);
        }
        else
        {
            string payload = JsonSerializer.Serialize(new WorkspaceSemanticSearchRequest("target"), ArcanumJsonContext.Default.WorkspaceSemanticSearchRequest);

            using HttpResponseMessage response = await client.PostAsync($"/api/workspaces/{target.Id}/files/divine", new StringContent(payload, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            string json = await response.Content.ReadAsStringAsync();

            WorkspaceSearchResult result = Assert.Single(JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseWorkspaceSearchResultArray)!.Data!);

            Assert.Equal("target.cs", result.RelativePath);

            Assert.Equal(1, result.TotalChunks);
        }
    }

    private static ArcanumWebApplicationFactory CreateFactory() => new()
    {
        SettingsOverride = settings => settings with
        {
            Features = settings.Features with { Embeddings = true, CodebaseRetrieval = true },
            Integrations = settings.Integrations with
            {
                Embeddings = settings.Integrations.Embeddings with { Provider = "test", Model = "test-embed" },
            },
        },
        ServiceOverrides = services =>
        {
            services.RemoveAll<IWeaveService>();

            services.AddSingleton<IWeaveService>(new AliasWeave());
        },
    };

    private static async Task<WorkspaceInfo> RegisterAsync(HttpClient client, string name, string path)
    {
        string payload = JsonSerializer.Serialize(new CreateWorkspaceRequest(name, path, WorkspaceType.Custom), ArcanumJsonContext.Default.CreateWorkspaceRequest);

        using HttpResponseMessage response = await client.PostAsync("/api/workspaces", new StringContent(payload, Encoding.UTF8, "application/json"));

        response.EnsureSuccessStatusCode();

        return JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(), ArcanumJsonContext.Default.ApiResponseWorkspaceInfo)!.Data!;
    }

    private static async Task DrainAsync(WorkspaceIndexingService indexing)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));

        while (indexing.GetSchedulerSnapshot() is not { ActiveCount: 0, QueuedCount: 0 })
        {
            await Task.Delay(10, deadline.Token);
        }
    }

    private static async Task<(string Path, string Id)[]> ReadStoredIdentityAsync(ArcanumWebApplicationFactory factory, string relativePath)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        await db.Database.OpenConnectionAsync();

        await using DbCommand command = db.Database.GetDbConnection().CreateCommand();

        command.CommandText = "SELECT \"WorkspacePath\", \"ChunkId\" FROM \"workspace_file_chunks\" WHERE \"RelativePath\" = @path";

        DbParameter parameter = command.CreateParameter();

        parameter.ParameterName = "@path";

        parameter.Value = relativePath;

        command.Parameters.Add(parameter);

        await using DbDataReader reader = await command.ExecuteReaderAsync();

        List<(string Path, string Id)> rows = [];

        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        return rows.ToArray();
    }

    private sealed class AliasWeave : IWeaveService
    {
        public bool IsAvailable => true;

        public Task<Result<Embedding<float>>> EmbedAsync(string text, CancellationToken cancellationToken) =>
            Task.FromResult(Result<Embedding<float>>.Success(new Embedding<float>(new float[] { 1f, 0f, 0f })));

        public Task<Result<Embedding<float>[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken) =>
            Task.FromResult(Result<Embedding<float>[]>.Success(texts.Select(static _ => new Embedding<float>(new float[] { 1f, 0f, 0f })).ToArray()));

        public Task<Result<(string Chunk, int Offset)[]>> ChunkAsync(string text, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
