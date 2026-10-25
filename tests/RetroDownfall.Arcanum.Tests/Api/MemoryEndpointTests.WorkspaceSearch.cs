using System.Net;

using System.Net.Http.Json;

using Microsoft.Data.Sqlite;

using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Core.Memory;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage.Entities;

using RetroDownfall.Arcanum.Core.Tower;

using RetroDownfall.Arcanum.Core.Workspaces;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

public sealed partial class MemoryEndpointTests
{
    [SkippableTheory]
    [InlineData(MemorySearchScope.Workspace)]
    [InlineData(MemorySearchScope.All)]
    public async Task Search_workspace_accepts_listed_identity_Guid_formats_and_name_without_crossing_roots(MemorySearchScope searchScope)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        Guid targetId = Guid.Parse("abcdef01-2345-4678-9abc-def012345678");

        await SeedSearchWorkspaceAsync(factory, targetId, "Needle Guild", "target-chunk");

        await SeedSearchWorkspaceAsync(factory, Guid.NewGuid(), "Other Guild", "other-chunk");

        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage listed = await client.GetAsync("/api/workspaces");

        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);

        ApiResponse<WorkspaceInfo[]>? workspaces = await ReadAsync(listed, ArcanumJsonContext.Default.ApiResponseWorkspaceInfoArray);

        WorkspaceInfo target = Assert.Single(workspaces!.Data!, workspace => workspace.Name == "Needle Guild");

        Assert.Equal("abcdef01234546789abcdef012345678", target.Id);

        using HttpResponseMessage unfiltered = await SearchWorkspaceRequestAsync(client, searchScope, null);

        Assert.Equal(HttpStatusCode.OK, unfiltered.StatusCode);

        ApiResponse<MemorySearchResponse>? all = await ReadAsync(unfiltered, ArcanumJsonContext.Default.ApiResponseMemorySearchResponse);

        Assert.Equal(["other-chunk", "target-chunk"], all!.Data!.Results.Select(hit => hit.SourceId).Order().ToArray());

        string[] selectors = [target.Id, targetId.ToString("D"), targetId.ToString("D").ToUpperInvariant(),
            targetId.ToString("N").ToUpperInvariant(), targetId.ToString("B"), targetId.ToString("P"),
            targetId.ToString("X"), "  " + target.Id + "  ", "  nEeDlE gUiLd  "];

        foreach (string selector in selectors)
        {
            using HttpResponseMessage response = await SearchWorkspaceRequestAsync(client, searchScope, selector);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            ApiResponse<MemorySearchResponse>? envelope = await ReadAsync(response, ArcanumJsonContext.Default.ApiResponseMemorySearchResponse);

            MemorySearchResultDto hit = Assert.Single(envelope!.Data!.Results);

            Assert.Equal("target-chunk", hit.SourceId);

            Assert.Equal(MemorySearchScope.Workspace, hit.Scope);

            Assert.Contains("Workspace Needle Guild", hit.Provenance, StringComparison.Ordinal);

            Assert.DoesNotContain(factory.TempHome, hit.Provenance, StringComparison.Ordinal);

            Assert.False(envelope.Data.HasMore);
        }
    }

    [SkippableTheory]
    [InlineData(MemorySearchScope.Workspace, 0)]
    [InlineData(MemorySearchScope.All, 0)]
    [InlineData(MemorySearchScope.All, 1)]
    [InlineData(MemorySearchScope.All, 2)]
    public async Task Search_workspace_rejects_unknown_selector_before_shared_budget_can_skip_resolution(
        MemorySearchScope searchScope,
        int budgetMode)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        using HttpClient client = factory.CreateAuthenticatedClient();

        Guid? sessionId = budgetMode > 0 ? await SeedWorkspaceSearchSessionAsync(factory, budgetMode == 2) : null;

        if (budgetMode > 0)
        {
            using HttpResponseMessage control = await client.PostAsJsonAsync(
                "/api/memory/search",
                new MemorySearchRequest("identityneedle", searchScope, sessionId, Limit: budgetMode == 1 ? 1 : null),
                ArcanumJsonContext.Default.MemorySearchRequest);

            Assert.Equal(HttpStatusCode.OK, control.StatusCode);

            ApiResponse<MemorySearchResponse>? controlEnvelope = await ReadAsync(control, ArcanumJsonContext.Default.ApiResponseMemorySearchResponse);

            MemorySearchScopeStatusDto workspace = Assert.Single(controlEnvelope!.Data!.Scopes!, status => status.Scope == MemorySearchScope.Workspace);

            Assert.Equal(0, workspace.Count);

            Assert.True(workspace.HasMore);
        }

        string[] selectors = ["missing-workspace", "01234567-89ab-4cde-8012-3456789abcde"];

        foreach (string selector in selectors)
        {
            using HttpResponseMessage response = await client.PostAsJsonAsync(
                "/api/memory/search",
                new MemorySearchRequest("identityneedle", searchScope, sessionId, selector, Limit: budgetMode == 1 ? 1 : null),
                ArcanumJsonContext.Default.MemorySearchRequest);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

            ApiResponse<MemorySearchResponse>? envelope = await ReadAsync(response, ArcanumJsonContext.Default.ApiResponseMemorySearchResponse);

            Assert.False(envelope!.IsSuccess);

            Assert.Equal(ErrorCodes.Workspace.NotFound, envelope.Error!.Value.Code);

            Assert.Null(envelope.Data);
        }
    }

    [SkippableTheory]
    [InlineData(MemorySearchScope.Workspace)]
    [InlineData(MemorySearchScope.All)]
    public async Task Search_workspace_omitted_or_blank_selector_keeps_unfiltered_results(MemorySearchScope searchScope)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        await SeedSearchWorkspaceAsync(factory, Guid.NewGuid(), "First Guild", "first-chunk");

        await SeedSearchWorkspaceAsync(factory, Guid.NewGuid(), "Second Guild", "second-chunk");

        using HttpClient client = factory.CreateAuthenticatedClient();

        foreach (string? selector in new string?[] { null, "", "   " })
        {
            using HttpResponseMessage response = await SearchWorkspaceRequestAsync(client, searchScope, selector);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            ApiResponse<MemorySearchResponse>? envelope = await ReadAsync(response, ArcanumJsonContext.Default.ApiResponseMemorySearchResponse);

            Assert.Equal(["first-chunk", "second-chunk"], envelope!.Data!.Results.Select(hit => hit.SourceId).Order().ToArray());

            Assert.False(envelope.Data.HasMore);
        }
    }

    [SkippableFact]
    public async Task Search_session_scope_does_not_consult_an_unselected_workspace()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        Guid sessionId = await SeedWorkspaceSearchSessionAsync(factory);

        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/memory/search",
            new MemorySearchRequest("identityneedle", MemorySearchScope.Session, sessionId, "missing-workspace"),
            ArcanumJsonContext.Default.MemorySearchRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResponse<MemorySearchResponse>? envelope = await ReadAsync(response, ArcanumJsonContext.Default.ApiResponseMemorySearchResponse);

        Assert.Equal(MemorySearchScope.Session, Assert.Single(envelope!.Data!.Results).Scope);
    }

    private static Task<HttpResponseMessage> SearchWorkspaceRequestAsync(HttpClient client, MemorySearchScope searchScope, string? selector) =>
        client.PostAsJsonAsync(
            "/api/memory/search",
            new MemorySearchRequest("identityneedle", searchScope, WorkspaceId: selector),
            ArcanumJsonContext.Default.MemorySearchRequest);

    private static async Task SeedSearchWorkspaceAsync(ArcanumWebApplicationFactory factory, Guid id, string name, string chunkId)
    {
        using IServiceScope scope = factory.Services.CreateScope();

        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        string root = Path.Combine(factory.TempHome, chunkId);

        db.Campaigns.Add(new Campaign
        {
            Id = id,
            Name = name,
            NameLower = name.ToLowerInvariant(),
            Path = root,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync();

        await db.Database.OpenConnectionAsync();

        using SqliteCommand command = ((SqliteConnection)db.Database.GetDbConnection()).CreateCommand();

        command.CommandText = """
            INSERT INTO workspace_file_chunks
                (ChunkId, WorkspacePath, RelativePath, ChunkIndex, Content, CharOffset, CharLength, FileLastWriteTime, IndexedAt)
            VALUES (@id, @path, 'notes.md', 0, 'identityneedle', 0, 14, @now, @now)
            """;

        command.Parameters.AddWithValue("@id", chunkId);

        command.Parameters.AddWithValue("@path", root);

        command.Parameters.AddWithValue("@now", "2026-10-25T12:00:00.0000000+00:00");

        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task<Guid> SeedWorkspaceSearchSessionAsync(ArcanumWebApplicationFactory factory, bool exhaustBytes = false)
    {
        using IServiceScope scope = factory.Services.CreateScope();

        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        Guid sessionId = Guid.NewGuid();

        db.Sessions.Add(new Session
        {
            Id = sessionId,
            Status = "active",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        db.Entries.Add(new Entry
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            Role = MessageRole.User,
            Content = exhaustBytes ? "identityneedle" + new string('x', 8 * 1024 * 1024) : "identityneedle",
            ModelUsed = "test",
            CreatedAt = DateTimeOffset.UtcNow,
            Sequence = 1,
        });

        await db.SaveChangesAsync();

        return sessionId;
    }
}
