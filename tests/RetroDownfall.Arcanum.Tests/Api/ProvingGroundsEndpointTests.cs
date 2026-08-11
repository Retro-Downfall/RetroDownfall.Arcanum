using System.Net;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.ProvingGrounds;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class ProvingGroundsEndpointTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public ProvingGroundsEndpointTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    [SkippableFact]
    public async Task RunTrial_ApprenticeGoal_ReturnsPassedTrialResult()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _factory.FakeIntelligence.NextText = "plan output";

        HttpClient client = _factory.CreateAuthenticatedClient();

        Trial trial = new(
            TargetKind: TrialTargetKind.ApprenticeGoal,
            Target: "Organize {{topic}}",
            Inquisitors: [new RegexInquisitor("plan output", ShouldMatch: true)],
            Variables: new Dictionary<string, string> { ["topic"] = "codex" },
            Name: "Trial");

        string payload = JsonSerializer.Serialize(trial, ArcanumJsonContext.Default.Trial);

        HttpResponseMessage response = await client.PostAsync(
            "/api/proving-grounds/trials/run",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<TrialResult>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseTrialResult);

        Assert.NotNull(body);

        Assert.True(body.IsSuccess);

        Assert.NotNull(body.Data);

        Assert.True(body.Data.Passed);

        Assert.Equal("plan output", body.Data.Output);

    }

    [SkippableFact]
    public async Task RunTrial_InvalidTrial_Returns400()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        Trial trial = new(
            TargetKind: TrialTargetKind.ApprenticeGoal,
            Target: string.Empty,
            Inquisitors: [new RegexInquisitor("x")]);

        string payload = JsonSerializer.Serialize(trial, ArcanumJsonContext.Default.Trial);

        HttpResponseMessage response = await client.PostAsync(
            "/api/proving-grounds/trials/run",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<TrialResult>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseTrialResult);

        Assert.NotNull(body);

        Assert.False(body.IsSuccess);

        Assert.Equal("ProvingGrounds.InvalidTrial", body.Error?.Code);

    }

    [SkippableFact]
    public async Task RunTrial_NullBody_ReturnsValidationInvalidBody()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync(
            "/api/proving-grounds/trials/run",
            new StringContent("null", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<TrialResult>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseTrialResult);

        Assert.NotNull(body);

        Assert.False(body.IsSuccess);

        Assert.Equal("Validation.InvalidBody", body.Error?.Code);

    }

    [SkippableFact]
    public async Task RunTrial_InferenceFailure_Returns500()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _factory.FakeIntelligence.NextFailure = new Error("Hub.Model", "unavailable");

        _factory.FakeIntelligence.NextText = string.Empty;

        HttpClient client = _factory.CreateAuthenticatedClient();

        Trial trial = new(
            TargetKind: TrialTargetKind.ApprenticeGoal,
            Target: "Organize notes",
            Inquisitors: [new RegexInquisitor("ok", ShouldMatch: true)]);

        string payload = JsonSerializer.Serialize(trial, ArcanumJsonContext.Default.Trial);

        HttpResponseMessage response = await client.PostAsync(
            "/api/proving-grounds/trials/run",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<TrialResult>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseTrialResult);

        Assert.NotNull(body);

        Assert.False(body.IsSuccess);

        Assert.Equal("ProvingGrounds.InferenceFailed", body.Error?.Code);

        _factory.FakeIntelligence.NextFailure = null;

    }

    [SkippableFact]
    public async Task RunTrial_SpellNotFound_Returns404()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        Trial trial = new(
            TargetKind: TrialTargetKind.Spell,
            Target: "definitely-missing-spell-name",
            Inquisitors: [new RegexInquisitor("x")]);

        string payload = JsonSerializer.Serialize(trial, ArcanumJsonContext.Default.Trial);

        HttpResponseMessage response = await client.PostAsync(
            "/api/proving-grounds/trials/run",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<TrialResult>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseTrialResult);

        Assert.NotNull(body);

        Assert.False(body.IsSuccess);

        Assert.Equal("ProvingGrounds.SpellNotFound", body.Error?.Code);

    }

    /// <summary>
    /// DESIGN §11.3: this route binds <c>Trial</c> from the body, so it is the shape that proves the
    /// API key is checked before binding. While the gate was an endpoint filter — which minimal APIs
    /// run <em>after</em> binding — an anonymous caller's body was deserialized first and the reply was
    /// the framework's body-parse <c>400</c> instead of a <c>401</c>.
    /// </summary>
    [SkippableFact]
    public async Task RunTrial_WithoutApiKey_AndUnparsableBody_Returns401NotABindingFailure()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.PostAsync(
            "/api/proving-grounds/trials/run",
            new StringContent("""{"targetKind":""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

    }

}
