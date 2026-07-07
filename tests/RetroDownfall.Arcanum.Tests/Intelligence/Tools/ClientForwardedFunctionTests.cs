using System.Text.Json;

using Microsoft.Extensions.AI;

using RetroDownfall.Arcanum.Api.Intelligence.Tools;

using RetroDownfall.Arcanum.Core.Intelligence.OpenAi;

namespace RetroDownfall.Arcanum.Tests.Intelligence.Tools;

public sealed class ClientForwardedFunctionTests
{

    [Fact]

    public void AdditionalProperties_StrictTrue_PromotesStrictFlag()
    {

        OpenAiFunctionDefinition definition = new(

            Name: "get_weather",

            Description: "Get weather",

            Parameters: JsonDocument.Parse("""{"type": "object"}""").RootElement.Clone(),

            Strict: true);

        ClientForwardedFunction function = new(definition);

        Assert.True(function.AdditionalProperties.ContainsKey("strict"));

        Assert.True((bool)function.AdditionalProperties["strict"]!);

    }

    [Fact]

    public void AdditionalProperties_StrictNull_DoesNotPromoteStrictFlag()
    {

        OpenAiFunctionDefinition definition = new(

            Name: "get_weather",

            Description: "Get weather",

            Parameters: null,

            Strict: null);

        ClientForwardedFunction function = new(definition);

        Assert.False(function.AdditionalProperties.ContainsKey("strict"));

    }

    [Fact]

    public void AdditionalProperties_StrictFalse_DoesNotPromoteStrictFlag()
    {

        OpenAiFunctionDefinition definition = new(

            Name: "get_weather",

            Description: "Get weather",

            Parameters: null,

            Strict: false);

        ClientForwardedFunction function = new(definition);

        Assert.False(function.AdditionalProperties.ContainsKey("strict"));

    }

    [Fact]

    public void JsonSchema_NullParameters_ReturnsEmptyObjectSchema()
    {

        OpenAiFunctionDefinition definition = new("test", "desc", null, null);

        ClientForwardedFunction function = new(definition);

        Assert.Equal(JsonValueKind.Object, function.JsonSchema.ValueKind);

    }

    [Fact]

    public async Task InvokeCoreAsync_ReturnsForwardedMessage()
    {

        OpenAiFunctionDefinition definition = new("test_tool", "desc", null, null);

        ClientForwardedFunction function = new(definition);

        object? result = await function.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

        Assert.NotNull(result);

        Assert.Contains("Client tool 'test_tool'", result.ToString()!);

    }

}
