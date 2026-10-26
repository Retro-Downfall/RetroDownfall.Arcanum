using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
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
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CurrentTurnPii_RealWizardRoutes_AllowHistoryAndRejectCurrentPii(bool openAi, bool stream)
    {
        await using ArcanumWebApplicationFactory factory = new();

        factory.SettingsOverride = settings => settings with
        {
            Features = settings.Features with { Guardrails = true, Lexicon = false, Reasoning = false },
        };

        GuardrailsChatClient chat = new();

        factory.ServiceOverrides = services =>
        {
            services.RemoveAll<IArcanumIntelligenceProvider>();

            services.AddScoped<IArcanumIntelligenceProvider>(sp => sp.GetRequiredService<WizardIntelligenceProvider>());

            services.RemoveAll<IChatClientFactory>();

            services.AddSingleton<IChatClientFactory>(sp => new GuardrailsChatFactory(chat, sp.GetRequiredService<IOptions<ArcanumSettings>>().Value.Providers[0]));
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        string route = openAi ? "/v1/chat/completions" : stream ? "/api/intelligence/ping-stream" : "/api/intelligence/ping";

        foreach (bool hasPii in new[] { false, true })
        {
            string current = hasPii ? "alice@example.com" : "epoch 1729800000; continue";

            string messages = "[{\"role\":\"user\",\"content\":\"old@example.com\"},{\"role\":\"assistant\",\"content\":\"123-45-6789\"},{\"role\":\"tool\",\"toolCallId\":\"old\",\"content\":\"4111111111111111\"},{\"role\":\"user\",\"content\":\"" + current + "\"},{\"role\":\"assistant\",\"content\":\"Continue\"}]";

            if (hasPii)
            {
                string replacement = openAi
                    ? "\"content\":[{\"type\":\"text\",\"text\":\"alice@example.com\"}]"
                    : "\"content\":\"Safe fallback\",\"contentParts\":[{\"kind\":\"text\",\"text\":\"alice@example.com\"}]";

                messages = messages.Replace("\"content\":\"alice@example.com\"", replacement, StringComparison.Ordinal);
            }

            string payload = openAi
                ? "{\"model\":\"mistral:latest\",\"stream\":" + (stream ? "true" : "false") + ",\"messages\":" + messages + "}"
                : "{\"prompt\":\"\",\"skipSpellRouting\":true,\"disableMcpTools\":true,\"statelessMessages\":" + messages + "}";

            using HttpResponseMessage response = await client.PostAsync(route, new StringContent(payload, Encoding.UTF8, "application/json"));

            string body = await response.Content.ReadAsStringAsync();

            if (hasPii)
            {
                Assert.Equal(stream ? HttpStatusCode.OK : HttpStatusCode.BadRequest, response.StatusCode);

                Assert.Contains(openAi ? stream ? "content_filter" : "inference_failed" : ErrorCodes.Guardrails.PiiDetected, body, StringComparison.Ordinal);

                Assert.Contains("pii-email", body, StringComparison.Ordinal);

                Assert.Contains("Redact", body, StringComparison.Ordinal);

                Assert.DoesNotContain(current, body, StringComparison.Ordinal);

                Assert.Equal(1, chat.Calls);
            }
            else
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);

                Assert.Contains("Clean answer", body, StringComparison.Ordinal);

                Assert.Equal(1, chat.Calls);
            }
        }
    }

    [Fact]
    public async Task CurrentTurnPii_ConfigEndpoint_RetainsIndependentSwitches()
    {
        await using ArcanumWebApplicationFactory factory = new();

        factory.SettingsOverride = settings => settings with
        {
            Security = settings.Security with
            {
                Guardrails = new GuardrailsPolicySettings
                {
                    DetectEmail = false,
                    DetectPhone = true,
                    DetectSsn = false,
                    DetectCreditCard = true,
                },
            },
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.GetAsync("/api/config");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResponse<ArcanumSettings>? body = JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(), ArcanumJsonContext.Default.ApiResponseArcanumSettings);

        Assert.NotNull(body?.Data);

        GuardrailsPolicySettings policy = body.Data.Security.Guardrails;

        Assert.True(policy.DetectPii);

        Assert.False(policy.DetectEmail);

        Assert.True(policy.DetectPhone);

        Assert.False(policy.DetectSsn);

        Assert.True(policy.DetectCreditCard);
    }

    private sealed class GuardrailsChatFactory(GuardrailsChatClient chat, ProviderSettings provider) : IChatClientFactory
    {
        public Task<ChatClientLease> ResolveClientAsync(string? targetModel, CancellationToken cancellationToken) =>
            Task.FromResult(new ChatClientLease(chat, provider, provider.Models[0].Name, ownedHttpClient: null));

        public Task<ChatClientLease> ResolveClientAsync(ProviderSettings resolvedProvider, string resolvedModel, CancellationToken cancellationToken) =>
            ResolveClientAsync(resolvedModel, cancellationToken);
    }

    private sealed class GuardrailsChatClient : IChatClient
    {
        public int Calls { get; private set; }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Clean answer")) { FinishReason = ChatFinishReason.Stop });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;

            await Task.CompletedTask;

            yield return new ChatResponseUpdate(ChatRole.Assistant, "Clean answer") { FinishReason = ChatFinishReason.Stop };
        }
    }

    [SkippableFact]
    public async Task GetGuardrailsAudit_WhenDisabled_Returns200EmptyList()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

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

        await using ArcanumWebApplicationFactory factory = new();

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

        await using ArcanumWebApplicationFactory factory = new()
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
