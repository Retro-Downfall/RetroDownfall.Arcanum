using System.Net;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class ClientToolForwardingTests
{
    [SkippableFact]
    public async Task PostChatCompletions_ClientToolForwardingEnabled_ForwardsToolsAndSurfacesToolCalls()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings => settings with
            {
                Features = settings.Features with { ClientTools = true },
            },
        };

        factory.FakeIntelligence.NextToolCalls =
        [
            new PromptToolCall("call_provider_minted_abc123", "get_weather", "{\"location\":\"NYC\"}"),
        ];

        factory.FakeIntelligence.NextFinishReason = "tool_calls";

        HttpClient client = factory.CreateAuthenticatedClient();

        string payload = """
            {
              "model": "mistral:latest",
              "messages": [
                { "role": "user", "content": "What is the weather?" }
              ],
              "tools": [
                {
                  "type": "function",
                  "function": {
                    "name": "get_weather",
                    "description": "Get the weather for a location.",
                    "parameters": {
                      "type": "object",
                      "properties": {
                        "location": { "type": "string" }
                      },
                      "required": ["location"]
                    }
                  }
                }
              ],
              "tool_choice": "auto"
            }
            """;

        HttpResponseMessage response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        PingRequest? lastRequest = factory.FakeIntelligence.LastRequest;

        Assert.NotNull(lastRequest);
        Assert.True(lastRequest.ForwardClientTools);
        Assert.NotNull(lastRequest.ClientTools);
        Assert.Single(lastRequest.ClientTools);
        Assert.Equal("get_weather", lastRequest.ClientTools[0].Function?.Name);

        string json = await response.Content.ReadAsStringAsync();

        OpenAiChatResponse? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.OpenAiChatResponse);

        Assert.NotNull(body);

        OpenAiToolCall[]? toolCalls = body!.Choices[0].Message.ToolCalls;

        Assert.NotNull(toolCalls);
        Assert.Single(toolCalls);
        Assert.Equal("call_provider_minted_abc123", toolCalls[0].Id);
        Assert.Equal("get_weather", toolCalls[0].Function?.Name);
    }

    [SkippableFact]
    public async Task PostChatCompletions_ClientToolForwardingDisabled_Returns400Disabled()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        HttpClient client = factory.CreateAuthenticatedClient();

        string payload = """
            {
              "model": "mistral:latest",
              "messages": [
                { "role": "user", "content": "What is the weather?" }
              ],
              "tools": [
                {
                  "type": "function",
                  "function": {
                    "name": "get_weather",
                    "parameters": { "type": "object", "properties": {} }
                  }
                }
              ]
            }
            """;

        HttpResponseMessage response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        OpenAiErrorResponse? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.OpenAiErrorResponse);

        Assert.NotNull(body);
        Assert.Equal("unsupported_parameter", body!.Error.Code);
        Assert.Equal("tools", body.Error.Param);
    }

    [SkippableFact]
    public async Task PostChatCompletions_TooManyClientTools_Returns400TooMany()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings => settings with
            {
                Features = settings.Features with { ClientTools = true },
            },
        };

        HttpClient client = factory.CreateAuthenticatedClient();

        int maxClientTools = ArcanumSettingClamps.ClientToolForwardingMaxClientTools(
            ArcanumRuntimeDefaults.ClientTools.MaxClientTools);
        string payload = JsonSerializer.Serialize(new
        {
            model = "mistral:latest",
            messages = new[] { new { role = "user", content = "What is the weather?" } },
            tools = Enumerable.Range(0, maxClientTools + 1)
                .Select(i => new
                {
                    type = "function",
                    function = new
                    {
                        name = $"tool_{i}",
                        parameters = new { type = "object" },
                    },
                })
                .ToArray(),
        });

        HttpResponseMessage response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        OpenAiErrorResponse? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.OpenAiErrorResponse);

        Assert.NotNull(body);
        Assert.Equal("too_many_tools", body!.Error.Code);
        Assert.Equal("tools", body.Error.Param);
    }

    [SkippableFact]
    public async Task PostChatCompletions_InvalidClientToolSchema_Returns400InvalidSchema()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings => settings with
            {
                Features = settings.Features with { ClientTools = true },
            },
        };

        HttpClient client = factory.CreateAuthenticatedClient();

        string payload = """
            {
              "model": "mistral:latest",
              "messages": [
                { "role": "user", "content": "What is the weather?" }
              ],
              "tools": [
                {
                  "type": "function",
                  "function": {
                    "name": "get_weather",
                    "parameters": "not-an-object"
                  }
                }
              ]
            }
            """;

        HttpResponseMessage response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        OpenAiErrorResponse? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.OpenAiErrorResponse);

        Assert.NotNull(body);
        Assert.Equal("invalid_schema", body!.Error.Code);
        Assert.Equal("tools", body.Error.Param);
    }

    [SkippableFact]
    public async Task PostChatCompletions_InvalidClientToolChoice_Returns400InvalidSchema()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings => settings with
            {
                Features = settings.Features with { ClientTools = true },
            },
        };

        HttpClient client = factory.CreateAuthenticatedClient();

        string payload = """
            {
              "model": "mistral:latest",
              "messages": [
                { "role": "user", "content": "What is the weather?" }
              ],
              "tools": [
                {
                  "type": "function",
                  "function": {
                    "name": "get_weather",
                    "parameters": { "type": "object" }
                  }
                }
              ],
              "tool_choice": { "type": "function", "function": {} }
            }
            """;

        HttpResponseMessage response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        OpenAiErrorResponse? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.OpenAiErrorResponse);

        Assert.NotNull(body);
        Assert.Equal("invalid_schema", body!.Error.Code);
        Assert.Equal("tool_choice", body.Error.Param);
    }

    [SkippableTheory]
    [InlineData(false, "\"required\"")]
    [InlineData(true, "\"required\"")]
    [InlineData(false, "{\"type\":\"function\",\"function\":{\"name\":\"get_weather\"}}")]
    [InlineData(true, "{\"type\":\"function\",\"function\":{\"name\":\"get_weather\"}}")]
    public async Task PostChatCompletions_RequiredToolChoiceWithoutTools_Returns400InvalidValue(
        bool includeEmptyTools,
        string toolChoiceJson)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings => settings with
            {
                Features = settings.Features with { ClientTools = true },
            },
        };

        HttpClient client = factory.CreateAuthenticatedClient();

        string toolsMember = includeEmptyTools ? "\"tools\": []," : string.Empty;
        string payload = $$"""
            {
              "model": "mistral:latest",
              "messages": [
                { "role": "user", "content": "Use a tool." }
              ],
              {{toolsMember}}
              "tool_choice": {{toolChoiceJson}}
            }
            """;

        HttpResponseMessage response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Equal(0, factory.FakeIntelligence.ExecutePromptCallCount);

        string json = await response.Content.ReadAsStringAsync();

        OpenAiErrorResponse? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.OpenAiErrorResponse);

        Assert.NotNull(body);
        Assert.Equal("invalid_request_error", body!.Error.Type);
        Assert.Equal("invalid_value", body.Error.Code);
        Assert.Equal("tool_choice", body.Error.Param);
    }

    [SkippableTheory]
    [InlineData("{\"type\":7,\"function\":{\"name\":\"get_weather\"}}")]
    [InlineData("{\"type\":\"function\",\"function\":[]}")]
    public async Task PostChatCompletions_MalformedToolChoiceKinds_Return400InvalidSchema(
        string toolChoiceJson)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings => settings with
            {
                Features = settings.Features with { ClientTools = true },
            },
        };

        HttpClient client = factory.CreateAuthenticatedClient();

        string payload = $$"""
            {
              "model": "mistral:latest",
              "messages": [
                { "role": "user", "content": "Use the weather tool." }
              ],
              "tools": [
                {
                  "type": "function",
                  "function": {
                    "name": "get_weather",
                    "parameters": { "type": "object" }
                  }
                }
              ],
              "tool_choice": {{toolChoiceJson}}
            }
            """;

        HttpResponseMessage response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        OpenAiErrorResponse? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.OpenAiErrorResponse);

        Assert.NotNull(body);
        Assert.Equal("invalid_schema", body!.Error.Code);
        Assert.Equal("tool_choice", body.Error.Param);
        Assert.Equal(0, factory.FakeIntelligence.ExecutePromptCallCount);
    }

    [SkippableFact]
    public async Task PostChatCompletions_NullClientTool_Returns400InvalidSchema()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings => settings with
            {
                Features = settings.Features with { ClientTools = true },
            },
        };

        HttpClient client = factory.CreateAuthenticatedClient();

        string payload = """
            {
              "model": "mistral:latest",
              "messages": [
                { "role": "user", "content": "Use a tool." }
              ],
              "tools": [null]
            }
            """;

        HttpResponseMessage response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        OpenAiErrorResponse? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.OpenAiErrorResponse);

        Assert.NotNull(body);
        Assert.Equal("invalid_schema", body!.Error.Code);
        Assert.Equal("tools", body.Error.Param);
        Assert.Equal(0, factory.FakeIntelligence.ExecutePromptCallCount);
    }

    [SkippableTheory]
    [InlineData(
        ErrorCodes.ClientTools.ModelUnsupported,
        "The selected model does not support the requested tool choice.",
        "unsupported_parameter")]
    [InlineData(
        ErrorCodes.ClientTools.ToolChoiceUnavailable,
        "The requested tool choice cannot be satisfied with the available tools.",
        "invalid_value")]
    public async Task PostChatCompletions_LateToolChoiceFailure_UsesSanitizedRequestError(
        string internalCode,
        string expectedMessage,
        string expectedOpenAiCode)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        const string canary = "CANARY_INTERNAL_PROVIDER_AND_MODEL_DETAILS";

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings => settings with
            {
                Features = settings.Features with { ClientTools = true },
                Providers =
                [
                    settings.Providers[0] with
                    {
                        Models = [new ModelEntry("mistral:latest", SupportsTools: false)],
                    },
                ],
            },
        };

        factory.FakeIntelligence.NextFailure = new Error(internalCode, canary);

        HttpClient client = factory.CreateAuthenticatedClient();

        string payload = """
            {
              "model": "mistral:latest",
              "messages": [
                { "role": "user", "content": "Use the weather tool." }
              ],
              "tools": [
                {
                  "type": "function",
                  "function": {
                    "name": "get_weather",
                    "parameters": { "type": "object" }
                  }
                }
              ],
              "tool_choice": "required"
            }
            """;

        HttpResponseMessage response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Equal(1, factory.FakeIntelligence.ExecutePromptCallCount);

        string json = await response.Content.ReadAsStringAsync();

        OpenAiErrorResponse? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.OpenAiErrorResponse);

        Assert.NotNull(body);
        Assert.Equal(expectedMessage, body!.Error.Message);
        Assert.Equal("invalid_request_error", body.Error.Type);
        Assert.Equal(expectedOpenAiCode, body!.Error.Code);
        Assert.Equal("tool_choice", body.Error.Param);
        Assert.DoesNotContain(canary, json, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task PostChatCompletions_ClientToolReplyReplay_EchoesToolResult()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings => settings with
            {
                Features = settings.Features with { ClientTools = true },
            },
        };

        HttpClient client = factory.CreateAuthenticatedClient();

        string payload = """
            {
              "model": "mistral:latest",
              "messages": [
                { "role": "user", "content": "What is the weather?" },
                {
                  "role": "assistant",
                  "content": null,
                  "tool_calls": [
                    {
                      "id": "call_replay_123",
                      "type": "function",
                      "function": { "name": "get_weather", "arguments": "{\"location\":\"NYC\"}" }
                    }
                  ]
                },
                {
                  "role": "tool",
                  "tool_call_id": "call_replay_123",
                  "content": "72 degrees and sunny"
                }
              ]
            }
            """;

        HttpResponseMessage response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        PingRequest? lastRequest = factory.FakeIntelligence.LastRequest;

        Assert.NotNull(lastRequest);
        Assert.NotNull(lastRequest.StatelessMessages);

        CoreChatMessage? toolMessage = lastRequest.StatelessMessages.FirstOrDefault(m => m.Role == "tool");

        Assert.NotNull(toolMessage);
        Assert.Equal("call_replay_123", toolMessage.ToolCallId);
        Assert.Equal("72 degrees and sunny", toolMessage.Content);
    }
}
