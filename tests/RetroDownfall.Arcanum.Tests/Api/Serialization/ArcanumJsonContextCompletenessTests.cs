using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
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
    [InlineData(typeof(PromptTurnResult))]
    [InlineData(typeof(PingRequest))]
    [InlineData(typeof(ReasoningRequestOptions))]
    [InlineData(typeof(ReasoningEffortLevel))]
    [InlineData(typeof(ReasoningOutputMode))]
    [InlineData(typeof(ReasoningContentSegment))]
    [InlineData(typeof(List<ReasoningContentSegment>))]
    [InlineData(typeof(ReasoningCapabilities))]
    [InlineData(typeof(ReasoningControlSupport))]
    [InlineData(typeof(ReasoningWireDialect))]
    [InlineData(typeof(PromptCachingProfile))]
    [InlineData(typeof(PromptCachingControlMode))]
    [InlineData(typeof(PromptCachingWireDialect))]
    [InlineData(typeof(PromptCacheRetentionPolicy))]
    [InlineData(typeof(ArcanumSettings))]
    [InlineData(typeof(WorkspaceCheckProfileSettings))]
    [InlineData(typeof(WorkspaceCheckCapabilityDto))]
    [InlineData(typeof(ApiResponse<ArcanumSettings>))]
    [InlineData(typeof(OpenAiReasoningEffort))]
    [InlineData(typeof(SubmitHumanResponseRequest))]
    [InlineData(typeof(Error))]
    public void TypeInfo_RegisteredForType(Type type)
    {

        JsonTypeInfo? typeInfo = ArcanumJsonContext.Default.GetTypeInfo(type);

        Assert.NotNull(typeInfo);

    }

    [Fact]
    public void TypeInfo_NotRegisteredForRawResultTypes()
    {

        // Result<T> marks Value with [JsonIgnore] (see the "never serialize Value directly" note on
        // Result.cs), so an endpoint that mistakenly hands Results.Ok a raw Result<T> serializes an
        // envelope with the payload dropped and a hollow Error.None — a silent wrong answer. With no
        // registration the resolver chain throws NotSupportedException instead, and the published AOT
        // binary sheds the dead JsonTypeInfo classes.
        string[] registeredResultProperties = typeof(ArcanumJsonContext)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(static property =>
                property.PropertyType.IsGenericType
                && property.PropertyType.GetGenericTypeDefinition() == typeof(JsonTypeInfo<>)
                && property.PropertyType.GetGenericArguments()[0] is { IsGenericType: true } payload
                && payload.GetGenericTypeDefinition() == typeof(Result<>))
            .Select(static property => property.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(registeredResultProperties);

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

    [Fact]
    public void RoundTrip_ReasoningEvent_UsesTypedClientSafePayload()
    {
        IntelligenceEvent original = new(
            IntelligenceEventType.Reasoning,
            "safe summary",
            Reasoning: new ReasoningContentSegment(
                "safe summary",
                ReasoningOutputMode.Summary));

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            original,
            ArcanumJsonContext.Default.IntelligenceEvent);
        string json = System.Text.Encoding.UTF8.GetString(bytes);
        IntelligenceEvent? result = JsonSerializer.Deserialize(
            bytes,
            ArcanumJsonContext.Default.IntelligenceEvent);

        Assert.NotNull(result);
        Assert.Equal(IntelligenceEventType.Reasoning, result.Type);
        Assert.Equal(original.Reasoning, result.Reasoning);
        Assert.Contains("\"type\":\"reasoning\"", json, StringComparison.Ordinal);
        Assert.Contains("\"reasoning\":", json, StringComparison.Ordinal);
        Assert.DoesNotContain("protected", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PromptResponseDto_HasOptionalTypedReasoningSegments()
    {
        Assert.Equal(
            typeof(IReadOnlyList<ReasoningContentSegment>),
            typeof(PromptResponseDto).GetProperty("Reasoning")?.PropertyType);
    }

    [Fact]
    public void Workspace_arsenal_round_trips_workspace_check_capability_reason()
    {

        WorkspaceArsenalDto original = new(
            [],
            [],
            [],
            [],
            new WorkspaceCheckCapabilityDto(
                false,
                "Linux mandatory jail unavailable."));

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            original,
            ArcanumJsonContext.Default.WorkspaceArsenalDto);
        WorkspaceArsenalDto? result = JsonSerializer.Deserialize(
            bytes,
            ArcanumJsonContext.Default.WorkspaceArsenalDto);

        Assert.NotNull(result);
        Assert.False(result.WorkspaceCheck!.Available);
        Assert.Contains(
            "mandatory jail",
            result.WorkspaceCheck.Reason,
            StringComparison.Ordinal);
    }

}
