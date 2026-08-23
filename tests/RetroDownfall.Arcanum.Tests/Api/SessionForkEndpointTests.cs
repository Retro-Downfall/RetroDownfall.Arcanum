using System.Net;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// <c>POST /api/sessions/{id}/fork</c> — DESIGN.md §11.16.1.
/// </summary>
[Collection("ApiHost")]
public sealed class SessionForkEndpointTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public SessionForkEndpointTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    [SkippableFact]
    public async Task PostFork_ValidSource_CopiesEntriesAndSetsLineage()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        SessionDetailDto source = await CreateSessionAsync(client, "Original session");

        await AppendEntryAsync(client, source.Id, MessageRole.User, "hello");

        await AppendEntryAsync(client, source.Id, MessageRole.Assistant, "hi there");

        HttpResponseMessage response = await client.PostAsync(
            $"/api/sessions/{source.Id:D}/fork",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        SessionDetailDto fork = await ReadSessionAsync(response);

        Assert.NotEqual(source.Id, fork.Id);

        Assert.Equal(source.Id, fork.ForkedFromSessionId);

        Assert.Equal(2, fork.EntryCount);

        Assert.Equal("Fork of Original session", fork.Title);

        Assert.Equal(0, fork.TotalTokensUsed);

        Assert.Equal("active", fork.Status);

        HttpResponseMessage entriesResponse = await client.GetAsync($"/api/sessions/{fork.Id:D}/entries");

        EntryDto[] entries = await ReadEntriesAsync(entriesResponse);

        Assert.Equal(2, entries.Length);

        Assert.Contains(entries, e => e.Content == "hello");

        Assert.Contains(entries, e => e.Content == "hi there");

        // Copied entries must have fresh ids, not reuse the source's.
        HttpResponseMessage sourceEntriesResponse = await client.GetAsync($"/api/sessions/{source.Id:D}/entries");

        EntryDto[] sourceEntries = await ReadEntriesAsync(sourceEntriesResponse);

        Assert.DoesNotContain(entries, e => sourceEntries.Any(se => se.Id == e.Id));

    }

    [SkippableFact]
    public async Task PostFork_WithCustomTitle_UsesProvidedTitle()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        SessionDetailDto source = await CreateSessionAsync(client, "Original");

        ForkSessionRequest request = new(Title: "My custom fork title");

        HttpResponseMessage response = await PostForkAsync(client, source.Id, request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        SessionDetailDto fork = await ReadSessionAsync(response);

        Assert.Equal("My custom fork title", fork.Title);

    }

    [SkippableFact]
    public async Task PostFork_WithUpToEntryId_CopiesOnlyEntriesUpToCutoffInclusive()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        SessionDetailDto source = await CreateSessionAsync(client, "Cutoff session");

        await AppendEntryAsync(client, source.Id, MessageRole.User, "first");

        EntryDto cutoff = await AppendEntryAsync(client, source.Id, MessageRole.Assistant, "second");

        await AppendEntryAsync(client, source.Id, MessageRole.User, "third");

        ForkSessionRequest request = new(UpToEntryId: cutoff.Id);

        HttpResponseMessage response = await PostForkAsync(client, source.Id, request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        SessionDetailDto fork = await ReadSessionAsync(response);

        Assert.Equal(2, fork.EntryCount);

        HttpResponseMessage entriesResponse = await client.GetAsync($"/api/sessions/{fork.Id:D}/entries");

        EntryDto[] entries = await ReadEntriesAsync(entriesResponse);

        Assert.Equal(2, entries.Length);

        Assert.Contains(entries, e => e.Content == "first");

        Assert.Contains(entries, e => e.Content == "second");

        Assert.DoesNotContain(entries, e => e.Content == "third");

    }

    [SkippableFact]
    public async Task PostFork_UpToEntryIdFromDifferentSession_Returns404EntryNotFound()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        SessionDetailDto source = await CreateSessionAsync(client, "Source");

        SessionDetailDto other = await CreateSessionAsync(client, "Other");

        EntryDto otherEntry = await AppendEntryAsync(client, other.Id, MessageRole.User, "not in source");

        ForkSessionRequest request = new(UpToEntryId: otherEntry.Id);

        HttpResponseMessage response = await PostForkAsync(client, source.Id, request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        ApiResponse<SessionDetailDto>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseSessionDetailDto);

        Assert.Equal("Session.EntryNotFound", body!.Error?.Code);

    }

    [SkippableFact]
    public async Task PostFork_SourceNotFound_Returns404()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await PostForkAsync(client, Guid.NewGuid(), new ForkSessionRequest());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        ApiResponse<SessionDetailDto>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseSessionDetailDto);

        Assert.Equal("Session.NotFound", body!.Error?.Code);

    }

    [SkippableFact]
    public async Task PostFork_FromArchivedSession_Succeeds()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        SessionDetailDto source = await CreateSessionAsync(client, "To be archived");

        await AppendEntryAsync(client, source.Id, MessageRole.User, "content before archive");

        HttpResponseMessage archiveResponse = await client.DeleteAsync($"/api/sessions/{source.Id:D}");

        Assert.Equal(HttpStatusCode.NoContent, archiveResponse.StatusCode);

        HttpResponseMessage response = await PostForkAsync(client, source.Id, new ForkSessionRequest());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        SessionDetailDto fork = await ReadSessionAsync(response);

        // The fork is always active, even though its source is archived.
        Assert.Equal("active", fork.Status);

        Assert.Equal(1, fork.EntryCount);

    }

    [SkippableFact]
    public async Task PostFork_CustomCampaignId_Overrides()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        SessionDetailDto source = await CreateSessionAsync(client, "No campaign");

        Guid overrideCampaignId = Guid.NewGuid();

        ForkSessionRequest request = new(CampaignId: overrideCampaignId);

        HttpResponseMessage response = await PostForkAsync(client, source.Id, request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        SessionDetailDto fork = await ReadSessionAsync(response);

        Assert.Equal(overrideCampaignId, fork.CampaignId);

    }

    [SkippableFact]
    public async Task PostFork_DeepAcyclicChain_RemainsUsable()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        SessionDetailDto root = await CreateSessionAsync(client, "Root");

        SessionDetailDto fork1 = await ReadSessionAsync(await PostForkAsync(client, root.Id, new ForkSessionRequest()));

        SessionDetailDto fork2 = await ReadSessionAsync(await PostForkAsync(client, fork1.Id, new ForkSessionRequest()));

        SessionDetailDto fork3 = await ReadSessionAsync(await PostForkAsync(client, fork2.Id, new ForkSessionRequest()));

        HttpResponseMessage fourthForkResponse = await PostForkAsync(client, fork3.Id, new ForkSessionRequest());

        Assert.Equal(HttpStatusCode.Created, fourthForkResponse.StatusCode);

        SessionDetailDto fork4 = await ReadSessionAsync(fourthForkResponse);

        Assert.Equal(fork3.Id, fork4.ForkedFromSessionId);

    }

    [SkippableFact]
    public async Task PostFork_EmptySourceSession_CreatesEmptyFork()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        SessionDetailDto source = await CreateSessionAsync(client, "Empty");

        HttpResponseMessage response = await PostForkAsync(client, source.Id, new ForkSessionRequest());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        SessionDetailDto fork = await ReadSessionAsync(response);

        Assert.Equal(0, fork.EntryCount);

    }

    [SkippableFact]
    public async Task PostFork_WithoutApiKey_Returns401()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient authenticated = _factory.CreateAuthenticatedClient();

        SessionDetailDto source = await CreateSessionAsync(authenticated, "Needs auth");

        HttpClient anonymous = _factory.CreateClient();

        HttpResponseMessage response = await anonymous.PostAsync(
            $"/api/sessions/{source.Id:D}/fork",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

    }

    private static async Task<SessionDetailDto> CreateSessionAsync(HttpClient client, string title)
    {

        CreateSessionRequest request = new(CampaignId: null, Title: title);

        string payload = JsonSerializer.Serialize(request, ArcanumJsonContext.Default.CreateSessionRequest);

        HttpResponseMessage response = await client.PostAsync(
            "/api/sessions",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return await ReadSessionAsync(response);

    }

    private static async Task<EntryDto> AppendEntryAsync(HttpClient client, Guid sessionId, MessageRole role, string content)
    {

        AppendEntryRequest request = new(role, content);

        string payload = JsonSerializer.Serialize(request, ArcanumJsonContext.Default.AppendEntryRequest);

        HttpResponseMessage response = await client.PostAsync(
            $"/api/sessions/{sessionId:D}/entries",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<EntryDto>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseEntryDto);

        Assert.NotNull(body?.Data);

        return body!.Data!;

    }

    private static Task<HttpResponseMessage> PostForkAsync(HttpClient client, Guid sourceId, ForkSessionRequest request)
    {

        string payload = JsonSerializer.Serialize(request, ArcanumJsonContext.Default.ForkSessionRequest);

        return client.PostAsync(
            $"/api/sessions/{sourceId:D}/fork",
            new StringContent(payload, Encoding.UTF8, "application/json"));

    }

    private static async Task<SessionDetailDto> ReadSessionAsync(HttpResponseMessage response)
    {

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<SessionDetailDto>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseSessionDetailDto);

        Assert.NotNull(body?.Data);

        return body!.Data!;

    }

    private static async Task<EntryDto[]> ReadEntriesAsync(HttpResponseMessage response)
    {

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<EntryDto[]>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseEntryDtoArray);

        Assert.NotNull(body?.Data);

        return body!.Data!;

    }

}
