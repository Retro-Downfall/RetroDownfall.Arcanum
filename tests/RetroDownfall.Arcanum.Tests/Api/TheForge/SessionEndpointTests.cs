using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Primitives;
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
