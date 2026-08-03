using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// <c>GET /api/audit</c> — query-parameter parsing and response shaping over
/// <see cref="IInferenceAuditLogger.QueryAsync"/>. Substitutes <see cref="FakeInferenceAuditLogger"/>
/// (rather than exercising real file I/O — already covered by <c>InferenceAuditLoggerTests</c>) so
/// these tests focus purely on the endpoint's own parsing/validation/response-envelope logic.
/// </summary>
/// <remarks>
/// Tagged <c>[Collection("ApiHost")]</c> like every other test class constructing its own isolated
/// <see cref="ArcanumWebApplicationFactory"/> — see the remarks on
/// <c>OpenAiV1EmbeddingsEndpointTests</c> for why this is required to avoid a process-wide
/// environment-variable race between concurrently-starting isolated factories.
/// </remarks>
[Collection("ApiHost")]
public sealed class AuditEndpointTests : IAsyncLifetime
{

    private ArcanumWebApplicationFactory _factory = null!;

    private FakeInferenceAuditLogger _auditLogger = null!;

    public Task InitializeAsync()
    {

        _auditLogger = new FakeInferenceAuditLogger();

        _factory = new ArcanumWebApplicationFactory
        {
            ServiceOverrides = services =>
            {

                services.RemoveAll<IInferenceAuditLogger>();

                services.AddSingleton<IInferenceAuditLogger>(_auditLogger);

            },
        };

        return Task.CompletedTask;

    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [SkippableFact]
    public async Task GetAudit_ReturnsRecordsNewestFirst()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _auditLogger.Records.Add(MakeRecord("ping", model: "model-a"));

        _auditLogger.Records.Add(MakeRecord("v1-completion", model: "model-b"));

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync("/api/audit");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<InferenceAuditRecord[]>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseInferenceAuditRecordArray);

        Assert.NotNull(body);

        Assert.True(body.IsSuccess);

        Assert.NotNull(body.Data);

        Assert.Equal(2, body.Data!.Length);

        Assert.Equal("model-b", body.Data[0].Model);

        Assert.Equal("model-a", body.Data[1].Model);

    }

    [SkippableFact]
    public async Task GetAudit_FiltersByModel()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _auditLogger.Records.Add(MakeRecord("ping", model: "model-a"));

        _auditLogger.Records.Add(MakeRecord("ping", model: "model-b"));

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync("/api/audit?model=model-a");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<InferenceAuditRecord[]>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseInferenceAuditRecordArray);

        Assert.NotNull(body?.Data);

        InferenceAuditRecord record = Assert.Single(body!.Data!);

        Assert.Equal("model-a", record.Model);

    }

    [SkippableFact]
    public async Task GetAudit_FiltersBySessionId()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _auditLogger.Records.Add(MakeRecord("ping", sessionId: "session-1"));

        _auditLogger.Records.Add(MakeRecord("ping", sessionId: "session-2"));

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync("/api/audit?sessionId=session-2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<InferenceAuditRecord[]>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseInferenceAuditRecordArray);

        Assert.NotNull(body?.Data);

        InferenceAuditRecord record = Assert.Single(body!.Data!);

        Assert.Equal("session-2", record.SessionId);

    }

    [SkippableFact]
    public async Task GetAudit_RespectsLimit()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        for (int i = 0; i < 5; i++)
        {

            _auditLogger.Records.Add(MakeRecord("ping"));

        }

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync("/api/audit?limit=2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<InferenceAuditRecord[]>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseInferenceAuditRecordArray);

        Assert.NotNull(body?.Data);

        Assert.Equal(2, body!.Data!.Length);

    }

    [SkippableFact]
    public async Task GetAudit_Returns_and_accepts_opaque_cursor_without_changing_array_body()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _auditLogger.Records.Add(MakeRecord("ping", sessionId: "oldest"));

        _auditLogger.Records.Add(MakeRecord("ping", sessionId: "middle"));

        _auditLogger.Records.Add(MakeRecord("ping", sessionId: "newest"));

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage firstResponse = await client.GetAsync("/api/audit?limit=2");

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

        string cursor = Assert.Single(firstResponse.Headers.GetValues("X-Arcanum-Next-Cursor"));

        string firstJson = await firstResponse.Content.ReadAsStringAsync();

        ApiResponse<InferenceAuditRecord[]>? first = JsonSerializer.Deserialize(
            firstJson,
            ArcanumJsonContext.Default.ApiResponseInferenceAuditRecordArray);

        Assert.Equal(["newest", "middle"], first!.Data!.Select(static record => record.SessionId));

        HttpResponseMessage secondResponse = await client.GetAsync(
            $"/api/audit?limit=2&cursor={Uri.EscapeDataString(cursor)}");

        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);

        string secondJson = await secondResponse.Content.ReadAsStringAsync();

        ApiResponse<InferenceAuditRecord[]>? second = JsonSerializer.Deserialize(
            secondJson,
            ArcanumJsonContext.Default.ApiResponseInferenceAuditRecordArray);

        InferenceAuditRecord record = Assert.Single(second!.Data!);

        Assert.Equal("oldest", record.SessionId);

        Assert.False(secondResponse.Headers.Contains("X-Arcanum-Next-Cursor"));

    }

    [SkippableFact]
    public async Task GetAudit_InvalidFromDate_Returns400()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync("/api/audit?from=not-a-date");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<InferenceAuditRecord[]>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseInferenceAuditRecordArray);

        Assert.NotNull(body);

        Assert.False(body.IsSuccess);

        Assert.Equal("Validation.InvalidBody", body.Error?.Code);

    }

    [SkippableFact]
    public async Task GetAudit_FromAfterTo_Returns400()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync("/api/audit?from=2026-01-02T00:00:00Z&to=2026-01-01T00:00:00Z");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

    }

    [SkippableFact]
    public async Task GetAudit_NoRecords_ReturnsEmptyArray()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync("/api/audit");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<InferenceAuditRecord[]>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseInferenceAuditRecordArray);

        Assert.NotNull(body?.Data);

        Assert.Empty(body!.Data!);

    }

    [SkippableFact]
    public async Task GetAudit_WithoutApiKey_Returns401()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/audit");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

    }

    private static InferenceAuditRecord MakeRecord(
        string requestType,
        string? model = "test-model",
        string? sessionId = null) =>
        new(
            Timestamp: DateTimeOffset.UtcNow.ToString("O"),
            SessionId: sessionId,
            RequestType: requestType,
            Model: model,
            Provider: "test-provider",
            PromptTokens: 10,
            CompletionTokens: 5,
            TotalTokens: 15,
            LatencyMs: 42,
            ToolCalls: 0,
            ToolNames: [],
            ToolArgumentsJson: null,
            FinishReason: "stop",
            ClientIp: "127.0.0.1",
            SpellName: null,
            CampaignId: null);

}
