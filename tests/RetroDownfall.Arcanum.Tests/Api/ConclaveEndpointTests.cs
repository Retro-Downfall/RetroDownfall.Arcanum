using System.Net;
using System.Text;
using System.Text.Json;

using RetroDownfall.Arcanum.Api.Models;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// The operator-facing Conclave routes added by issue #12 so a Mage can inspect and drive A2A without
/// hand-rolling JSON-RPC.
/// </summary>
[Collection("ApiHost")]
public sealed class ConclaveEndpointTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public ConclaveEndpointTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    [SkippableFact]
    public async Task GetStatus_WithoutApiKey_Returns401()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/conclave/status");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

    }

    [SkippableFact]
    public async Task GetStatus_ReportsDisabledByDefaultWithAnActionableDetail()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync("/api/conclave/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResponse<ConclaveStatusDto>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseConclaveStatusDto);

        Assert.NotNull(body?.Data);

        Assert.Equal("disabled", body.Data.State);

        Assert.False(body.Data.ConclaveEnabled);

        Assert.Null(body.Data.ServerPath);

        Assert.Equal(0, body.Data.AllowedRemoteAgentCount);

        // "It's off" is only useful if it also says how to turn it on.
        Assert.Contains("Arcanum:Features:", body.Data.Detail, StringComparison.Ordinal);

    }

    [SkippableFact]
    public async Task PostSending_WhenA2AClientIsDisabled_Returns403WithTheOwningReason()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await PostSendingAsync(
            client,
            """{"agentUrl":"https://remote.example.test/","goal":"do the thing"}""");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        ApiResponse<SendingDispatchDto>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseSendingDispatchDto);

        Assert.False(body!.IsSuccess);

        Assert.Equal(ErrorCodes.Sending.Disabled, body.Error?.Code);

    }

    [SkippableTheory]
    [InlineData("""{"goal":"do the thing"}""")]
    [InlineData("""{"agentUrl":"https://remote.example.test/"}""")]
    [InlineData("""{"agentUrl":"   ","goal":"   "}""")]
    public async Task PostSending_MissingRequiredFields_Returns400(string payload)
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await PostSendingAsync(client, payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        ApiResponse<SendingDispatchDto>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseSendingDispatchDto);

        Assert.Equal(ErrorCodes.Validation.InvalidBody, body!.Error?.Code);

    }

    [SkippableFact]
    public async Task PostSending_MalformedJson_Returns400RatherThanThrowing()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await PostSendingAsync(client, "{not json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

    }

    [SkippableFact]
    public async Task PostSending_WithoutApiKey_Returns401()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await PostSendingAsync(
            client,
            """{"agentUrl":"https://remote.example.test/","goal":"do the thing"}""");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

    }

    private static Task<HttpResponseMessage> PostSendingAsync(HttpClient client, string payload) =>
        client.PostAsync(
            "/api/conclave/sendings",
            new StringContent(payload, Encoding.UTF8, "application/json"));

}
