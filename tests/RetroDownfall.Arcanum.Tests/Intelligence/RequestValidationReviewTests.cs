using System.Text.Json;

using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class RequestValidationReviewTests
{
    [Theory]
    [InlineData("definitions")]
    [InlineData("empty-definitions")]
    [InlineData("choice")]
    public void Native_preflight_refuses_client_tool_fields_without_forwarding(string field)
    {
        using JsonDocument choice = JsonDocument.Parse("\"auto\"");

        PingRequest request = new(
            Prompt: "hello",
            ClientTools: field == "definitions" ? [new("function", new("lookup"))]
                : field == "empty-definitions" ? [] : null,
            ClientToolChoice: field == "choice" ? choice.RootElement.Clone() : null);

        Result result = PingRequestPreflightValidator.Validate(request, Settings());

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.ClientTools.InvalidSchema, result.Error.Code);

        Assert.Contains("forwardClientTools", result.Error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("null", "clientTools[0]")]
    [InlineData("type", "clientTools[0].type")]
    [InlineData("name", "clientTools[0].function.name")]
    [InlineData("parameters", "clientTools[0].function.parameters")]
    public void Native_preflight_identifies_the_native_tool_field(string malformed, string field)
    {
        using JsonDocument parameters = JsonDocument.Parse("[]");

        OpenAiToolDefinition? tool = malformed switch
        {
            "null" => null,
            "type" => new("unsupported", new("lookup")),
            "name" => new("function", new("")),
            _ => new("function", new("lookup", Parameters: parameters.RootElement.Clone())),
        };

        PingRequest request = new(Prompt: "hello", ForwardClientTools: true, ClientTools: [tool!]);

        Result result = PingRequestPreflightValidator.Validate(request, Settings());

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.ClientTools.InvalidSchema, result.Error.Code);

        Assert.Contains(field, result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Omitted_native_tools_and_valid_forwarded_tools_remain_valid()
    {
        Assert.True(PingRequestPreflightValidator.Validate(new(Prompt: "hello"), Settings()).IsSuccess);

        Assert.True(PingRequestPreflightValidator.Validate(
            new(Prompt: "hello", ForwardClientTools: true, ClientTools: [new("function", new("lookup"))]),
            Settings()).IsSuccess);

        Result openAi = ClientToolCapabilityValidator.ValidateToolDefinitions([new("function", new(""))], 10, 8);

        Assert.Contains("tools[0].function.name", openAi.Error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("private-image-canary.png")]
    [InlineData("file:///private-image-canary.png")]
    [InlineData("ftp://private-image-canary.invalid/a.png")]
    [InlineData("data:image/png,private-image-canary")]
    [InlineData("data:image/png;base64")]
    [InlineData("data:image/png;other,AQID")]
    public void Image_preflight_refuses_unmaterializable_image_with_exact_index(string? imageUrl)
    {
        PingRequest request = new(
            Prompt: "look",
            StatelessMessages:
            [
                new("user", "first"),
                new("user", "", ContentParts:
                [
                    new("text", "look", null, null),
                    new("image_url", null, imageUrl, null),
                ]),
            ]);

        Result result = PingRequestPreflightValidator.Validate(request, Settings());

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Scrying.InvalidImageData, result.Error.Code);

        Assert.Contains("messages[1].content[1].image_url.url", result.Error.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("private-image-canary", result.Error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(401, "credential")]
    [InlineData(403, "permission")]
    [InlineData(400, "request")]
    [InlineData(404, "model")]
    [InlineData(429, "rate limit")]
    [InlineData(500, "provider")]
    [InlineData(503, "retry")]
    public void Provider_failure_guidance_matches_the_response_category(int status, string remedy)
    {
        string message = InferenceProviderFailureMessage.Build("test-provider", status);

        Assert.Contains($"HTTP {status}", message, StringComparison.Ordinal);

        Assert.Contains(remedy, message, StringComparison.OrdinalIgnoreCase);

        if (status == 429 || status >= 500)
        {
            Assert.DoesNotContain("API key", message, StringComparison.OrdinalIgnoreCase);

            Assert.Contains("retry", message, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static ArcanumSettings Settings() => new()
    {
        Features = new()
        {
            ClientTools = true,
            Scrying = true,
        },
    };
}
