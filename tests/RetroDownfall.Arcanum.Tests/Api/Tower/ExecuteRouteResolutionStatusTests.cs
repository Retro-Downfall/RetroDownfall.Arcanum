using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api.Tower;

/// <summary>
/// The four execute routes (prompt and spell, buffered and streamed) answer a canonical Campaign/Session
/// resolution failure with that failure's own status code, not a flat 400.
/// </summary>
[Collection("ApiHost")]
public sealed class ExecuteRouteResolutionStatusTests
{
    public static TheoryData<string> Routes => new()
    {
        "prompt-execute",
        "prompt-execute-stream",
        "spell-execute",
        "spell-execute-stream",
    };

    [SkippableTheory]
    [MemberData(nameof(Routes))]
    public async Task Execute_with_an_unknown_sessionId_answers_404(string route)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await PostExecuteAsync(factory, client, route, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        Assert.Equal(ErrorCodes.Session.NotFound, await ReadErrorCodeAsync(response));
    }

    [SkippableTheory]
    [MemberData(nameof(Routes))]
    public async Task Execute_on_a_session_without_a_campaign_binding_answers_409(string route)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new()
        {
            ServiceOverrides = services =>
            {
                services.RemoveAll<ICanonicalCampaignContextResolver>();

                services.AddSingleton<ICanonicalCampaignContextResolver>(new FailingResolver(
                    new Error(ErrorCodes.Session.CampaignBindingRequired, "This Session must be resolved before it can be used.")));
            },
        };

        HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await PostExecuteAsync(factory, client, route, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        Assert.Equal(ErrorCodes.Session.CampaignBindingRequired, await ReadErrorCodeAsync(response));
    }

    private static async Task<HttpResponseMessage> PostExecuteAsync(
        ArcanumWebApplicationFactory factory,
        HttpClient client,
        string route,
        Guid sessionId)
    {
        if (route.StartsWith("prompt", StringComparison.Ordinal))
        {
            Guid promptId = await CreatePromptAsync(client);

            string suffix = route.EndsWith("stream", StringComparison.Ordinal) ? "execute-stream" : "execute";

            PromptExecuteRequest body = new("hello", SessionId: sessionId);

            return await client.PostAsync(
                $"/api/prompts/{promptId}/{suffix}",
                new StringContent(JsonSerializer.Serialize(body, ArcanumJsonContext.Default.PromptExecuteRequest), Encoding.UTF8, "application/json"));
        }

        string spellName = $"resolution-status-{Guid.NewGuid():N}";

        string spellDirectory = Path.Combine(factory.TempHome, spellName);

        Directory.CreateDirectory(spellDirectory);

        await File.WriteAllTextAsync(Path.Combine(spellDirectory, "SPELL.md"), $"# {spellName}\n\n---\n\nTest spell.");

        string spellSuffix = route.EndsWith("stream", StringComparison.Ordinal) ? "execute-stream" : "execute";

        SpellExecuteRequest spellBody = new("hello", SessionId: sessionId);

        return await client.PostAsync(
            $"/api/spells/{spellName}/{spellSuffix}?workspace={Uri.EscapeDataString(factory.TempHome)}",
            new StringContent(JsonSerializer.Serialize(spellBody, ArcanumJsonContext.Default.SpellExecuteRequest), Encoding.UTF8, "application/json"));
    }

    private static async Task<Guid> CreatePromptAsync(HttpClient client)
    {
        CreatePromptRequest request = new(
            $"resolution-status-{Guid.NewGuid():N}",
            "1.0.0",
            "Hello",
            null,
            [],
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);

        HttpResponseMessage response = await client.PostAsync(
            "/api/prompts",
            new StringContent(JsonSerializer.Serialize(request, ArcanumJsonContext.Default.CreatePromptRequest), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        ApiResponse<PromptDetailDto>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponsePromptDetailDto);

        return body!.Data!.Id;
    }

    /// <summary>The error code of the failure envelope, which every route writes in the same shape.</summary>
    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.TryGetProperty("error", out JsonElement error)
            && error.ValueKind == JsonValueKind.Object
            && error.TryGetProperty("code", out JsonElement code)
                ? code.GetString()
                : null;
    }

    private sealed class FailingResolver(Error error) : ICanonicalCampaignContextResolver
    {
        public ValueTask<Result<CanonicalCampaignContext>> ResolveAsync(
            CanonicalCampaignResolutionRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<CanonicalCampaignContext>.Failure(error));
    }
}
