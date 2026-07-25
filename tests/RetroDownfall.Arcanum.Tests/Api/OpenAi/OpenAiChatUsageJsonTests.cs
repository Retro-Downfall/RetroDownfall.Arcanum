using System.Text.Json;
using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence.Models;

namespace RetroDownfall.Arcanum.Tests.Api.OpenAi;

public sealed class OpenAiChatUsageJsonTests
{
    private const string NativeUsageJson =
        """{"prompt_tokens":10,"completion_tokens":8,"total_tokens":18,"cached_tokens":2,"reasoning_tokens":3}""";

    [Fact]
    public void ChatCompletionUsage_NativeJson_RoundTripsAdditiveReasoningTokens()
    {
        ChatCompletionUsage? usage = JsonSerializer.Deserialize(
            NativeUsageJson,
            ArcanumJsonContext.Default.ChatCompletionUsage);

        Assert.NotNull(usage);

        string json = JsonSerializer.Serialize(
            usage,
            ArcanumJsonContext.Default.ChatCompletionUsage);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        Assert.Equal(8, root.GetProperty("completion_tokens").GetInt32());
        Assert.Equal(18, root.GetProperty("total_tokens").GetInt32());
        Assert.Equal(3, root.GetProperty("reasoning_tokens").GetInt32());
    }

    [Fact]
    public void OpenAiChatResponse_ProjectsReasoningUnderCompletionTokenDetails()
    {
        ChatCompletionUsage usage = JsonSerializer.Deserialize(
            NativeUsageJson,
            ArcanumJsonContext.Default.ChatCompletionUsage)!;
        OpenAiChatResponse response = new(
            Id: "chatcmpl-test",
            ObjectKind: "chat.completion",
            Created: 1,
            Model: "reasoner",
            Choices: [],
            Usage: usage);

        string json = JsonSerializer.Serialize(
            response,
            ArcanumJsonContext.Default.OpenAiChatResponse);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement projectedUsage = document.RootElement.GetProperty("usage");

        Assert.Equal(8, projectedUsage.GetProperty("completion_tokens").GetInt32());
        Assert.Equal(18, projectedUsage.GetProperty("total_tokens").GetInt32());
        Assert.False(projectedUsage.TryGetProperty("reasoning_tokens", out _));
        Assert.Equal(
            3,
            projectedUsage
                .GetProperty("completion_tokens_details")
                .GetProperty("reasoning_tokens")
                .GetInt32());

        OpenAiChatResponse? roundTripped = JsonSerializer.Deserialize(
            json,
            ArcanumJsonContext.Default.OpenAiChatResponse);
        Assert.Equal(3, roundTripped?.Usage?.ReasoningTokens);
    }

    [Fact]
    public void OpenAiChatChunk_ProjectsReasoningWithoutChangingCompletionOrTotal()
    {
        ChatCompletionUsage usage = JsonSerializer.Deserialize(
            NativeUsageJson,
            ArcanumJsonContext.Default.ChatCompletionUsage)!;
        OpenAiChatChunk chunk = new(
            Id: "chatcmpl-test",
            ObjectKind: "chat.completion.chunk",
            Created: 1,
            Model: "reasoner",
            Choices: [],
            Usage: usage);

        string json = JsonSerializer.Serialize(
            chunk,
            ArcanumJsonContext.Default.OpenAiChatChunk);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement projectedUsage = document.RootElement.GetProperty("usage");

        Assert.Equal(8, projectedUsage.GetProperty("completion_tokens").GetInt32());
        Assert.Equal(18, projectedUsage.GetProperty("total_tokens").GetInt32());
        Assert.Equal(
            3,
            projectedUsage
                .GetProperty("completion_tokens_details")
                .GetProperty("reasoning_tokens")
                .GetInt32());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"invalid\"")]
    [InlineData("[]")]
    public void OpenAiUsage_NonObjectDetails_PreserveTopLevelCachedTokens(string details)
    {
        string json =
            $$"""{"prompt_tokens":10,"completion_tokens":8,"total_tokens":18,"cached_tokens":2,"prompt_tokens_details":{{details}},"completion_tokens_details":{{details}}}""";

        ChatCompletionUsage? usage = JsonSerializer.Deserialize<ChatCompletionUsage>(
            json,
            new JsonSerializerOptions
            {
                Converters = { new OpenAiChatUsageJsonConverter() },
            });

        Assert.NotNull(usage);
        Assert.Equal(2, usage.CachedTokens);
        Assert.Equal(0, usage.ReasoningTokens);
    }

    [Fact]
    public void OpenAiUsage_TrueRoundTripPreservesNestedCachedAndReasoningTokens()
    {
        const string json =
            """{"prompt_tokens":10,"completion_tokens":8,"total_tokens":18,"cached_tokens":2,"prompt_tokens_details":{"cached_tokens":4},"completion_tokens_details":{"reasoning_tokens":3}}""";
        JsonSerializerOptions options = new()
        {
            Converters = { new OpenAiChatUsageJsonConverter() },
        };

        ChatCompletionUsage usage = JsonSerializer.Deserialize<ChatCompletionUsage>(json, options)!;
        string roundTripJson = JsonSerializer.Serialize(usage, options);
        ChatCompletionUsage roundTripped =
            JsonSerializer.Deserialize<ChatCompletionUsage>(roundTripJson, options)!;

        Assert.Equal(4, usage.CachedTokens);
        Assert.Equal(3, usage.ReasoningTokens);
        Assert.Equal(usage, roundTripped);

        using JsonDocument document = JsonDocument.Parse(roundTripJson);
        Assert.Equal(
            4,
            document.RootElement
                .GetProperty("prompt_tokens_details")
                .GetProperty("cached_tokens")
                .GetInt32());
    }
}
