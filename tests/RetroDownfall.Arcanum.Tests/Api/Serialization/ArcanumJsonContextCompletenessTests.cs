using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Api.Tower;
using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Wards;
using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Tests.Api.Serialization;

public sealed class ArcanumJsonContextCompletenessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Generation_provenance_roundtrips_exact_and_Bloom_payloads(bool overflow)
    {
        Guid[] identities = Enumerable.Range(1, overflow ? CovenantLimits.MaxExactGenerationIds + 1 : 2)
            .Select(static index => new Guid(index, 0, 0, new byte[8]))
            .ToArray();

        GenerationProvenance value = GenerationProvenance.Create(identities);

        string json = JsonSerializer.Serialize(value, ArcanumJsonContext.Default.GenerationProvenance);

        GenerationProvenance restored = Assert.IsType<GenerationProvenance>(
            JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.GenerationProvenance));

        Assert.Equal(overflow ? GenerationProvenanceMode.BloomOverflow : GenerationProvenanceMode.Exact, restored.Mode);

        Assert.Equal(value, restored);

        Assert.Equal(value.ExactGenerationIds.ToArray(), restored.ExactGenerationIds.ToArray());

        Assert.Equal(value.BloomBits.ToArray(), restored.BloomBits.ToArray());
    }

    [Theory]
    [InlineData(typeof(LexiconShowRequest))]
    [InlineData(typeof(LexiconCorrectRequest))]
    [InlineData(typeof(LexiconRetireRequest))]
    [InlineData(typeof(LexiconReinstateRequest))]
    [InlineData(typeof(LexiconPinRequest))]
    [InlineData(typeof(LexiconUnpinRequest))]
    [InlineData(typeof(LexiconCurationScope))]
    [InlineData(typeof(LexiconScopeKind))]
    [InlineData(typeof(LexiconRetrievalEligibility))]
    [InlineData(typeof(LexiconCurationOutcomeKind))]
    [InlineData(typeof(LexiconEntryLifecycle))]
    [InlineData(typeof(LexiconReplacementContent))]
    [InlineData(typeof(LexiconCurationAnnalHead))]
    [InlineData(typeof(LexiconCurationSensitivityLabel))]
    [InlineData(typeof(LexiconCurationTarget))]
    [InlineData(typeof(LexiconCurationResult))]
    [InlineData(typeof(LexiconEntryDetail))]
    [InlineData(typeof(LexiconEntryDto))]
    [InlineData(typeof(LexiconEntryDto[]))]
    [InlineData(typeof(LexiconAnnalFactProvenance))]
    [InlineData(typeof(LexiconAnnalFactProvenance[]))]
    [InlineData(typeof(LexiconFactProvenance))]
    [InlineData(typeof(LexiconFactProvenance[]))]
    [InlineData(typeof(AttachmentMemoryProvenance))]
    [InlineData(typeof(AttachmentSourceAvailability))]
    [InlineData(typeof(AnnalClaimVersion))]
    [InlineData(typeof(AnnalClaimVersion[]))]
    [InlineData(typeof(AnnalOperation))]
    [InlineData(typeof(AnnalOrigin))]
    [InlineData(typeof(AnnalContentHashFormat))]
    [InlineData(typeof(ContentSensitivity))]
    [InlineData(typeof(SagaMemoryScopeKind))]
    [InlineData(typeof(GenerationProvenance))]
    [InlineData(typeof(GenerationProvenanceMode))]
    [InlineData(typeof(DataRetentionLexiconCurationInventory))]
    [InlineData(typeof(ApiResponse<LexiconEntryDetail>))]
    [InlineData(typeof(ApiResponse<LexiconCurationResult>))]
    public void Lexicon_wire_types_have_explicit_source_generation_registrations(Type type)
    {
        Assert.Contains(typeof(ArcanumJsonContext).CustomAttributes, attribute => attribute.AttributeType == typeof(JsonSerializableAttribute)
            && attribute.ConstructorArguments[0].Value is Type registered && registered == type);

        Assert.NotNull(ArcanumJsonContext.Default.GetTypeInfo(type));
    }

    [Theory]
    [InlineData(typeof(MemoryLocalErasureOutcome))]
    [InlineData(typeof(MemoryErasureScrubPendingReason))]
    [InlineData(typeof(MemoryErasureWalCheckpointAttempt))]
    [InlineData(typeof(MemoryExternalChannel))]
    [InlineData(typeof(MemoryExternalEvidence))]
    [InlineData(typeof(MemoryExternalRevocation))]
    [InlineData(typeof(MemoryRetainedLocalCopy))]
    [InlineData(typeof(MemoryErasureNote))]
    [InlineData(typeof(MemoryErasureReleaseOutcome))]
    [InlineData(typeof(MemoryErasureKeyStatus))]
    [InlineData(typeof(MemoryExternalExposureChannelDto))]
    [InlineData(typeof(MemoryErasureExternalExposureDto))]
    [InlineData(typeof(LexiconErasurePlanFacts))]
    [InlineData(typeof(CovenantErasurePlanFacts))]
    [InlineData(typeof(MemoryErasurePlanDto))]
    [InlineData(typeof(MemoryErasurePreflightDto))]
    [InlineData(typeof(MemoryErasureLocalResultDto))]
    [InlineData(typeof(MemoryErasureResultDto))]
    [InlineData(typeof(MemoryErasureReleaseResultDto))]
    [InlineData(typeof(MemoryErasureStoreCountsDto))]
    [InlineData(typeof(MemoryErasureStatusDto))]
    [InlineData(typeof(MemoryErasureScrubResultDto))]
    [InlineData(typeof(MemoryErasureKeyResetPreflightDto))]
    [InlineData(typeof(MemoryErasureKeyResetRequest))]
    [InlineData(typeof(MemoryErasureKeyResetResultDto))]
    [InlineData(typeof(SagaErasePrepareRequest))]
    [InlineData(typeof(SagaEraseRequest))]
    [InlineData(typeof(SagaErasureReleaseRequest))]
    [InlineData(typeof(LexiconErasePrepareRequest))]
    [InlineData(typeof(LexiconEraseRequest))]
    [InlineData(typeof(LexiconErasureReleaseRequest))]
    [InlineData(typeof(MemoryErasureScrubPendingReason[]))]
    [InlineData(typeof(MemoryExternalExposureChannelDto[]))]
    [InlineData(typeof(MemoryRetainedLocalCopy[]))]
    [InlineData(typeof(MemoryErasureNote[]))]
    [InlineData(typeof(MemoryErasureStoreCountsDto[]))]
    [InlineData(typeof(CovenantEraseHeadExpectation))]
    [InlineData(typeof(CovenantErasePrepareRequest))]
    [InlineData(typeof(CovenantEraseRequest))]
    [InlineData(typeof(ApiResponse<MemoryErasurePreflightDto>))]
    [InlineData(typeof(ApiResponse<MemoryErasureResultDto>))]
    [InlineData(typeof(ApiResponse<MemoryErasureReleaseResultDto>))]
    [InlineData(typeof(ApiResponse<MemoryErasureStatusDto>))]
    [InlineData(typeof(ApiResponse<MemoryErasureScrubResultDto>))]
    [InlineData(typeof(ApiResponse<MemoryErasureKeyResetPreflightDto>))]
    [InlineData(typeof(ApiResponse<MemoryErasureKeyResetResultDto>))]
    public void Memory_erasure_wire_types_have_explicit_source_generation_registrations(Type type)
    {
        Assert.Contains(typeof(ArcanumJsonContext).CustomAttributes, attribute => attribute.AttributeType == typeof(JsonSerializableAttribute)
            && attribute.ConstructorArguments[0].Value is Type registered && registered == type);

        Assert.NotNull(ArcanumJsonContext.Default.GetTypeInfo(type));
    }

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
    [InlineData(typeof(LexiconCurationScope))]
    [InlineData(typeof(LexiconScopeKind))]
    [InlineData(typeof(LexiconRetrievalEligibility))]
    [InlineData(typeof(LexiconCurationOutcomeKind))]
    [InlineData(typeof(LexiconEntryLifecycle))]
    [InlineData(typeof(LexiconReplacementContent))]
    [InlineData(typeof(LexiconCurationAnnalHead))]
    [InlineData(typeof(LexiconCurationSensitivityLabel))]
    [InlineData(typeof(LexiconCurationTarget))]
    [InlineData(typeof(LexiconCurationResult))]
    [InlineData(typeof(LexiconAnnalFactProvenance))]
    [InlineData(typeof(LexiconEntryDetail))]
    [InlineData(typeof(ApiResponse<LexiconEntryDetail>))]
    [InlineData(typeof(ApiResponse<LexiconCurationResult>))]
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
    public void Ungated_ward_origin_round_trips_through_stream_and_api_contracts()
    {

        WardResolutionOrigin origin = WardResolutionOrigin.Ungated;

        IntelligenceEvent streamFrame = new(
            IntelligenceEventType.WardResolved,
            "read_file_chunk",
            WardId: "ward-ungated",
            WardToolName: "read_file_chunk",
            WardAllowed: true,
            WardOrigin: origin);

        byte[] streamBytes = JsonSerializer.SerializeToUtf8Bytes(
            streamFrame,
            ArcanumJsonContext.Default.IntelligenceEvent);

        IntelligenceEvent? roundTrippedFrame = JsonSerializer.Deserialize(
            streamBytes,
            ArcanumJsonContext.Default.IntelligenceEvent);

        Assert.NotNull(roundTrippedFrame);

        Assert.Equal(origin, roundTrippedFrame.WardOrigin);

        Assert.Contains(
            "\"origin\":\"ungated\"",
            System.Text.Encoding.UTF8.GetString(streamBytes),
            StringComparison.Ordinal);

        WardResolutionDto apiResolution = new(
            "ward-ungated",
            true,
            null,
            DateTimeOffset.UnixEpoch,
            origin);

        byte[] apiBytes = JsonSerializer.SerializeToUtf8Bytes(
            apiResolution,
            ArcanumJsonContext.Default.WardResolutionDto);

        WardResolutionDto? roundTrippedResolution = JsonSerializer.Deserialize(
            apiBytes,
            ArcanumJsonContext.Default.WardResolutionDto);

        Assert.NotNull(roundTrippedResolution);

        Assert.Equal(origin, roundTrippedResolution.Origin);

        Assert.Contains(
            "\"origin\":\"ungated\"",
            System.Text.Encoding.UTF8.GetString(apiBytes),
            StringComparison.Ordinal);

        Assert.Equal("ungated", WardResolutionOrigins.ToMetricLabel(origin));

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
