using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api.Tower;

[Collection("ApiHost")]
public sealed class ApiInputDiagnosticsEndpointTests
{
    [SkippableTheory]
    [InlineData(false, "Archived", "archived")]
    [InlineData(true, "ARCHIVED", "archived")]
    [InlineData(false, " Active ", "active")]
    [InlineData(true, " ALL ", "all")]
    [InlineData(false, "archived", "archived")]
    [InlineData(true, "", "active")]
    [InlineData(false, "archvied", null)]
    [InlineData(true, "bogus", null)]
    public async Task Session_lists_validate_and_normalize_status_before_native_filtering(bool campaignRoute, string status, string? expected)
    {
        Available();

        await using ArcanumWebApplicationFactory factory = new();

        HttpClient client = factory.CreateAuthenticatedClient();

        Guid campaign = await CampaignAsync(factory, client);

        Guid active = await SessionAsync(client, campaign);

        Guid archived = await SessionAsync(client, campaign);

        Assert.Equal(HttpStatusCode.OK, (await PostJsonAsync(client, $"/api/sessions/{archived:D}", new UpdateSessionRequest(null, "archived"), ArcanumJsonContext.Default.UpdateSessionRequest, HttpMethod.Patch)).StatusCode);

        string route = campaignRoute ? $"/api/campaigns/{campaign:D}/sessions?" : $"/api/sessions?campaignId={campaign:D}&";

        HttpResponseMessage response = await client.GetAsync(route + "status=" + Uri.EscapeDataString(status));

        if (expected is null)
        {
            await ErrorAsync(response, HttpStatusCode.BadRequest, ErrorCodes.Session.InvalidStatus);

            return;
        }

        SessionQueryResult data = await DataAsync(response, ArcanumJsonContext.Default.ApiResponseSessionQueryResult);

        Guid[] ids = [.. data.Summaries.Select(static row => row.Id)];

        Assert.Equal(expected == "all" ? 2 : 1, ids.Length);

        Assert.Contains(expected == "archived" ? archived : active, ids);
    }

    [SkippableTheory]
    [InlineData("", null)]
    [InlineData("\"role\":null,", null)]
    [InlineData("\"role\":7,", null)]
    [InlineData("\"role\":-1,", null)]
    [InlineData("\"role\":\"system\",", "system")]
    [InlineData("\"role\":\"user\",", "user")]
    [InlineData("\"role\":\"assistant\",", "assistant")]
    [InlineData("\"role\":\"tool\",", "tool")]
    [InlineData("\"role\":0,", "system")]
    [InlineData("\"role\":1,", "user")]
    [InlineData("\"role\":2,", "assistant")]
    [InlineData("\"role\":3,", "tool")]
    public async Task Append_requires_an_explicit_defined_role_without_persisting_invalid_entries(string role, string? expected)
    {
        Available();

        await using ArcanumWebApplicationFactory factory = new();

        HttpClient client = factory.CreateAuthenticatedClient();

        Guid session = await SessionAsync(client);

        HttpResponseMessage response = await client.PostAsync($"/api/sessions/{session:D}/entries", Body("{" + role + "\"content\":\"role validation\"}"));

        if (expected is null)
        {
            string message = await ErrorAsync(response, HttpStatusCode.BadRequest, ErrorCodes.Validation.InvalidBody);

            Assert.Contains("role", message, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            EntryDto entry = await DataAsync(response, ArcanumJsonContext.Default.ApiResponseEntryDto);

            Assert.Equal(expected, entry.Role);
        }

        EntryDto[] stored = await DataAsync(await client.GetAsync($"/api/sessions/{session:D}/entries"), ArcanumJsonContext.Default.ApiResponseEntryDtoArray);

        Assert.Equal(expected is null ? 0 : 1, stored.Length);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Prompt_creation_requires_template_and_does_not_reach_persistence(bool explicitNull)
    {
        Available();

        await using ArcanumWebApplicationFactory factory = new();

        HttpClient client = factory.CreateAuthenticatedClient();

        string name = "template-" + Guid.NewGuid().ToString("N");

        string json = "{\"name\":\"" + name + "\",\"version\":\"1\"" + (explicitNull ? ",\"template\":null}" : "}");

        string message = await ErrorAsync(await client.PostAsync("/api/prompts", Body(json)), HttpStatusCode.BadRequest, ErrorCodes.Prompt.InvalidRequest);

        Assert.Contains("template", message, StringComparison.OrdinalIgnoreCase);

        // A refusal must leave the same name/version available for a later valid create.
        await DataAsync(await client.PostAsync("/api/prompts", Body("{\"name\":\"" + name + "\",\"version\":\"1\",\"template\":\"valid\"}")), ArcanumJsonContext.Default.ApiResponsePromptDetailDto, HttpStatusCode.Created);
    }

    [SkippableTheory]
    [InlineData(156, true, 156)]
    [InlineData(155, true, 157)]
    [InlineData(161, false, 157)]
    [InlineData(160, false, 160)]
    public async Task Default_pin_labels_round_trip_without_splitting_supplementary_scalars(int prefix, bool emoji, int kept)
    {
        Available();

        await using ArcanumWebApplicationFactory factory = new();

        HttpClient client = factory.CreateAuthenticatedClient();

        Guid session = await SessionAsync(client);

        string target = new('a', prefix);

        target += emoji ? "😀abcdefghij" : string.Empty;

        CreateSessionContextPinRequest request = new(SessionContextPinKind.Diagnostic, target);

        SessionContextPinDto pin = await DataAsync(await PostJsonAsync(client, $"/api/sessions/{session:D}/context-pins", request, ArcanumJsonContext.Default.CreateSessionContextPinRequest), ArcanumJsonContext.Default.ApiResponseSessionContextPinDto);

        string expected = target.Length > 160 ? target[..kept] + "..." : target;

        Assert.Equal(expected, pin.DisplayLabel);

        Assert.DoesNotContain('\uFFFD', pin.DisplayLabel);

        Assert.Equal(target, pin.TargetIdentifier);

        SessionContextPinDto[] persisted = await DataAsync(await client.GetAsync($"/api/sessions/{session:D}/context-pins"), ArcanumJsonContext.Default.ApiResponseSessionContextPinDtoArray);

        Assert.Equal(pin.DisplayLabel, Assert.Single(persisted).DisplayLabel);
    }

    [SkippableTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Explicit_missing_saga_session_is_refused_without_embedding(bool scoped, bool divine)
    {
        Available();

        ProbeWeave weave = new();

        await using ArcanumWebApplicationFactory factory = Enabled(weave, scoped);

        HttpClient client = factory.CreateAuthenticatedClient();

        Guid missing = Guid.NewGuid();

        HttpResponseMessage response = divine
            ? await PostJsonAsync(client, "/api/saga/divine", new SagaSearchRequest("missing-session-probe", SessionId: missing), ArcanumJsonContext.Default.SagaSearchRequest)
            : await client.GetAsync($"/api/saga?sessionId={missing:D}");

        Assert.True(weave.Requests == 0, $"Missing Session performed {weave.Requests} fake query embedding calls (HTTP {(int)response.StatusCode}); expected 404 and no embedding.");

        await ErrorAsync(response, HttpStatusCode.NotFound, ErrorCodes.Session.NotFound);
    }

    [SkippableTheory]
    [InlineData("DELETE", "entries/{entry}")]
    [InlineData("POST", "entries/{entry}/pin")]
    [InlineData("POST", "compact")]
    [InlineData("DELETE", "entries/{entry}/pin")]
    public async Task Disabled_memory_management_explains_both_configuration_key_spellings(string method, string suffix)
    {
        Available();

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings => settings with { Features = settings.Features with { MemoryManagement = false } },
        };

        HttpClient client = factory.CreateAuthenticatedClient();

        string route = $"/api/sessions/{Guid.NewGuid():D}/" + suffix.Replace("{entry}", Guid.NewGuid().ToString("D"), StringComparison.Ordinal);

        using HttpRequestMessage request = new(new HttpMethod(method), route);

        string message = await ErrorAsync(await client.SendAsync(request), HttpStatusCode.BadRequest, ErrorCodes.Session.MemoryManagementDisabled);

        Assert.Contains("Arcanum:Features:MemoryManagement", message, StringComparison.Ordinal);

        Assert.Contains("features.memoryManagement", message, StringComparison.Ordinal);
    }

    [SkippableTheory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("  literal {{instruction}} 😀  ")]
    public async Task Empty_and_literal_prompt_templates_remain_valid_and_exact(string template)
    {
        Available();

        await using ArcanumWebApplicationFactory factory = new();

        HttpClient client = factory.CreateAuthenticatedClient();

        CreatePromptRequest request = new("literal-" + Guid.NewGuid().ToString("N"), "1", template, null, null, null, null, null, null, null, null, null, null);

        PromptDetailDto created = await DataAsync(await PostJsonAsync(client, "/api/prompts", request, ArcanumJsonContext.Default.CreatePromptRequest), ArcanumJsonContext.Default.ApiResponsePromptDetailDto, HttpStatusCode.Created);

        PromptDetailDto stored = await DataAsync(await client.GetAsync($"/api/prompts/{created.Id:D}"), ArcanumJsonContext.Default.ApiResponsePromptDetailDto);

        Assert.Equal(template, stored.Template);
    }

    [Fact]
    public void Api_generated_role_contract_distinguishes_omission_from_explicit_system()
    {
        AppendEntryRequest omitted = JsonSerializer.Deserialize("{\"content\":\"note\"}", ArcanumJsonContext.Default.AppendEntryRequest)!;

        Assert.Null(omitted.Role);

        string explicitSystem = JsonSerializer.Serialize(new AppendEntryRequest(MessageRole.System, "note"), ArcanumJsonContext.Default.AppendEntryRequest);

        using JsonDocument wire = JsonDocument.Parse(explicitSystem);

        Assert.Equal("system", wire.RootElement.GetProperty("role").GetString());
    }

    [SkippableFact]
    public async Task Explicit_pin_label_is_preserved_instead_of_derived_from_target()
    {
        Available();

        await using ArcanumWebApplicationFactory factory = new();

        HttpClient client = factory.CreateAuthenticatedClient();

        Guid session = await SessionAsync(client);

        CreateSessionContextPinRequest request = new(SessionContextPinKind.Diagnostic, new string('a', 156) + "😀suffix", "Operator 😀 label");

        SessionContextPinDto pin = await DataAsync(await PostJsonAsync(client, $"/api/sessions/{session:D}/context-pins", request, ArcanumJsonContext.Default.CreateSessionContextPinRequest), ArcanumJsonContext.Default.ApiResponseSessionContextPinDto);

        Assert.Equal(request.DisplayLabel, pin.DisplayLabel);
    }

    [SkippableTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Saga_existing_and_absent_session_selectors_remain_available(bool scoped, bool divine)
    {
        Available();

        ProbeWeave weave = new();

        await using ArcanumWebApplicationFactory factory = Enabled(weave, scoped);

        HttpClient client = factory.CreateAuthenticatedClient();

        Guid session = await SessionAsync(client);

        if (divine)
        {
            foreach (Guid? selector in new Guid?[] { session, null })
            {
                SagaSearchResult result = await DataAsync(await PostJsonAsync(client, "/api/saga/divine", new SagaSearchRequest("operator-probe", SessionId: selector), ArcanumJsonContext.Default.SagaSearchRequest), ArcanumJsonContext.Default.ApiResponseSagaSearchResult);

                Assert.Empty(result.Memories);
            }

            Assert.Equal(2, weave.Requests);
        }
        else
        {
            foreach (string route in new[] { $"/api/saga?sessionId={session:D}", "/api/saga" })
            {
                SagaMemoryDto[] result = await DataAsync(await client.GetAsync(route), ArcanumJsonContext.Default.ApiResponseSagaMemoryDtoArray);

                Assert.Empty(result);
            }

            Assert.Equal(0, weave.Requests);
        }
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Saga_divine_keeps_feature_then_body_validation_before_missing_session(bool enabled)
    {
        Available();

        ProbeWeave weave = new();

        await using ArcanumWebApplicationFactory factory = Enabled(weave, false);

        Func<ArcanumSettings, ArcanumSettings> previous = factory.SettingsOverride!;

        factory.SettingsOverride = settings =>
        {
            ArcanumSettings configured = previous(settings);

            return configured with { Features = configured.Features with { Saga = enabled } };
        };

        HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await PostJsonAsync(client, "/api/saga/divine", new SagaSearchRequest(enabled ? " " : "valid", SessionId: Guid.NewGuid()), ArcanumJsonContext.Default.SagaSearchRequest);

        await ErrorAsync(response, enabled ? HttpStatusCode.BadRequest : HttpStatusCode.ServiceUnavailable, enabled ? ErrorCodes.Validation.InvalidBody : ErrorCodes.Embeddings.FeatureDisabled);

        Assert.Equal(0, weave.Requests);
    }

    [SkippableFact]
    public async Task Saga_list_keeps_query_length_validation_before_session_lookup()
    {
        Available();

        await using ArcanumWebApplicationFactory factory = new();

        await ErrorAsync(await factory.CreateAuthenticatedClient().GetAsync($"/api/saga?sessionId={Guid.NewGuid():D}&q={new string('q', 4097)}"), HttpStatusCode.BadRequest, ErrorCodes.Validation.InvalidBody);
    }

    [SkippableFact]
    public async Task Existing_saga_session_preserves_provider_unavailable_refusal_without_embedding()
    {
        Available();

        ProbeWeave weave = new() { Available = false };

        await using ArcanumWebApplicationFactory factory = Enabled(weave, false);

        HttpClient client = factory.CreateAuthenticatedClient();

        Guid session = await SessionAsync(client);

        await ErrorAsync(await PostJsonAsync(client, "/api/saga/divine", new SagaSearchRequest("provider-probe", SessionId: session), ArcanumJsonContext.Default.SagaSearchRequest), HttpStatusCode.ServiceUnavailable, ErrorCodes.Embeddings.ProviderUnavailable);

        Assert.Equal(0, weave.Requests);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Valid_role_keeps_missing_and_archived_session_refusals(bool archived)
    {
        Available();

        await using ArcanumWebApplicationFactory factory = new();

        HttpClient client = factory.CreateAuthenticatedClient();

        Guid session = archived ? await SessionAsync(client) : Guid.NewGuid();

        if (archived)
        {
            Assert.Equal(HttpStatusCode.OK, (await PostJsonAsync(client, $"/api/sessions/{session:D}", new UpdateSessionRequest(null, "archived"), ArcanumJsonContext.Default.UpdateSessionRequest, HttpMethod.Patch)).StatusCode);
        }

        await ErrorAsync(await PostJsonAsync(client, $"/api/sessions/{session:D}/entries", new AppendEntryRequest(MessageRole.User, "valid"), ArcanumJsonContext.Default.AppendEntryRequest), archived ? HttpStatusCode.BadRequest : HttpStatusCode.NotFound, archived ? ErrorCodes.Session.Archived : ErrorCodes.Session.NotFound);
    }

    [SkippableFact]
    public async Task Valid_role_keeps_the_existing_empty_content_diagnostic()
    {
        Available();

        await using ArcanumWebApplicationFactory factory = new();

        HttpClient client = factory.CreateAuthenticatedClient();

        Guid session = await SessionAsync(client);

        await ErrorAsync(await PostJsonAsync(client, $"/api/sessions/{session:D}/entries", new AppendEntryRequest(MessageRole.User, " "), ArcanumJsonContext.Default.AppendEntryRequest), HttpStatusCode.BadRequest, ErrorCodes.Session.EmptyContent);
    }

    private static void Available() => Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    private static Task<HttpResponseMessage> PostJsonAsync<T>(HttpClient client, string route, T value, JsonTypeInfo<T> type, HttpMethod? method = null) =>
        client.SendAsync(new HttpRequestMessage(method ?? HttpMethod.Post, route) { Content = Body(JsonSerializer.Serialize(value, type)) });

    private static async Task<T> DataAsync<T>(HttpResponseMessage response, JsonTypeInfo<ApiResponse<T>> type, HttpStatusCode status = HttpStatusCode.OK)
    {
        string json = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == status, $"Expected {status}, got {response.StatusCode}: {json}");

        ApiResponse<T> body = Assert.IsType<ApiResponse<T>>(JsonSerializer.Deserialize(json, type));

        Assert.True(body.IsSuccess, json);

        Assert.NotNull(body.Data);

        return body.Data;
    }

    private static async Task<string> ErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        string json = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == status, $"Expected {status}, got {response.StatusCode}: {json}");

        using JsonDocument body = JsonDocument.Parse(json);

        Assert.False(body.RootElement.GetProperty("isSuccess").GetBoolean());

        JsonElement error = body.RootElement.GetProperty("error");

        Assert.Equal(code, error.GetProperty("code").GetString());

        return error.GetProperty("message").GetString()!;
    }

    private static async Task<Guid> SessionAsync(HttpClient client, Guid? campaign = null) =>
        (await DataAsync(await PostJsonAsync(client, "/api/sessions", new CreateSessionRequest(campaign, "Input validation"), ArcanumJsonContext.Default.CreateSessionRequest), ArcanumJsonContext.Default.ApiResponseSessionDetailDto, HttpStatusCode.Created)).Id;

    private static async Task<Guid> CampaignAsync(ArcanumWebApplicationFactory factory, HttpClient client)
    {
        string path = Path.Combine(factory.TempHome, "input-validation-campaign");

        Directory.CreateDirectory(path);

        return (await DataAsync(await PostJsonAsync(client, "/api/campaigns", new RegisterCampaignRequest("Input validation", path, WorkspaceType.Campaign, null), ArcanumJsonContext.Default.RegisterCampaignRequest), ArcanumJsonContext.Default.ApiResponseCampaignDto, HttpStatusCode.Created)).Id;
    }

    private static ArcanumWebApplicationFactory Enabled(ProbeWeave weave, bool scoped) => new()
    {
        SettingsOverride = settings => settings with
        {
            Features = settings.Features with { Embeddings = true, Saga = true, CampaignScopedMemory = scoped },
            Integrations = settings.Integrations with { Embeddings = settings.Integrations.Embeddings with { Provider = "test", Model = "test-embed" } },
        },
        ServiceOverrides = services =>
        {
            services.RemoveAll<IWeaveService>();

            services.AddSingleton<IWeaveService>(weave);
        },
    };

    private sealed class ProbeWeave : IWeaveService
    {
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        public bool Available { get; set; } = true;

        public bool IsAvailable => Available;

        public Task<Result<Embedding<float>>> EmbedAsync(string text, CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _requests);

            return Task.FromResult(Result<Embedding<float>>.Success(new(new float[64])));
        }

        public Task<Result<Embedding<float>[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _requests);

            return Task.FromResult(Result<Embedding<float>[]>.Success([.. texts.Select(static _ => new Embedding<float>(new float[64]))]));
        }

        public Task<Result<(string Chunk, int Offset)[]>> ChunkAsync(string text, CancellationToken cancellationToken) =>
            Task.FromResult(Result<(string Chunk, int Offset)[]>.Success([(text, 0)]));
    }
}
