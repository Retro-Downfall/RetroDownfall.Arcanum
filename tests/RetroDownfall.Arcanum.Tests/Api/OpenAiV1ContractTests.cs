using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.Api;

public sealed class OpenAiV1ContractTests
{

    [Fact]
    public void HubModelFailure_MapsToModelNotFoundAnd404()
    {

        Assert.Equal("model_not_found", OpenAiV1Endpoints.MapPublicOpenAiErrorCodeForTests("Hub.Model"));

        Assert.Equal(StatusCodes.Status404NotFound, OpenAiV1Endpoints.ResolveOpenAiInferenceFailureStatusCodeForTests("Hub.Model"));

    }

    [Fact]
    public void HubToolLoopFailure_ResolvesTo503()
    {

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, OpenAiV1Endpoints.ResolveOpenAiInferenceFailureStatusCodeForTests("Hub.ToolLoop"));

    }

    [Fact]
    public void ValidationInvalidPromptFailure_ResolvesTo400()
    {

        Assert.Equal(StatusCodes.Status400BadRequest, OpenAiV1Endpoints.ResolveOpenAiInferenceFailureStatusCodeForTests("Validation.InvalidPrompt"));

    }

    [Theory]
    [InlineData("length", "length")]
    [InlineData("stop", "stop")]
    [InlineData(null, "stop")]
    public void ResolveFinishReason_UsesHubValueOrStop(string? hubReason, string expected)
    {

        Assert.Equal(expected, OpenAiV1Endpoints.ResolveFinishReasonForTests(hubReason));

    }

    [Fact]
    public void MapChatFinishReasonToOpenAi_NullDefaultsStop()
    {

        Assert.Equal("stop", WizardIntelligenceProvider.MapChatFinishReasonToOpenAi(null));

    }

    [Theory]
    [InlineData("stop")]
    [InlineData("length")]
    [InlineData("tool_calls")]
    [InlineData("content_filter")]
    public void MapChatFinishReasonToOpenAi_MapsKnownValues(string expected)
    {

        ChatFinishReason reason = expected switch
        {
            "stop" => ChatFinishReason.Stop,
            "length" => ChatFinishReason.Length,
            "tool_calls" => ChatFinishReason.ToolCalls,
            "content_filter" => ChatFinishReason.ContentFilter,
            _ => throw new ArgumentOutOfRangeException(nameof(expected)),
        };

        Assert.Equal(expected, WizardIntelligenceProvider.MapChatFinishReasonToOpenAi(reason));

    }

    [Fact]
    public void ProviderResolver_UnknownModel_IsNotConfigured()
    {

        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "Local",
                    Type = AiProviderKind.OpenAICompatible,
                    Endpoint = "http://127.0.0.1:11434/v1",
                    Models = ["mistral:latest"],
                },
            ],
        };

        bool resolved = ProviderResolver.TryResolveProviderForModel(settings, "gpt-4o-mini", out _, out _);

        Assert.False(resolved);

    }

}
