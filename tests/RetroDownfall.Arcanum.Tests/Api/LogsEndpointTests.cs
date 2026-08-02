using System.Net;

using System.Runtime.CompilerServices;

using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Core.Logging;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Logging;

using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class LogsEndpointTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public LogsEndpointTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    [SkippableFact]
    public async Task GetLogs_WithValidApiKey_ReturnsLogQueryEnvelope()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync("/api/logs");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<LogQueryResult>? body = JsonSerializer.Deserialize(
            json,
            ArcanumJsonContext.Default.ApiResponseLogQueryResult);

        Assert.NotNull(body);

        Assert.True(body.IsSuccess);

        Assert.NotNull(body.Data);

        Assert.NotNull(body.Data.Entries);

    }

    [SkippableFact]
    public async Task GetLogs_WithLimit_ReturnsOkEnvelope()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync("/api/logs?limit=10");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<LogQueryResult>? body = JsonSerializer.Deserialize(
            json,
            ArcanumJsonContext.Default.ApiResponseLogQueryResult);

        Assert.NotNull(body);

        Assert.True(body.IsSuccess);

        Assert.NotNull(body.Data);

    }

    [SkippableFact]
    public async Task StreamLogs_PassesFiltersAndUsesCommentForConnectionSentinel()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        CapturingLogQueryService query = new();

        await using ArcanumWebApplicationFactory factory = new()
        {

            ServiceOverrides = services =>
            {

                services.RemoveAll<ILogQueryService>();

                services.AddSingleton<ILogQueryService>(query);

            },

        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync(
            "/api/events/logs?level=warning&category=Api&search=needle");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        LogQueryRequest request = Assert.IsType<LogQueryRequest>(query.StreamRequest);

        Assert.Equal(LogLevel.Warning, request.MinLevel);

        Assert.Equal("Api", request.Category);

        Assert.Equal("needle", request.Search);

        string body = await response.Content.ReadAsStringAsync();

        Assert.StartsWith(": connected\n\n", body, StringComparison.Ordinal);

        Assert.DoesNotContain("data: {\"connected\":true}", body, StringComparison.Ordinal);

    }

    [SkippableTheory]
    [InlineData("verbose")]
    [InlineData("999")]
    public async Task StreamLogs_WithUnknownLevel_ReturnsValidationEnvelope(string level)
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        CapturingLogQueryService query = new();

        await using ArcanumWebApplicationFactory factory = new()
        {

            ServiceOverrides = services =>
            {

                services.RemoveAll<ILogQueryService>();

                services.AddSingleton<ILogQueryService>(query);

            },

        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync($"/api/events/logs?level={level}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<LogQueryResult>? body = JsonSerializer.Deserialize(
            json,
            ArcanumJsonContext.Default.ApiResponseLogQueryResult);

        Assert.NotNull(body);

        Assert.False(body.IsSuccess);

        Assert.Equal(ErrorCodes.Validation.InvalidQuery, body.Error?.Code);

        Assert.Null(query.StreamRequest);

    }

    private sealed class CapturingLogQueryService : ILogQueryService
    {

        public LogQueryRequest? StreamRequest { get; private set; }

        public Task<LogQueryResult> QueryAsync(LogQueryRequest request, CancellationToken ct) =>
            Task.FromResult(new LogQueryResult([], null, false));

        public async IAsyncEnumerable<LogEntry> StreamAsync(
            LogQueryRequest? request,
            [EnumeratorCancellation] CancellationToken ct)
        {

            StreamRequest = request;

            await Task.CompletedTask;

            yield return new LogEntry(
                1,
                DateTimeOffset.Parse("2026-08-01T12:00:00Z"),
                LogLevel.Warning,
                "Api",
                "needle",
                null,
                null,
                null,
                []);

        }

    }

}
