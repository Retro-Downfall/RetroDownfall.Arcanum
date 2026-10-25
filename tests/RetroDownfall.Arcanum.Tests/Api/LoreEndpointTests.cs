using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Tests.Support;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class LoreEndpointTests
{
    private readonly ArcanumWebApplicationFactory _factory;

    public LoreEndpointTests(ArcanumWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [SkippableFact]
    public async Task GetLore_WithValidApiKey_ReturnsPagedEnvelope()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync("/api/lore");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<ListPageResult<LoreDto>>? body = JsonSerializer.Deserialize(
            json,
            ArcanumJsonContext.Default.ApiResponseListPageResultLoreDto);

        Assert.NotNull(body);

        Assert.True(body.IsSuccess);

        Assert.NotNull(body.Data);

        Assert.NotNull(body.Data.Items);
    }

    [SkippableFact]
    public async Task PostLore_WithValidBody_ReturnsSavedLoreEnvelope()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        string key = $"integration-{Guid.NewGuid():N}";

        UpsertLoreRequest request = new(key, "integration test value");

        string payload = JsonSerializer.Serialize(request, ArcanumJsonContext.Default.UpsertLoreRequest);

        HttpResponseMessage response = await client.PostAsync(
            "/api/lore",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<LoreDto>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseLoreDto);

        Assert.NotNull(body);

        Assert.True(body.IsSuccess);

        Assert.NotNull(body.Data);

        Assert.Equal(key, body.Data.Key);

        Assert.Equal("integration test value", body.Data.Value);
    }

    [SkippableFact]
    public async Task PostLore_MissingKeyAndValue_Returns400Envelope()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync(
            "/api/lore",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<LoreDto>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseLoreDto);

        Assert.NotNull(body);

        Assert.False(body.IsSuccess);

        Assert.Equal("Validation.InvalidLore", body.Error?.Code);
    }

    [SkippableTheory]
    [InlineData("team/standards")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("team\\standards")]
    [InlineData("percent%2Fkey")]
    [InlineData("hash#key")]
    [InlineData("question?key")]
    [InlineData("plus+ space")]
    [InlineData("規則/é")]
    [InlineData("entries")]
    public async Task Client_can_read_and_remove_every_accepted_key(string key)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using ArcanumApiCredentialLease lease = ArcanumApiCredentialLeaseTestFactory.Create(ArcanumWebApplicationFactory.TestApiKey);

        ArcanumApiClient client = new(new HostClientFactory(_factory), lease);

        Result<LoreDto> saved = await client.UpsertLoreAsync(key, "use tabs");

        Assert.True(saved.IsSuccess, saved.IsFailure ? saved.Error.Code : null);

        Result<List<LoreDto>> listed = await client.ListLoreAsync();

        Assert.True(listed.IsSuccess);

        Assert.Contains(listed.Value, entry => entry.Key == key);

        Result<LoreDto> fetched = await client.GetLoreAsync(key);

        Assert.True(fetched.IsSuccess, fetched.IsFailure ? fetched.Error.Code : null);

        Assert.Equal(key, fetched.Value.Key);

        Assert.Equal("use tabs", fetched.Value.Value);

        Result<bool> deleted = await client.DeleteLoreAsync(key);

        Assert.True(deleted.IsSuccess, deleted.IsFailure ? deleted.Error.Code : null);

        Assert.True(deleted.Value);

        Result<LoreDto> missing = await client.GetLoreAsync(key);

        Assert.Equal(ErrorCodes.Grimoire.LoreNotFound, missing.Error.Code);

        using CancellationTokenSource cancellation = new();

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetLoreAsync(key, cancellation.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.DeleteLoreAsync(key, cancellation.Token));
    }

    [SkippableTheory]
    [InlineData("team/legacy")]
    [InlineData(".")]
    [InlineData("..")]
    public async Task Query_routes_read_and_remove_preexisting_keys(string key)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using IServiceScope scope = _factory.Services.CreateScope();

        IGrimoireRepository repository = scope.ServiceProvider.GetRequiredService<IGrimoireRepository>();

        await repository.ScribeLoreAsync(key, "legacy value");

        using HttpClient client = _factory.CreateAuthenticatedClient();

        string address = "/api/lore/entries/by-key?key=" + Uri.EscapeDataString(" " + key + " ");

        using HttpResponseMessage fetched = await client.GetAsync(address);

        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);

        ApiResponse<LoreDto>? body = JsonSerializer.Deserialize(await fetched.Content.ReadAsStringAsync(), ArcanumJsonContext.Default.ApiResponseLoreDto);

        Assert.Equal(key, body?.Data?.Key);

        using HttpResponseMessage deleted = await client.DeleteAsync(address);

        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

        ApiResponse<bool>? deletion = JsonSerializer.Deserialize(await deleted.Content.ReadAsStringAsync(), ArcanumJsonContext.Default.ApiResponseBoolean);

        Assert.True(deletion?.Data);

        Assert.Null(await repository.GetLoreAsync(key));
    }

    [SkippableTheory]
    [InlineData(false, null, HttpStatusCode.BadRequest, "Validation.InvalidKey")]
    [InlineData(true, null, HttpStatusCode.BadRequest, "Validation.InvalidKey")]
    [InlineData(false, "", HttpStatusCode.BadRequest, "Validation.InvalidKey")]
    [InlineData(true, "   ", HttpStatusCode.BadRequest, "Validation.InvalidKey")]
    [InlineData(false, "missing-key-for-addressing-test", HttpStatusCode.NotFound, "Grimoire.LoreNotFound")]
    [InlineData(true, "missing-key-for-addressing-test", HttpStatusCode.NotFound, "Grimoire.LoreNotFound")]
    public async Task Query_routes_return_typed_errors(bool delete, string? key, HttpStatusCode status, string code)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using HttpClient client = _factory.CreateAuthenticatedClient();

        using HttpRequestMessage request = new(delete ? HttpMethod.Delete : HttpMethod.Get, key is null ? "/api/lore/entries/by-key" : "/api/lore/entries/by-key?key=" + Uri.EscapeDataString(key));

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(status, response.StatusCode);

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.False(body.RootElement.GetProperty("isSuccess").GetBoolean());

        Assert.Equal(code, body.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [SkippableTheory]
    [InlineData("entries")]
    [InlineData("ordinary-key")]
    public async Task Legacy_routes_still_read_and_remove_single_segment_keys(string key)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using IServiceScope scope = _factory.Services.CreateScope();

        await scope.ServiceProvider.GetRequiredService<IGrimoireRepository>().ScribeLoreAsync(key, "legacy route");

        using HttpClient client = _factory.CreateAuthenticatedClient();

        using HttpResponseMessage fetched = await client.GetAsync("/api/lore/" + key);

        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);

        using HttpResponseMessage deleted = await client.DeleteAsync("/api/lore/" + key);

        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Query_routes_reject_keys_over_the_write_limit(bool delete)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using HttpClient client = _factory.CreateAuthenticatedClient();

        using HttpRequestMessage request = new(delete ? HttpMethod.Delete : HttpMethod.Get, "/api/lore/entries/by-key?key=" + new string('k', 257));

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("Validation.InvalidKey", body.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [SkippableTheory]
    [InlineData("team/standards")]
    [InlineData(".")]
    [InlineData("..")]
    public async Task Client_can_remove_written_special_keys(string key)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using ArcanumApiCredentialLease lease = ArcanumApiCredentialLeaseTestFactory.Create(ArcanumWebApplicationFactory.TestApiKey);

        ArcanumApiClient client = new(new HostClientFactory(_factory), lease);

        Result<LoreDto> saved = await client.UpsertLoreAsync(key, "use tabs");

        Assert.True(saved.IsSuccess);

        Result<bool> deleted = await client.DeleteLoreAsync(key);

        Assert.True(deleted.IsSuccess, deleted.IsFailure ? deleted.Error.Code : null);

        Assert.True(deleted.Value);
    }

    private sealed class HostClientFactory(ArcanumWebApplicationFactory factory) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new HostAuthenticationHandler(factory.Server.CreateHandler()))
        {
            BaseAddress = new Uri("http://localhost:5001/"),
        };
    }

    // The credential test helper issues its own process capability. Adapt only authentication to
    // the host fixture's API key; request URI, body, cancellation and response pass through unchanged.
    private sealed class HostAuthenticationHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Remove(ArcanumApiHeaders.ProcessCapability);

            request.Headers.Add(ArcanumApiHeaders.ApiKey, ArcanumWebApplicationFactory.TestApiKey);

            return base.SendAsync(request, cancellationToken);
        }
    }
}
