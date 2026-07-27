using System.Data;
using System.Data.Common;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// Phase 7 (RAG / The Weave inspector) — integration tests for the read-only
/// <c>GET /api/workspaces/{id}/files/index/status</c> and
/// <c>GET /api/workspaces/{id}/files/chunks</c> routes. Same <c>[Collection("ApiHost")]</c> harness as
/// the workspace divination endpoint tests; seeds <c>workspace_file_chunks</c> + companion embedding
/// rows directly and asserts status counts, chunk pagination/filtering/preview cap, workspace scoping,
/// and the 404/400 failure shapes.
/// </summary>
[Collection("ApiHost")]
public sealed class WorkspaceIndexInspectorEndpointTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public WorkspaceIndexInspectorEndpointTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    [SkippableFact]
    public async Task Status_UnknownWorkspace_Returns404()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await GetStatusAsync(client, "no-such-workspace");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await AssertErrorCodeAsync(response, "Workspace.NotFound");

    }

    [SkippableFact]
    public async Task Status_WhenDisabled_ReportsDisabledVectorModeAndIndexingDisabled()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        // Default factory has Embeddings.Enabled=false.
        HttpClient client = _factory.CreateAuthenticatedClient();

        WorkspaceInfo workspace = await RegisterWorkspaceAsync(client, _factory.TempHome, "disabled-status");

        HttpResponseMessage response = await GetStatusAsync(client, workspace.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        WorkspaceIndexStatusDto status = await ReadStatusAsync(response);

        Assert.Equal("disabled", status.VectorMode, StringComparer.Ordinal);

        Assert.False(status.IndexingEnabled);

        Assert.Equal(0, status.TotalIndexedFiles);

        Assert.Equal(0, status.TotalChunks);

        Assert.Null(status.OldestIndexedAt);

        Assert.Null(status.NewestIndexedAt);

        Assert.Null(status.EmbeddingsDimensions);

    }

    [SkippableFact]
    public async Task Status_ReportsCountsTimestampsDimensionsAndHonestSkippedNote()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory enabled = CreateEnabledFactory();

        HttpClient client = enabled.CreateAuthenticatedClient();

        WorkspaceInfo workspace = await RegisterWorkspaceAsync(client, enabled.TempHome, "status-happy");

        DateTimeOffset older = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

        DateTimeOffset newer = new DateTimeOffset(2025, 6, 1, 12, 30, 0, TimeSpan.Zero);

        await SeedChunkAsync(enabled, workspace.Path, "src/A.cs", "chunk-a-0", 0, "public class A {}", older, dim: 4);

        await SeedChunkAsync(enabled, workspace.Path, "src/A.cs", "chunk-a-1", 1, "public class A { void B() {} }", older, dim: 4);

        await SeedChunkAsync(enabled, workspace.Path, "src/B.cs", "chunk-b-0", 0, "public class B {}", newer, dim: 4);

        HttpResponseMessage response = await GetStatusAsync(client, workspace.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        WorkspaceIndexStatusDto status = await ReadStatusAsync(response);

        Assert.True(status.IndexingEnabled);

        Assert.Equal("managed", status.VectorMode, StringComparer.Ordinal);

        Assert.Equal(2, status.TotalIndexedFiles);

        Assert.Equal(3, status.TotalChunks);

        Assert.Equal(older, status.OldestIndexedAt);

        Assert.Equal(newer, status.NewestIndexedAt);

        Assert.Equal(4, status.EmbeddingsDimensions);

        Assert.Contains("not currently persisted", status.SkippedFilesNote, StringComparison.Ordinal);

    }

    [SkippableFact]
    public async Task Status_IsScopedToTheRequestedWorkspace()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory enabled = CreateEnabledFactory();

        HttpClient client = enabled.CreateAuthenticatedClient();

        WorkspaceInfo target = await RegisterWorkspaceAsync(client, enabled.TempHome, "status-scope-target");

        WorkspaceInfo other = await RegisterWorkspaceAsync(client, enabled.TempHome, "status-scope-other");

        await SeedChunkAsync(enabled, target.Path, "src/Target.cs", "chunk-target", 0, "target", DateTimeOffset.UtcNow, dim: 3);

        await SeedChunkAsync(enabled, other.Path, "src/Other.cs", "chunk-other", 0, "other", DateTimeOffset.UtcNow, dim: 3);

        HttpResponseMessage response = await GetStatusAsync(client, target.Id);

        WorkspaceIndexStatusDto status = await ReadStatusAsync(response);

        Assert.Equal(1, status.TotalIndexedFiles);

        Assert.Equal(1, status.TotalChunks);

    }

    [SkippableFact]
    public async Task Chunks_UnknownWorkspace_Returns404()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await GetChunksAsync(client, "no-such-workspace", null, null, null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await AssertErrorCodeAsync(response, "Workspace.NotFound");

    }

    [SkippableFact]
    public async Task Chunks_RootedRelativePath_Returns400PathTraversal()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        WorkspaceInfo workspace = await RegisterWorkspaceAsync(client, _factory.TempHome, "chunks-rooted");

        HttpResponseMessage response = await GetChunksAsync(client, workspace.Id, "/etc/passwd", null, null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await AssertErrorCodeAsync(response, "Workspace.PathTraversal");

    }

    [SkippableFact]
    public async Task Chunks_ParentSegmentRelativePath_Returns400PathTraversal()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        WorkspaceInfo workspace = await RegisterWorkspaceAsync(client, _factory.TempHome, "chunks-parent");

        HttpResponseMessage response = await GetChunksAsync(client, workspace.Id, "../escape.cs", null, null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await AssertErrorCodeAsync(response, "Workspace.PathTraversal");

    }

    [SkippableFact]
    public async Task Chunks_EmptyWorkspace_ReturnsEmptyPageWith200()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        WorkspaceInfo workspace = await RegisterWorkspaceAsync(client, _factory.TempHome, "chunks-empty");

        HttpResponseMessage response = await GetChunksAsync(client, workspace.Id, null, null, null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        WorkspaceFileChunkPage page = await ReadPageAsync(response);

        Assert.Empty(page.Chunks);

        Assert.Equal(0, page.Total);

        Assert.False(page.HasMore);

    }

    [SkippableFact]
    public async Task Chunks_ReturnsOrderedPaginatedPreviewsWithTotalChunksForFile()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory enabled = CreateEnabledFactory();

        HttpClient client = enabled.CreateAuthenticatedClient();

        WorkspaceInfo workspace = await RegisterWorkspaceAsync(client, enabled.TempHome, "chunks-page");

        await SeedChunkAsync(enabled, workspace.Path, "src/A.cs", "chunk-a-0", 0, "aaa", DateTimeOffset.UtcNow, dim: 3);

        await SeedChunkAsync(enabled, workspace.Path, "src/A.cs", "chunk-a-1", 1, "bbb", DateTimeOffset.UtcNow, dim: 3);

        await SeedChunkAsync(enabled, workspace.Path, "src/B.cs", "chunk-b-0", 0, "ccc", DateTimeOffset.UtcNow, dim: 3);

        HttpResponseMessage response = await GetChunksAsync(client, workspace.Id, null, limit: 2, offset: 0);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        WorkspaceFileChunkPage page = await ReadPageAsync(response);

        Assert.Equal(2, page.Chunks.Length);

        Assert.Equal(3, page.Total);

        Assert.True(page.HasMore);

        // Ordered by RelativePath then ChunkIndex: A.cs[0], A.cs[1] on the first page.
        Assert.Equal("src/A.cs", page.Chunks[0].RelativePath);

        Assert.Equal(0, page.Chunks[0].ChunkIndex);

        Assert.Equal(2, page.Chunks[0].TotalChunksForFile);

        Assert.Equal(1, page.Chunks[1].ChunkIndex);

        Assert.Equal(2, page.Chunks[1].TotalChunksForFile);

        // Second page.
        HttpResponseMessage page2 = await GetChunksAsync(client, workspace.Id, null, limit: 2, offset: 2);

        WorkspaceFileChunkPage next = await ReadPageAsync(page2);

        Assert.Single(next.Chunks);

        Assert.False(next.HasMore);

        Assert.Equal("src/B.cs", next.Chunks[0].RelativePath);

        Assert.Equal(1, next.Chunks[0].TotalChunksForFile);

    }

    [SkippableFact]
    public async Task Chunks_FiltersByRelativePathAndStaysScopedToWorkspace()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory enabled = CreateEnabledFactory();

        HttpClient client = enabled.CreateAuthenticatedClient();

        WorkspaceInfo target = await RegisterWorkspaceAsync(client, enabled.TempHome, "chunks-filter-target");

        WorkspaceInfo other = await RegisterWorkspaceAsync(client, enabled.TempHome, "chunks-filter-other");

        string sharedRelativePath = Path.Combine("src", "Shared.cs");

        await SeedChunkAsync(enabled, target.Path, sharedRelativePath, "chunk-target-shared", 0, "target shared", DateTimeOffset.UtcNow, dim: 3);

        await SeedChunkAsync(enabled, target.Path, Path.Combine("src", "Other.cs"), "chunk-target-other", 0, "target other", DateTimeOffset.UtcNow, dim: 3);

        // Same relative path under a different workspace must NOT leak into the target's filtered page.
        await SeedChunkAsync(enabled, other.Path, sharedRelativePath, "chunk-other-shared", 0, "other shared", DateTimeOffset.UtcNow, dim: 3);

        HttpResponseMessage response = await GetChunksAsync(client, target.Id, sharedRelativePath, null, null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        WorkspaceFileChunkPage page = await ReadPageAsync(response);

        WorkspaceFileChunkDto hit = Assert.Single(page.Chunks);

        Assert.Equal(sharedRelativePath, hit.RelativePath);

        Assert.Equal("chunk-target-shared", hit.ChunkId, StringComparer.Ordinal);

        Assert.Equal(1, page.Total);

        Assert.Equal(sharedRelativePath, page.RelativePathFilter, StringComparer.Ordinal);

    }

    [SkippableFact]
    public async Task Chunks_PreviewIsCappedTo500Chars()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory enabled = CreateEnabledFactory();

        HttpClient client = enabled.CreateAuthenticatedClient();

        WorkspaceInfo workspace = await RegisterWorkspaceAsync(client, enabled.TempHome, "chunks-preview");

        string longContent = new('x', 1_200);

        await SeedChunkAsync(enabled, workspace.Path, "src/Big.cs", "chunk-big", 0, longContent, DateTimeOffset.UtcNow, dim: 3);

        HttpResponseMessage response = await GetChunksAsync(client, workspace.Id, null, null, null);

        WorkspaceFileChunkPage page = await ReadPageAsync(response);

        WorkspaceFileChunkDto hit = Assert.Single(page.Chunks);

        Assert.True(hit.ContentPreview.Length <= 500);

        Assert.Equal(longContent[..500], hit.ContentPreview);

        // CharLength reports the full chunk length, not the preview.
        Assert.Equal(1_200, hit.CharLength);

    }

    private static ArcanumWebApplicationFactory CreateEnabledFactory() =>
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
        };

    private static async Task<HttpResponseMessage> GetStatusAsync(HttpClient client, string workspaceId) =>
        await client.GetAsync($"/api/workspaces/{workspaceId}/files/index/status");

    private static async Task<HttpResponseMessage> GetChunksAsync(HttpClient client, string workspaceId, string? relativePath, int? limit, int? offset)
    {

        List<string> queryParts = [];

        if (!string.IsNullOrEmpty(relativePath))
        {

            queryParts.Add($"relativePath={Uri.EscapeDataString(relativePath)}");

        }

        if (limit is { } l)
        {

            queryParts.Add($"limit={l.ToString(System.Globalization.CultureInfo.InvariantCulture)}");

        }

        if (offset is { } o)
        {

            queryParts.Add($"offset={o.ToString(System.Globalization.CultureInfo.InvariantCulture)}");

        }

        string query = queryParts.Count > 0 ? "?" + string.Join('&', queryParts) : string.Empty;

        return await client.GetAsync($"/api/workspaces/{workspaceId}/files/chunks{query}");

    }

    private static async Task<WorkspaceInfo> RegisterWorkspaceAsync(HttpClient client, string tempHome, string suffix)
    {

        string root = Path.Combine(tempHome, $"workspace-inspector-{suffix}-{Guid.NewGuid():N}");

        Directory.CreateDirectory(root);

        CreateWorkspaceRequest request = new(Name: $"test-inspector-{suffix}-{Guid.NewGuid():N}", Path: root, Type: WorkspaceType.Custom);

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

    private static async Task<WorkspaceIndexStatusDto> ReadStatusAsync(HttpResponseMessage response)
    {

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<WorkspaceIndexStatusDto>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseWorkspaceIndexStatusDto);

        Assert.NotNull(body);

        Assert.True(body.IsSuccess);

        Assert.NotNull(body.Data);

        return body.Data!;

    }

    private static async Task<WorkspaceFileChunkPage> ReadPageAsync(HttpResponseMessage response)
    {

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<WorkspaceFileChunkPage>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseWorkspaceFileChunkPage);

        Assert.NotNull(body);

        Assert.True(body.IsSuccess);

        Assert.NotNull(body.Data);

        return body.Data!;

    }

    private static async Task AssertErrorCodeAsync(HttpResponseMessage response, string expectedCode)
    {

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<WorkspaceFileChunkPage>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseWorkspaceFileChunkPage);

        Assert.NotNull(body);

        Assert.False(body.IsSuccess);

        Assert.Equal(expectedCode, body.Error?.Code);

    }

    private static async Task SeedChunkAsync(
        ArcanumWebApplicationFactory factory,
        string workspacePath,
        string relativePath,
        string chunkId,
        int chunkIndex,
        string content,
        DateTimeOffset indexedAt,
        int dim)
    {

        using IServiceScope scope = factory.Services.CreateScope();

        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        DbConnection connection = db.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {

            await connection.OpenAsync();

        }

        string nowIso = indexedAt.ToString("o", System.Globalization.CultureInfo.InvariantCulture);

        await using DbCommand chunkCmd = connection.CreateCommand();

        chunkCmd.CommandText =
            """
            INSERT INTO "workspace_file_chunks"
                ("ChunkId", "WorkspacePath", "RelativePath", "ChunkIndex", "Content", "CharOffset", "CharLength", "FileLastWriteTime", "IndexedAt")
            VALUES
                (@chunkId, @workspacePath, @relativePath, @chunkIndex, @content, @charOffset, @charLength, @fileLastWriteTime, @indexedAt)
            """;

        AddParameter(chunkCmd, "@chunkId", chunkId);

        AddParameter(chunkCmd, "@workspacePath", workspacePath);

        AddParameter(chunkCmd, "@relativePath", relativePath);

        AddParameter(chunkCmd, "@chunkIndex", chunkIndex);

        AddParameter(chunkCmd, "@content", content);

        AddParameter(chunkCmd, "@charOffset", chunkIndex * content.Length);

        AddParameter(chunkCmd, "@charLength", content.Length);

        AddParameter(chunkCmd, "@fileLastWriteTime", nowIso);

        AddParameter(chunkCmd, "@indexedAt", nowIso);

        _ = await chunkCmd.ExecuteNonQueryAsync();

        await using DbCommand embeddingCmd = connection.CreateCommand();

        embeddingCmd.CommandText =
            """
            INSERT INTO "workspace_file_embeddings" ("ChunkId", "Embedding", "Dim")
            VALUES (@chunkId, @embedding, @dim)
            """;

        float[] vector = new float[dim];

        AddParameter(embeddingCmd, "@chunkId", chunkId);

        AddParameter(embeddingCmd, "@embedding", System.Runtime.InteropServices.MemoryMarshal.AsBytes<float>(vector).ToArray());

        AddParameter(embeddingCmd, "@dim", dim);

        _ = await embeddingCmd.ExecuteNonQueryAsync();

    }

    private static void AddParameter(DbCommand cmd, string name, object value)
    {

        DbParameter parameter = cmd.CreateParameter();

        parameter.ParameterName = name;

        parameter.Value = value;

        cmd.Parameters.Add(parameter);

    }

}
