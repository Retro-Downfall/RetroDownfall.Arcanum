using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;

namespace RetroDownfall.Arcanum.Tests.Api.Serialization;

public sealed class ArcanumJsonContextCompletenessTests
{

    [Theory]
    [InlineData(typeof(ApiResponse<bool>))]
    [InlineData(typeof(ApiResponse<string>))]
    [InlineData(typeof(ApiResponse<PromptResponseDto>))]
    [InlineData(typeof(IntelligenceEvent))]
    [InlineData(typeof(PingRequest))]
    [InlineData(typeof(SubmitHumanResponseRequest))]
    [InlineData(typeof(Error))]
    public void TypeInfo_RegisteredForType(Type type)
    {

        JsonTypeInfo? typeInfo = ArcanumJsonContext.Default.GetTypeInfo(type);

        Assert.NotNull(typeInfo);

    }

    [Fact]
    public void RoundTrip_ApiResponseBool()
    {

        ApiResponse<bool> original = new(true, true, null, "trace");

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(original, ArcanumJsonContext.Default.ApiResponseBoolean);

        ApiResponse<bool>? result = JsonSerializer.Deserialize(bytes, ArcanumJsonContext.Default.ApiResponseBoolean);

        Assert.NotNull(result);

        Assert.True(result.IsSuccess);

        Assert.Equal(original.Data, result.Data);

    }

    [Fact]
    public void RoundTrip_IntelligenceEvent()
    {

        IntelligenceEvent original = new(
            IntelligenceEventType.ToolCall,
            "ask_human",
            "{\"question\":\"q\",\"promptId\":\"p\"}");

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(original, ArcanumJsonContext.Default.IntelligenceEvent);

        IntelligenceEvent? result = JsonSerializer.Deserialize(bytes, ArcanumJsonContext.Default.IntelligenceEvent);

        Assert.NotNull(result);

        Assert.Equal(original.Type, result.Type);

        Assert.Equal(original.Message, result.Message);

    }

}
