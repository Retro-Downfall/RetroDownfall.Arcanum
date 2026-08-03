using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class GuardrailsAuditEndpointTests
{

    [SkippableFact]
    public async Task GetGuardrailsAudit_WhenDisabled_Returns200EmptyList()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using ArcanumWebApplicationFactory factory = new();

        HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync("/api/guardrails/audit");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<GuardrailAuditRecord[]>? body = JsonSerializer.Deserialize(
            json,
            ArcanumJsonContext.Default.ApiResponseGuardrailAuditRecordArray);

        Assert.NotNull(body);

        Assert.True(body!.IsSuccess);

        Assert.Empty(body.Data ?? []);

    }

    [SkippableFact]
    public async Task GetGuardrailsAudit_InvalidFrom_Returns400()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using ArcanumWebApplicationFactory factory = new();

        HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync("/api/guardrails/audit?from=not-a-date");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

    }

    [SkippableFact]
    public async Task GetGuardrailsAudit_Returns_and_accepts_opaque_cursor_without_changing_array_body()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        FakeGuardrailAuditLogger logger = new();

        logger.Records.Add(MakeRecord("oldest"));

        logger.Records.Add(MakeRecord("middle"));

        logger.Records.Add(MakeRecord("newest"));

        using ArcanumWebApplicationFactory factory = new()
        {

            ServiceOverrides = services =>
            {

                services.RemoveAll<IGuardrailAuditLogger>();

                services.AddSingleton<IGuardrailAuditLogger>(logger);

            },

        };

        HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage firstResponse = await client.GetAsync("/api/guardrails/audit?limit=2");

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

        string cursor = Assert.Single(firstResponse.Headers.GetValues("X-Arcanum-Next-Cursor"));

        string firstJson = await firstResponse.Content.ReadAsStringAsync();

        ApiResponse<GuardrailAuditRecord[]>? first = JsonSerializer.Deserialize(
            firstJson,
            ArcanumJsonContext.Default.ApiResponseGuardrailAuditRecordArray);

        Assert.Equal(["newest", "middle"], first!.Data!.Select(static record => record.ViolationType));

        HttpResponseMessage secondResponse = await client.GetAsync(
            $"/api/guardrails/audit?limit=2&cursor={Uri.EscapeDataString(cursor)}");

        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);

        string secondJson = await secondResponse.Content.ReadAsStringAsync();

        ApiResponse<GuardrailAuditRecord[]>? second = JsonSerializer.Deserialize(
            secondJson,
            ArcanumJsonContext.Default.ApiResponseGuardrailAuditRecordArray);

        GuardrailAuditRecord record = Assert.Single(second!.Data!);

        Assert.Equal("oldest", record.ViolationType);

        Assert.False(secondResponse.Headers.Contains("X-Arcanum-Next-Cursor"));

    }

    private static GuardrailAuditRecord MakeRecord(string violationType) =>
        new(
            Timestamp: DateTimeOffset.UtcNow.ToString("O"),
            SessionId: "session",
            Stage: "Input",
            ViolationType: violationType,
            MatchedTextRedacted: "***",
            Model: "model");

}
