using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;
using static RetroDownfall.Arcanum.Tests.Packaging.PublishedArcanumSessionSmokeTests;

namespace RetroDownfall.Arcanum.Tests.Api.Tower;

[Collection("ApiHost")]
public sealed class SessionProviderHistoryOrderTests
{
    [SkippableTheory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task A_followup_sends_the_previous_tool_exchange_before_its_final_answer(bool withTool, bool reopen)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using var provider = await FakeOpenAiProvider.StartAsync(allowOrdinaryFollowUp: true);

        await using var profile = new RestartableArcanumProfileFixture();

        InMemoryOsCredentialStore credentials = new();

        const string credential = "ARCANUM_SESSION_PROVIDER_ORDER_TEST_KEY";

        string? previous = global::System.Environment.GetEnvironmentVariable(credential);

        global::System.Environment.SetEnvironmentVariable(credential, ProviderKey);

        try
        {
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(90));

            string firstPrompt = withTool ? FakeOpenAiProvider.OrdinaryToolPrompt : "answer without using tools";

            string finalAnswer = withTool ? FakeOpenAiProvider.OrdinaryToolFinal : "memory-unavailable";

            ArcanumWebApplicationFactory CreateHost()
            {
                var host = MemoryErasureRouteDriver.Host(credentials, profile, configure: settings =>
                {
                    settings.DefaultModel = ExpectedModel;
                    settings.Providers = [new ProviderSettings
                    {
                        Name = "history-order", Type = AiProviderKind.OpenAICompatible, Endpoint = provider.Endpoint.AbsoluteUri,
                        CredentialEnvironmentVariable = credential,
                        Models = [new ModelEntry(ExpectedModel, SupportsTools: true)], ContextWindowLimit = 8192
                    }];
                    settings.Features = new FeatureSettings
                    {
                        LongRest = false, CampaignRollups = false, Apprentices = false, Lexicon = false,
                        ArchiveSearch = false, Metrics = false, Scrying = false, Attachments = false,
                        Reasoning = false, WorkspaceChecks = false, Annals = false
                    };
                    ConfigureEvaluationPricing(settings);
                });

                var original = host.ServiceOverrides;

                host.ServiceOverrides = services =>
                {
                    original!(services);
                    services.RemoveAll<IArcanumIntelligenceProvider>();
                    services.AddScoped<IArcanumIntelligenceProvider>(sp => sp.GetRequiredService<WizardIntelligenceProvider>());
                };

                return host;
            }

            Guid session;

            EntryDto[] persisted;

            await using (var host = CreateHost())
            {
                using HttpClient client = host.CreateAuthenticatedClient();

                using HttpResponseMessage created = await client.PostAsync("api/sessions",
                    JsonContent.Create(new CreateSessionRequest(null, "provider history order"), ArcanumJsonContext.Default.CreateSessionRequest),
                    deadline.Token);

                Assert.Equal(HttpStatusCode.Created, created.StatusCode);

                var detail = await created.Content.ReadFromJsonAsync(ArcanumJsonContext.Default.ApiResponseSessionDetailDto, deadline.Token);

                session = detail!.Data!.Id;

                Assert.Equal(finalAnswer, await SendAsync(client, session, firstPrompt, deadline.Token));

                Assert.Equal(withTool ? 2 : 1, provider.RequestCount);

                persisted = await ReadEntriesInAppendOrderAsync(client, session, deadline.Token);

                Assert.Equal(withTool ? 4 : 2, persisted.Length);

                Assert.Equal(firstPrompt, persisted[0].Content);

                Assert.Equal(finalAnswer, persisted[1].Content);

                if (withTool)
                {
                    var messages = provider.Requests[1].Payload.GetProperty("messages").EnumerateArray().ToArray();

                    int call = Array.FindIndex(messages, message => message.TryGetProperty("tool_calls", out var calls)
                        && calls.EnumerateArray().Any(tool => tool.GetProperty("id").GetString() == "call-published-delegation"));

                    Assert.InRange(call, 0, messages.Length - 2);

                    Assert.Equal("call-published-delegation", messages[call + 1].GetProperty("tool_call_id").GetString());

                    Assert.StartsWith("[ToolCall: get_local_system_time", persisted[2].Content, StringComparison.Ordinal);

                    Assert.StartsWith("[ToolResult:", persisted[3].Content, StringComparison.Ordinal);
                }

                if (!reopen) await SendAsync(client, session, FakeOpenAiProvider.OrdinaryToolFollowUp, deadline.Token);
            }

            if (reopen)
            {
                await using var restarted = CreateHost();

                using HttpClient client = restarted.CreateAuthenticatedClient();

                Assert.Equal(persisted, await ReadEntriesInAppendOrderAsync(client, session, deadline.Token));

                await SendAsync(client, session, FakeOpenAiProvider.OrdinaryToolFollowUp, deadline.Token);
            }

            Assert.Equal(withTool ? 3 : 2, provider.RequestCount);

            var followUp = provider.Requests[^1];

            Assert.Equal(FakeOpenAiProvider.OrdinaryToolFollowUp, followUp.Prompt);

            string[] expected = withTool ? ["user", "call", "result", "answer", "followup"] : ["user", "answer", "followup"];

            Assert.Equal(expected, followUp.Payload.GetProperty("messages").EnumerateArray()
                .Select(message => Identify(message, firstPrompt, finalAnswer)).OfType<string>().ToArray());
        }
        finally
        {
            global::System.Environment.SetEnvironmentVariable(credential, previous);
        }
    }

    private static string? Identify(JsonElement message, string firstPrompt, string finalAnswer)
    {
        string role = message.GetProperty("role").GetString()!;

        if (role == "assistant" && message.TryGetProperty("tool_calls", out var calls)
            && calls.EnumerateArray().Any(tool => tool.GetProperty("id").GetString() == "call-published-delegation"))
            return "call";

        if (role == "tool" && message.TryGetProperty("tool_call_id", out var id) && id.GetString() == "call-published-delegation")
            return "result";

        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String) return null;

        string text = content.GetString()!;

        return role switch
        {
            "user" when text == firstPrompt => "user",
            "user" when text == FakeOpenAiProvider.OrdinaryToolFollowUp => "followup",
            "assistant" when text == finalAnswer => "answer",
            "assistant" when text.StartsWith("[ToolCall: get_local_system_time", StringComparison.Ordinal) => "call",
            "system" when text.StartsWith("[ToolResult:", StringComparison.Ordinal) => "result",
            _ => null
        };
    }

    private static async Task<EntryDto[]> ReadEntriesInAppendOrderAsync(HttpClient client, Guid session, CancellationToken cancellationToken)
    {
        var result = await client.GetFromJsonAsync($"api/sessions/{session:D}/entries", ArcanumJsonContext.Default.ApiResponseEntryDtoArray, cancellationToken);

        Assert.True(result!.IsSuccess);

        return result.Data!.Reverse().ToArray();
    }

    private static async Task<string> SendAsync(HttpClient client, Guid session, string prompt, CancellationToken cancellationToken)
    {
        var request = new PingRequest(prompt, Model: ExpectedModel, SessionId: session,
            DisableMcpTools: true, SkipSpellRouting: true, MaxOutputTokens: 256);

        using HttpRequestMessage message = new(HttpMethod.Post, "api/intelligence/ping")
        {
            Content = JsonContent.Create(request, ArcanumJsonContext.Default.PingRequest)
        };

        message.Headers.Add(ArcanumApiHeaders.IdempotencyKey, Guid.NewGuid().ToString("D"));

        using HttpResponseMessage response = await client.SendAsync(message, cancellationToken);

        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.True(response.IsSuccessStatusCode, body);

        var result = JsonSerializer.Deserialize(body, ArcanumJsonContext.Default.ApiResponsePromptResponseDto);

        Assert.True(result!.IsSuccess, body);

        return result.Data!.Text;
    }
}
