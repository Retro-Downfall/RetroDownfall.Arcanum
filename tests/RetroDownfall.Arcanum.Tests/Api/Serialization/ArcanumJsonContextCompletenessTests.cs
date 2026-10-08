using System.Reflection;
using System.Reflection.Emit;
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
    [InlineData(typeof(CovenantErasureReleaseRequest))]
    [InlineData(typeof(ApiResponse<MemoryErasurePreflightDto>))]
    [InlineData(typeof(ApiResponse<MemoryErasureResultDto>))]
    [InlineData(typeof(ApiResponse<MemoryErasureReleaseResultDto>))]
    [InlineData(typeof(ApiResponse<MemoryErasureStatusDto>))]
    [InlineData(typeof(ApiResponse<MemoryErasureScrubResultDto>))]
    [InlineData(typeof(ApiResponse<MemoryErasureKeyResetPreflightDto>))]
    [InlineData(typeof(ApiResponse<MemoryErasureKeyResetResultDto>))]
    [InlineData(typeof(DataRetentionMemoryErasureInventory))]
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

    /// <summary>
    /// Every <c>ApiResponse&lt;T&gt;</c> the Api assembly can build has a source-generated type info.
    /// </summary>
    /// <remarks>
    /// A route hands its payload to <c>Results.Ok</c> or <c>Results.Json</c> as an <c>IResult</c>, so the
    /// handler's declared return type never names the payload and the endpoint table cannot be walked for
    /// it. The envelope is built in the handler's own body, though, so every instantiation the assembly can
    /// reach appears in its compiled code: as a call target's declaring type, a call's return or generic
    /// argument, a local, a field. This reads all of those, so a registration removed from
    /// <c>ArcanumJsonContext</c> fails here for routes no other test happens to hit, instead of
    /// surfacing as a runtime serialization failure in the published binary, where reflection is off.
    /// </remarks>
    [Fact]
    public void Every_route_response_type_is_source_generated()
    {
        Type[] envelopes = ApiResponseInstantiationsIn(typeof(ArcanumJsonContext).Assembly);

        // A scan that silently stops finding envelopes would pass for the wrong reason, so it has to see
        // ones the routes are known to build before its silence about the rest means anything.
        Assert.Contains(typeof(ApiResponse<PromptResponseDto>), envelopes);

        Assert.Contains(typeof(ApiResponse<bool>), envelopes);

        string[] unregistered =
        [
            .. envelopes
                .Where(static envelope => ArcanumJsonContext.Default.GetTypeInfo(envelope) is null)
                .Select(static envelope => envelope.ToString())
                .Order(StringComparer.Ordinal),
        ];

        Assert.True(
            unregistered.Length == 0,
            "ApiResponse<T> instantiations the Api assembly builds without a [JsonSerializable] entry on "
            + $"ArcanumJsonContext: {string.Join(", ", unregistered)}");
    }

    private static Type[] ApiResponseInstantiationsIn(Assembly assembly)
    {
        HashSet<Type> found = [];

        HashSet<Type> visited = [];

        const BindingFlags AllMembers =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (Type type in assembly.GetTypes())
        {
            Visit(type, found, visited);

            foreach (FieldInfo field in type.GetFields(AllMembers))
            {
                Visit(field.FieldType, found, visited);
            }

            foreach (MethodBase method in type.GetMethods(AllMembers).Cast<MethodBase>().Concat(type.GetConstructors(AllMembers)))
            {
                if (method is MethodInfo info)
                {
                    Visit(info.ReturnType, found, visited);
                }

                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    Visit(parameter.ParameterType, found, visited);
                }

                MethodBody? body = method.GetMethodBody();

                if (body is null)
                {
                    continue;
                }

                foreach (LocalVariableInfo local in body.LocalVariables)
                {
                    Visit(local.LocalType, found, visited);
                }

                Type[] typeArguments = type.IsGenericTypeDefinition ? type.GetGenericArguments() : [];

                Type[] methodArguments = method.IsGenericMethodDefinition ? method.GetGenericArguments() : [];

                foreach (int token in MetadataTokensIn(body.GetILAsByteArray() ?? []))
                {
                    MemberInfo? member;

                    try
                    {
                        member = assembly.ManifestModule.ResolveMember(token, typeArguments, methodArguments);
                    }
                    catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
                    {
                        continue;
                    }

                    switch (member)
                    {
                        case Type resolved:

                            Visit(resolved, found, visited);

                            break;

                        case FieldInfo field:

                            Visit(field.DeclaringType, found, visited);

                            Visit(field.FieldType, found, visited);

                            break;

                        case MethodBase called:

                            Visit(called.DeclaringType, found, visited);

                            if (called is MethodInfo calledInfo)
                            {
                                Visit(calledInfo.ReturnType, found, visited);
                            }

                            foreach (ParameterInfo parameter in called.GetParameters())
                            {
                                Visit(parameter.ParameterType, found, visited);
                            }

                            if (called.IsGenericMethod && !called.IsGenericMethodDefinition)
                            {
                                foreach (Type argument in called.GetGenericArguments())
                                {
                                    Visit(argument, found, visited);
                                }
                            }

                            break;
                    }
                }
            }
        }

        return [.. found];
    }

    private static void Visit(Type? type, HashSet<Type> found, HashSet<Type> visited)
    {
        if (type is null || !visited.Add(type))
        {
            return;
        }

        if (type.HasElementType)
        {
            Visit(type.GetElementType(), found, visited);

            return;
        }

        if (!type.IsGenericType || type.ContainsGenericParameters)
        {
            return;
        }

        if (type.GetGenericTypeDefinition() == typeof(ApiResponse<>))
        {
            _ = found.Add(type);
        }

        foreach (Type argument in type.GetGenericArguments())
        {
            Visit(argument, found, visited);
        }
    }

    private static readonly Dictionary<ushort, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(static field => field.FieldType == typeof(OpCode))
        .Select(static field => (OpCode)field.GetValue(null)!)
        .ToDictionary(static opCode => (ushort)opCode.Value);

    /// <summary>Every member, type, or token operand a method body's IL names.</summary>
    private static IEnumerable<int> MetadataTokensIn(byte[] il)
    {
        int offset = 0;

        while (offset < il.Length)
        {
            ushort value = il[offset++];

            if (value == 0xFE)
            {
                value = (ushort)(0xFE00 | il[offset++]);
            }

            OpCode opCode = OpCodesByValue[value];

            switch (opCode.OperandType)
            {
                case OperandType.InlineNone:

                    break;

                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:

                    offset += 1;

                    break;

                case OperandType.InlineVar:

                    offset += 2;

                    break;

                case OperandType.InlineI8:
                case OperandType.InlineR:

                    offset += 8;

                    break;

                case OperandType.InlineSwitch:

                    int count = BitConverter.ToInt32(il, offset);

                    offset += 4 + (4 * count);

                    break;

                case OperandType.InlineMethod:
                case OperandType.InlineField:
                case OperandType.InlineType:
                case OperandType.InlineTok:

                    yield return BitConverter.ToInt32(il, offset);

                    offset += 4;

                    break;

                default:

                    offset += 4;

                    break;
            }
        }
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

    /// <summary>
    /// The Ward frame's wire names come from the member names alone now that they carry no renames, and
    /// they are the names clients have always read.
    /// </summary>
    [Fact]
    public void Ward_frames_keep_their_wire_names_without_renames()
    {
        using JsonDocument warded = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(
            new IntelligenceEvent(
                IntelligenceEventType.Warded,
                "write_file",
                WardId: "ward-1",
                ToolName: "write_file",
                Arguments: JsonDocument.Parse("""{"path":"a.txt"}""").RootElement.Clone(),
                Timestamp: DateTimeOffset.UnixEpoch,
                Origin: WardResolutionOrigin.Ungated),
            ArcanumJsonContext.Default.IntelligenceEvent));

        using JsonDocument resolved = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(
            new IntelligenceEvent(
                IntelligenceEventType.WardResolved,
                "write_file",
                WardId: "ward-1",
                ToolName: "write_file",
                Allowed: false,
                Reason: "refused",
                Origin: WardResolutionOrigin.Human),
            ArcanumJsonContext.Default.IntelligenceEvent));

        Assert.Equal("ward-1", warded.RootElement.GetProperty("wardId").GetString());

        Assert.Equal("write_file", warded.RootElement.GetProperty("toolName").GetString());

        Assert.Equal("a.txt", warded.RootElement.GetProperty("arguments").GetProperty("path").GetString());

        Assert.Equal("ungated", warded.RootElement.GetProperty("origin").GetString());

        Assert.True(warded.RootElement.TryGetProperty("timestamp", out _));

        Assert.False(resolved.RootElement.GetProperty("allowed").GetBoolean());

        Assert.Equal("refused", resolved.RootElement.GetProperty("reason").GetString());

        string[] names = [.. warded.RootElement.EnumerateObject().Select(static property => property.Name)];

        Assert.DoesNotContain(names, static name => name.StartsWith("ward", StringComparison.Ordinal) && name != "wardId");
    }

    [Fact]
    public void Ungated_ward_origin_round_trips_through_stream_and_api_contracts()
    {
        WardResolutionOrigin origin = WardResolutionOrigin.Ungated;

        IntelligenceEvent streamFrame = new(
            IntelligenceEventType.WardResolved,
            "read_file_chunk",
            WardId: "ward-ungated",
            ToolName: "read_file_chunk",
            Allowed: true,
            Origin: origin);

        byte[] streamBytes = JsonSerializer.SerializeToUtf8Bytes(
            streamFrame,
            ArcanumJsonContext.Default.IntelligenceEvent);

        IntelligenceEvent? roundTrippedFrame = JsonSerializer.Deserialize(
            streamBytes,
            ArcanumJsonContext.Default.IntelligenceEvent);

        Assert.NotNull(roundTrippedFrame);

        Assert.Equal(origin, roundTrippedFrame.Origin);

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
