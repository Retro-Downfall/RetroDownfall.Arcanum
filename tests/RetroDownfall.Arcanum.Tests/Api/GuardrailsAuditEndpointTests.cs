using System.Net;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Fixtures;

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

}
