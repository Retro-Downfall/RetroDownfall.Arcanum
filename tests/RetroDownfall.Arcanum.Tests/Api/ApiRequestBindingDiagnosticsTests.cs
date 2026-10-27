using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class ApiRequestBindingDiagnosticsTests(ArcanumWebApplicationFactory factory)
{
    [SkippableTheory]
    [InlineData("GET", "/api/saga?limit=PRIVATE-invalid")]
    [InlineData("GET", "/api/lore?limit=PRIVATE-invalid")]
    [InlineData("GET", "/api/campaigns?type=PRIVATE-invalid")]
    [InlineData("GET", "/api/sessions?from=PRIVATE-invalid")]
    [InlineData("GET", "/api/sessions?campaignId=PRIVATE-invalid")]
    [InlineData("DELETE", "/api/saga?confirm=PRIVATE-invalid")]
    public async Task Optional_query_conversion_refusal_has_an_actionable_envelope_without_values(string method, string route)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpRequestMessage request = new(new HttpMethod(method), route);

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        string body = await response.Content.ReadAsStringAsync();

        Assert.False(string.IsNullOrWhiteSpace(body), "A generated optional-parameter refusal must carry the documented envelope.");

        using JsonDocument json = JsonDocument.Parse(body);

        Assert.Equal(ErrorCodes.Validation.InvalidBody, json.RootElement.GetProperty("error").GetProperty("code").GetString());

        Assert.Contains("parameters", json.RootElement.GetProperty("error").GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("PRIVATE", body, StringComparison.Ordinal);
    }

    [SkippableTheory]
    [InlineData("/api/memory/search", "{\"query\":\"x\",\"scope\":\"PRIVATE-invalid\"}", "/scope")]
    [InlineData("/api/memory/search", "{\"query\":\"x\",\"limit\":\"PRIVATE-invalid\"}", "/limit")]
    [InlineData("/api/lore", "{\"key\":\"PRIVATE-key\",\"value\":123}", "/value")]
    public async Task Typed_JSON_refusal_identifies_a_declared_field_without_blame_for_syntax_or_private_values(string route, string body, string pointer)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using HttpClient client = factory.CreateAuthenticatedClient();

        using StringContent content = new(body, Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await client.PostAsync(route, content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        string text = await response.Content.ReadAsStringAsync();

        using JsonDocument json = JsonDocument.Parse(text);

        JsonElement error = json.RootElement.GetProperty("error");

        Assert.Equal(ErrorCodes.Validation.InvalidBody, error.GetProperty("code").GetString());

        Assert.DoesNotContain("parsed as valid JSON", error.GetProperty("message").GetString(), StringComparison.Ordinal);

        JsonElement detail = Assert.Single(error.GetProperty("details").EnumerateArray());

        Assert.Equal(pointer, detail.GetProperty("pointer").GetString());

        Assert.DoesNotContain("PRIVATE", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("$.scope", "/scope")]
    [InlineData("$.query.PRIVATE-key", "/query")]
    [InlineData("$.query['PRIVATE-key']", "/query")]
    [InlineData("$.PRIVATE-key", null)]
    [InlineData("$['PRIVATE-key']", null)]
    [InlineData("$", null)]
    [InlineData("[PRIVATE-key]", null)]
    public void JSON_diagnostics_name_only_a_declared_containing_field(string path, string? pointer)
    {
        Error error = ApiRequestJson.JsonBodyFault(ArcanumJsonContext.Default.MemorySearchRequest,
            new JsonException("PRIVATE-message", path, 0, 0));

        Assert.Equal(ErrorCodes.Validation.InvalidBody, error.Code);

        Assert.DoesNotContain("PRIVATE", error.Message, StringComparison.Ordinal);

        if (pointer is null)
        {
            Assert.Null(error.Details);
        }
        else
        {
            ConfigurationValidationError detail = Assert.Single(error.Details!);

            Assert.Equal(pointer, detail.Pointer);

            Assert.DoesNotContain("PRIVATE", detail.Detail, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Oversized_JSON_path_and_missing_generated_contract_keep_bounded_generic_guidance()
    {
        JsonException failure = new("PRIVATE", "$.query." + new string('x', 1024), 0, 0);

        Assert.Null(ApiRequestJson.JsonBodyFault(ArcanumJsonContext.Default.MemorySearchRequest, failure).Details);

        Assert.Null(ApiRequestJson.JsonBodyFault(null, new JsonException("PRIVATE", "$.query", 0, 0)).Details);
    }

    [SkippableTheory]
    [InlineData("/api/memory/search", "[]")]
    [InlineData("/api/memory/search", "{\"query\":}")]
    [InlineData("/api/lore", "[\"PRIVATE\"]")]
    public async Task Root_shape_and_syntax_refusals_have_honest_generic_guidance(string route, string body)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using HttpClient client = factory.CreateAuthenticatedClient();

        using StringContent content = new(body, Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await client.PostAsync(route, content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        string text = await response.Content.ReadAsStringAsync();

        using JsonDocument json = JsonDocument.Parse(text);

        Assert.Equal(ApiRequestJson.MalformedJsonMessage, json.RootElement.GetProperty("error").GetProperty("message").GetString());

        Assert.DoesNotContain("PRIVATE", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("GET", "/api/saga", true, 0, true)]
    [InlineData("HEAD", "/api/saga", true, 0, true)]
    [InlineData("DELETE", "/api/saga", true, 0, true)]
    [InlineData("POST", "/api/saga", true, 0, false)]
    [InlineData("GET", "/api/saga", false, 0, false)]
    [InlineData("GET", "/api/saga", true, 1, false)]
    [InlineData("GET", "/v1/models", true, 0, false)]
    [InlineData("GET", "/api-other/saga", true, 0, false)]
    public async Task Empty_parameter_refusals_are_confined_to_matched_bodyless_API_read_routes(
        string method, string path, bool matched, int contentLength, bool expectedEnvelope)
    {
        using ServiceProvider services = new ServiceCollection().AddLogging().BuildServiceProvider();

        DefaultHttpContext context = new() { RequestServices = services };

        context.Request.Method = method;

        context.Request.Path = path;

        context.Request.ContentLength = contentLength;

        context.Response.StatusCode = 400;

        using MemoryStream response = new();

        context.Response.Body = response;

        if (matched)
        {
            context.SetEndpoint(new Endpoint(static _ => Task.CompletedTask,
                new EndpointMetadataCollection(), "matched"));
        }

        await ApiBootstrapper.WriteBodyFaultEnvelopeAsync(new StatusCodeContext(
            context, new StatusCodePagesOptions(), static _ => Task.CompletedTask));

        Assert.Equal(400, context.Response.StatusCode);

        Assert.Equal(expectedEnvelope, response.Length > 0);
    }
}
