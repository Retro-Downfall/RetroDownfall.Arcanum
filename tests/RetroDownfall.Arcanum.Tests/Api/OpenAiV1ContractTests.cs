using Microsoft.AspNetCore.Http;
using RetroDownfall.Arcanum.Api;
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
    public void ProviderResolver_UnknownModel_IsNotConfigured()
    {

        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "Local",
                    Type = AiProviderKind.Ollama,
                    Endpoint = "http://127.0.0.1:11434",
                    Models = ["mistral:latest"],
                },
            ],
        };

        bool resolved = ProviderResolver.TryResolveProviderForModel(settings, "gpt-4o-mini", out _, out _);

        Assert.False(resolved);

    }

}
