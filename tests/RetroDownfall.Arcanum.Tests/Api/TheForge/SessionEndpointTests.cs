using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api.TheForge;

[Collection("ApiHost")]
public sealed class SessionEndpointTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public SessionEndpointTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    [SkippableFact]
    public async Task GetEntries_CountOnly_ReturnsEntryCount()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionWithEntriesAsync(3);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync($"/api/sessions/{sessionId}/entries?countOnly=true");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResponse<SessionEntryCountDto>? body = await response.Content
            .ReadFromJsonAsync<ApiResponse<SessionEntryCountDto>>(ArcanumJsonContext.Default.ApiResponseSessionEntryCountDto);

        Assert.NotNull(body);
        Assert.NotNull(body.Data);
        Assert.True(body.IsSuccess);
        Assert.Equal(3, body.Data.Count);

    }

    [SkippableFact]
    public async Task GetEntries_CountOnly_UnknownSession_Returns404()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid unknownId = Guid.NewGuid();
        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync($"/api/sessions/{unknownId}/entries?countOnly=true");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

    }

    [SkippableFact]
    public async Task GetEntries_WithoutCountOnly_ReturnsEntries()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionWithEntriesAsync(2);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync($"/api/sessions/{sessionId}/entries");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResponse<EntryDto[]>? body = await response.Content
            .ReadFromJsonAsync<ApiResponse<EntryDto[]>>(ArcanumJsonContext.Default.ApiResponseEntryDtoArray);

        Assert.NotNull(body);
        Assert.NotNull(body.Data);
        Assert.True(body.IsSuccess);
        Assert.Equal(2, body.Data.Length);

    }

    [SkippableFact]
    public async Task GetAttachments_UnknownSession_Returns404()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid unknownId = Guid.NewGuid();

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync($"/api/sessions/{unknownId}/attachments");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        ApiResponse<SessionAttachmentDto[]>? body = await response.Content
            .ReadFromJsonAsync<ApiResponse<SessionAttachmentDto[]>>(
                ArcanumJsonContext.Default.ApiResponseSessionAttachmentDtoArray);

        Assert.NotNull(body);
        Assert.False(body.IsSuccess);
        Assert.Equal(ErrorCodes.Session.NotFound, body.Error?.Code);

    }

    [SkippableFact]
    public async Task GetAttachments_ReturnsBoundRowsOnly()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionWithEntriesAsync(0);

        SessionAttachmentRecord bound;

        using (IServiceScope scope = _factory.Services.CreateScope())
        {

            ISessionAttachmentStore store = scope.ServiceProvider.GetRequiredService<ISessionAttachmentStore>();

            bound = await store.PersistNewAsync(
                sessionId,
                pendingTurnId: null,
                entryId: null,
                logicalNameHint: "notes.txt",
                originalFileName: "notes.txt",
                Encoding.UTF8.GetBytes("bound-bytes"),
                mimeType: "text/plain",
                SessionAttachmentKind.Text);

            _ = await store.PersistNewAsync(
                sessionId: null,
                pendingTurnId: "turn-" + Guid.NewGuid().ToString("N"),
                entryId: null,
                logicalNameHint: "pending.txt",
                originalFileName: "pending.txt",
                Encoding.UTF8.GetBytes("pending-bytes"),
                mimeType: "text/plain",
                SessionAttachmentKind.Text);

        }

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync($"/api/sessions/{sessionId}/attachments");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResponse<SessionAttachmentDto[]>? body = await response.Content
            .ReadFromJsonAsync<ApiResponse<SessionAttachmentDto[]>>(
                ArcanumJsonContext.Default.ApiResponseSessionAttachmentDtoArray);

        Assert.NotNull(body);
        Assert.True(body.IsSuccess);
        Assert.NotNull(body.Data);
        Assert.Single(body.Data);

        SessionAttachmentDto dto = body.Data[0];

        Assert.Equal(bound.Id, dto.Id);
        Assert.Equal(bound.LogicalKey, dto.LogicalKey);
        Assert.Equal(bound.OriginalFileName, dto.OriginalFileName);
        Assert.Equal(bound.Version, dto.Version);
        Assert.Equal(bound.RelativePath, dto.RelativePath);
        Assert.Equal(bound.MimeType, dto.MimeType);
        Assert.Equal(bound.ByteLength, dto.ByteLength);
        Assert.Equal(SessionAttachmentKind.Text, dto.Kind);
        Assert.Equal(bound.ContentSha256, dto.ContentSha256);

    }

    [SkippableFact]
    public async Task GetAttachments_EmptySession_ReturnsEmptyArray()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionWithEntriesAsync(0);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync($"/api/sessions/{sessionId}/attachments");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResponse<SessionAttachmentDto[]>? body = await response.Content
            .ReadFromJsonAsync<ApiResponse<SessionAttachmentDto[]>>(
                ArcanumJsonContext.Default.ApiResponseSessionAttachmentDtoArray);

        Assert.NotNull(body);
        Assert.True(body.IsSuccess);
        Assert.NotNull(body.Data);
        Assert.Empty(body.Data);

    }

    private async Task<Guid> CreateSessionWithEntriesAsync(int count)
    {

        using IServiceScope scope = _factory.Services.CreateScope();

        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        Session session = new() { Title = "count-test" };

        _ = db.Sessions.Add(session);

        for (int i = 0; i < count; i++)
        {

            Entry entry = new()
            {
                SessionId = session.Id,
                Role = MessageRole.User,
                Content = $"entry {i}",
                CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-count + i),
            };

            _ = db.Entries.Add(entry);

        }

        await db.SaveChangesAsync();

        return session.Id;

    }

}
