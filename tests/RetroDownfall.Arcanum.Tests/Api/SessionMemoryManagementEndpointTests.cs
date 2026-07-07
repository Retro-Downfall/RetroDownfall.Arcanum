using System.Net;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class SessionMemoryManagementEndpointTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public SessionMemoryManagementEndpointTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    [SkippableFact]
    public async Task DeleteSessionEntry_returns_400_when_memory_management_disabled()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory disabledFactory = new()
        {
            SettingsOverride = settings => settings with
            {
                Sessions = (settings.Sessions ?? new SessionSettings()) with
                {
                    AllowMemoryManagement = false,
                },
            },
        };

        HttpClient client = disabledFactory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.DeleteAsync(
            $"/api/sessions/{Guid.NewGuid():D}/entries/{Guid.NewGuid():D}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<bool>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseBoolean);

        Assert.NotNull(body);

        Assert.False(body!.IsSuccess);

        Assert.Equal(ErrorCodes.Session.MemoryManagementDisabled, body.Error?.Code);

    }

    [SkippableFact]
    public async Task PinSessionEntry_returns_400_when_memory_management_disabled()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory disabledFactory = new()
        {
            SettingsOverride = settings => settings with
            {
                Sessions = (settings.Sessions ?? new SessionSettings()) with
                {
                    AllowMemoryManagement = false,
                },
            },
        };

        HttpClient client = disabledFactory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync(
            $"/api/sessions/{Guid.NewGuid():D}/entries/{Guid.NewGuid():D}/pin",
            null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<bool>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseBoolean);

        Assert.NotNull(body);

        Assert.False(body!.IsSuccess);

        Assert.Equal(ErrorCodes.Session.MemoryManagementDisabled, body.Error?.Code);

    }

    [SkippableFact]
    public async Task CompactSession_returns_400_when_memory_management_disabled()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory disabledFactory = new()
        {
            SettingsOverride = settings => settings with
            {
                Sessions = (settings.Sessions ?? new SessionSettings()) with
                {
                    AllowMemoryManagement = false,
                },
            },
        };

        HttpClient client = disabledFactory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync(
            $"/api/sessions/{Guid.NewGuid():D}/compact",
            null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<CompactResult>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseCompactResult);

        Assert.NotNull(body);

        Assert.False(body!.IsSuccess);

        Assert.Equal(ErrorCodes.Session.MemoryManagementDisabled, body.Error?.Code);

    }

    [SkippableFact]
    public async Task PinSessionEntry_and_UnpinSessionEntry_round_trip()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory enabledFactory = CreateEnabledFactory();

        HttpClient client = enabledFactory.CreateAuthenticatedClient();

        (Guid sessionId, Guid entryId) = await CreateSessionAndAppendEntryAsync(client);

        HttpResponseMessage pinResponse = await client.PostAsync(
            $"/api/sessions/{sessionId:D}/entries/{entryId:D}/pin",
            null);

        Assert.Equal(HttpStatusCode.OK, pinResponse.StatusCode);

        string pinJson = await pinResponse.Content.ReadAsStringAsync();

        ApiResponse<bool>? pinBody = JsonSerializer.Deserialize(pinJson, ArcanumJsonContext.Default.ApiResponseBoolean);

        Assert.NotNull(pinBody);

        Assert.True(pinBody!.IsSuccess);

        EntryDto[] entries = await GetSessionEntriesAsync(client, sessionId);

        Assert.Contains(entries, e => e.Id == entryId && e.IsPinned);

        HttpResponseMessage unpinResponse = await client.DeleteAsync(
            $"/api/sessions/{sessionId:D}/entries/{entryId:D}/pin");

        Assert.Equal(HttpStatusCode.OK, unpinResponse.StatusCode);

        EntryDto[] afterUnpin = await GetSessionEntriesAsync(client, sessionId);

        Assert.Contains(afterUnpin, e => e.Id == entryId && !e.IsPinned);

    }

    [SkippableFact]
    public async Task PinSessionEntry_returns_409_when_max_pinned_exceeded()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory limitedFactory = new()
        {
            SettingsOverride = settings => settings with
            {
                Sessions = (settings.Sessions ?? new SessionSettings()) with
                {
                    AllowMemoryManagement = true,
                    MaxPinnedEntries = 1,
                },
            },
        };

        HttpClient client = limitedFactory.CreateAuthenticatedClient();

        (Guid sessionId, Guid entryId1) = await CreateSessionAndAppendEntryAsync(client);
        Guid entryId2 = await AppendEntryAsync(client, sessionId);

        HttpResponseMessage firstPin = await client.PostAsync(
            $"/api/sessions/{sessionId:D}/entries/{entryId1:D}/pin",
            null);

        Assert.Equal(HttpStatusCode.OK, firstPin.StatusCode);

        HttpResponseMessage secondPin = await client.PostAsync(
            $"/api/sessions/{sessionId:D}/entries/{entryId2:D}/pin",
            null);

        Assert.Equal(HttpStatusCode.Conflict, secondPin.StatusCode);

        string json = await secondPin.Content.ReadAsStringAsync();

        ApiResponse<bool>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseBoolean);

        Assert.NotNull(body);

        Assert.False(body!.IsSuccess);

        Assert.Equal(ErrorCodes.Session.TooManyPinned, body.Error?.Code);

    }

    [SkippableFact]
    public async Task DeleteSessionEntry_removes_entry()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory enabledFactory = CreateEnabledFactory();

        HttpClient client = enabledFactory.CreateAuthenticatedClient();

        (Guid sessionId, Guid entryId) = await CreateSessionAndAppendEntryAsync(client);

        HttpResponseMessage deleteResponse = await client.DeleteAsync(
            $"/api/sessions/{sessionId:D}/entries/{entryId:D}");

        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        EntryDto[] entries = await GetSessionEntriesAsync(client, sessionId);

        Assert.DoesNotContain(entries, e => e.Id == entryId);

    }

    [SkippableFact]
    public async Task CompactSession_returns_success_for_empty_session()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory enabledFactory = CreateEnabledFactory();

        HttpClient client = enabledFactory.CreateAuthenticatedClient();

        Guid sessionId = await CreateSessionAsync(client);

        HttpResponseMessage response = await client.PostAsync(
            $"/api/sessions/{sessionId:D}/compact",
            null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<CompactResult>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseCompactResult);

        Assert.NotNull(body);

        Assert.True(body!.IsSuccess);

        Assert.NotNull(body.Data);

    }

    private static ArcanumWebApplicationFactory CreateEnabledFactory(int maxPinnedEntries = 5)
    {

        return new ArcanumWebApplicationFactory
        {
            SettingsOverride = settings => settings with
            {
                Sessions = (settings.Sessions ?? new SessionSettings()) with
                {
                    AllowMemoryManagement = true,
                    MaxPinnedEntries = maxPinnedEntries,
                },
            },
        };

    }

    private static async Task<Guid> CreateSessionAsync(HttpClient client)
    {

        CreateSessionRequest request = new(CampaignId: null, Title: "memory management test");

        string payload = JsonSerializer.Serialize(request, ArcanumJsonContext.Default.CreateSessionRequest);

        HttpResponseMessage response = await client.PostAsync(
            "/api/sessions",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<SessionDetailDto>? body = JsonSerializer.Deserialize(
            json,
            ArcanumJsonContext.Default.ApiResponseSessionDetailDto);

        Assert.NotNull(body);

        Assert.True(body!.IsSuccess);

        Assert.NotNull(body.Data);

        return body.Data.Id;

    }

    private static async Task<(Guid SessionId, Guid EntryId)> CreateSessionAndAppendEntryAsync(HttpClient client)
    {

        Guid sessionId = await CreateSessionAsync(client);

        Guid entryId = await AppendEntryAsync(client, sessionId);

        return (sessionId, entryId);

    }

    private static async Task<Guid> AppendEntryAsync(HttpClient client, Guid sessionId)
    {

        AppendEntryRequest request = new(
            Role: MessageRole.User,
            Content: "entry for memory management test",
            ModelUsed: "test-model");

        string payload = JsonSerializer.Serialize(request, ArcanumJsonContext.Default.AppendEntryRequest);

        HttpResponseMessage response = await client.PostAsync(
            $"/api/sessions/{sessionId:D}/entries",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<EntryDto>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseEntryDto);

        Assert.NotNull(body);

        Assert.True(body!.IsSuccess);

        Assert.NotNull(body.Data);

        return body.Data.Id;

    }

    private static async Task<EntryDto[]> GetSessionEntriesAsync(HttpClient client, Guid sessionId)
    {

        HttpResponseMessage response = await client.GetAsync($"/api/sessions/{sessionId:D}/entries");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<EntryDto[]>? body = JsonSerializer.Deserialize(
            json,
            ArcanumJsonContext.Default.ApiResponseEntryDtoArray);

        Assert.NotNull(body);

        Assert.True(body!.IsSuccess);

        Assert.NotNull(body.Data);

        return body.Data;

    }

}
