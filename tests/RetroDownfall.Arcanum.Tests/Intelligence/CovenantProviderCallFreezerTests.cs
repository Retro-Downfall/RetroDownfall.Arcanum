using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.AI;

using RetroDownfall.Arcanum.Api.Intelligence;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// Freezing one provider attempt: the envelope binds what actually goes out, or nothing goes out.
/// </summary>
public sealed class CovenantProviderCallFreezerTests
{

    [Fact]
    public void An_ordinary_transcript_freezes_into_a_stable_digest()
    {

        CovenantProviderCallDescriptor descriptor = Descriptor(
        [
            new ChatMessage(ChatRole.System, "you are a wizard"),
            new ChatMessage(ChatRole.User, "hello"),
            new ChatMessage(ChatRole.Assistant, "greetings"),
        ]);

        Result<ProviderCallEnvelope> first = CovenantProviderCallFreezer.TryFreeze(
            descriptor,
            Sensitivity(),
            new ProviderCallMaterializationSnapshot(false, []));

        Result<ProviderCallEnvelope> second = CovenantProviderCallFreezer.TryFreeze(
            descriptor,
            Sensitivity(),
            new ProviderCallMaterializationSnapshot(false, []));

        Assert.True(first.IsSuccess, first.IsFailure ? first.Error.Message : string.Empty);

        Assert.Equal(first.Value.Digest, second.Value.Digest);

    }

    [Fact]
    public void Changing_one_message_byte_changes_the_frozen_digest()
    {

        Result<ProviderCallEnvelope> original = CovenantProviderCallFreezer.TryFreeze(
            Descriptor([new ChatMessage(ChatRole.User, "hello")]),
            Sensitivity(),
            new ProviderCallMaterializationSnapshot(false, []));

        Result<ProviderCallEnvelope> altered = CovenantProviderCallFreezer.TryFreeze(
            Descriptor([new ChatMessage(ChatRole.User, "hellp")]),
            Sensitivity(),
            new ProviderCallMaterializationSnapshot(false, []));

        Assert.NotEqual(original.Value.Digest, altered.Value.Digest);

    }

    [Fact]
    public void Changing_the_system_prompt_changes_the_frozen_digest()
    {

        Result<ProviderCallEnvelope> original = CovenantProviderCallFreezer.TryFreeze(
            Descriptor([new ChatMessage(ChatRole.User, "hello")]) with { SystemPrompt = "one" },
            Sensitivity(),
            new ProviderCallMaterializationSnapshot(false, []));

        Result<ProviderCallEnvelope> altered = CovenantProviderCallFreezer.TryFreeze(
            Descriptor([new ChatMessage(ChatRole.User, "hello")]) with { SystemPrompt = "two" },
            Sensitivity(),
            new ProviderCallMaterializationSnapshot(false, []));

        Assert.NotEqual(original.Value.Digest, altered.Value.Digest);

    }

    [Fact]
    public void A_tool_call_and_its_result_freeze_without_losing_their_call_identity()
    {

        FunctionCallContent call = new("call-1", "search_workspace", new Dictionary<string, object?>
        {
            ["query"] = "wizard",
            ["limit"] = 5,
        });

        Result<ProviderCallEnvelope> frozen = CovenantProviderCallFreezer.TryFreeze(
            Descriptor(
            [
                new ChatMessage(ChatRole.Assistant, [call]),
                new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", "three hits")]),
            ]),
            Sensitivity(),
            new ProviderCallMaterializationSnapshot(false, []));

        Assert.True(frozen.IsSuccess, frozen.IsFailure ? frozen.Error.Message : string.Empty);

        ProviderContentPartEnvelope toolCall = Assert.Single(frozen.Value.Messages[0].ContentParts);

        Assert.Equal(CovenantProviderContentPart.ToolCall, toolCall.Kind);

        Assert.Equal("call-1", toolCall.ToolCallId);

        ProviderContentPartEnvelope toolResult = Assert.Single(frozen.Value.Messages[1].ContentParts);

        Assert.Equal(CovenantProviderContentPart.ToolResult, toolResult.Kind);

        Assert.Equal("call-1", toolResult.ToolCallId);

    }

    [Fact]
    public void Tool_arguments_freeze_the_same_whichever_order_they_were_supplied_in()
    {

        Result<ProviderCallEnvelope> ordered = CovenantProviderCallFreezer.TryFreeze(
            Descriptor([new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent("c", "t", new Dictionary<string, object?> { ["a"] = 1, ["b"] = 2 }),
            ])]),
            Sensitivity(),
            new ProviderCallMaterializationSnapshot(false, []));

        Result<ProviderCallEnvelope> reversed = CovenantProviderCallFreezer.TryFreeze(
            Descriptor([new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent("c", "t", new Dictionary<string, object?> { ["b"] = 2, ["a"] = 1 }),
            ])]),
            Sensitivity(),
            new ProviderCallMaterializationSnapshot(false, []));

        Assert.Equal(ordered.Value.Digest, reversed.Value.Digest);

    }

    [Fact]
    public void A_content_kind_this_build_cannot_freeze_refuses_the_whole_call()
    {

        Result<ProviderCallEnvelope> frozen = CovenantProviderCallFreezer.TryFreeze(
            Descriptor([new ChatMessage(ChatRole.User, [new UnknownContent()])]),
            Sensitivity(),
            new ProviderCallMaterializationSnapshot(false, []));

        Assert.True(frozen.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.InvalidContent, frozen.Error.Code);

    }

    [Fact]
    public void A_json_object_response_format_is_distinct_from_text()
    {
        Result<ProviderCallEnvelope> frozen = CovenantProviderCallFreezer.TryFreeze(
            Descriptor([new ChatMessage(ChatRole.User, "hello")]) with
            {
                Options = new ChatOptions { ResponseFormat = ChatResponseFormat.Json },
            },
            Sensitivity(),
            new ProviderCallMaterializationSnapshot(false, []));

        Assert.True(frozen.IsSuccess, frozen.IsFailure ? frozen.Error.Message : string.Empty);

        Assert.Equal(ProviderResponseFormat.JsonObject, frozen.Value.Options.ResponseFormat);

        Assert.False(frozen.Value.Options.HasCanonicalJsonSchema);

        Assert.Null(frozen.Value.StructuredOutputSchemaDigest);
    }

    [Fact]
    public void A_pinned_json_schema_freezes_its_canonical_bytes_and_metadata()
    {
        using JsonDocument schema = JsonDocument.Parse(
            """{"type":"object","required":["summary"],"properties":{"summary":{"type":"string"}},"additionalProperties":false}""");

        Result<ProviderCallEnvelope> frozen = CovenantProviderCallFreezer.TryFreeze(
            Descriptor([new ChatMessage(ChatRole.User, "hello")]) with
            {
                Options = new ChatOptions
                {
                    ResponseFormat = ChatResponseFormat.ForJsonSchema(
                        schema.RootElement,
                        "campaign_summary",
                        "A bounded summary."),
                },
            },
            Sensitivity(),
            new ProviderCallMaterializationSnapshot(false, []));

        Assert.True(frozen.IsSuccess, frozen.IsFailure ? frozen.Error.Message : string.Empty);

        const string canonicalSchema =
            """{"additionalProperties":false,"properties":{"summary":{"type":"string"}},"required":["summary"],"type":"object"}""";

        CovenantDigest schemaDigest = new(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalSchema)));

        Assert.Equal(ProviderResponseFormat.JsonSchema, frozen.Value.Options.ResponseFormat);

        Assert.Equal("campaign_summary", frozen.Value.Options.JsonSchemaName);

        Assert.Equal("A bounded summary.", frozen.Value.Options.JsonSchemaDescription);

        Assert.Equal(CovenantTriStateBoolean.Absent, frozen.Value.Options.JsonSchemaStrict);

        Assert.Equal(canonicalSchema, Encoding.UTF8.GetString([.. frozen.Value.Options.CanonicalJsonSchemaBytes]));

        Assert.Equal(schemaDigest, frozen.Value.Options.CanonicalJsonSchemaDigest);

        Assert.Equal(schemaDigest, frozen.Value.StructuredOutputSchemaDigest);

        Assert.Equal(canonicalSchema, Encoding.UTF8.GetString([.. frozen.Value.CanonicalStructuredOutputSchemaBytes]));
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("name")]
    [InlineData("description")]
    public void Changing_a_schema_or_its_metadata_changes_the_frozen_call(string changed)
    {
        using JsonDocument originalSchema = JsonDocument.Parse("""{"type":"string"}""");

        using JsonDocument alteredSchema = JsonDocument.Parse("""{"type":"integer"}""");

        CovenantProviderCallDescriptor original = Descriptor([new ChatMessage(ChatRole.User, "hello")]) with
        {
            Options = new ChatOptions
            {
                ResponseFormat = ChatResponseFormat.ForJsonSchema(originalSchema.RootElement, "summary", "Original description."),
            },
        };

        CovenantProviderCallDescriptor altered = original with
        {
            Options = new ChatOptions
            {
                ResponseFormat = ChatResponseFormat.ForJsonSchema(
                    changed == "schema" ? alteredSchema.RootElement : originalSchema.RootElement,
                    changed == "name" ? "different_summary" : "summary",
                    changed == "description" ? "Changed description." : "Original description."),
            },
        };

        Result<ProviderCallEnvelope> before = CovenantProviderCallFreezer.TryFreeze(
            original, Sensitivity(), new ProviderCallMaterializationSnapshot(false, []));

        Result<ProviderCallEnvelope> after = CovenantProviderCallFreezer.TryFreeze(
            altered, Sensitivity(), new ProviderCallMaterializationSnapshot(false, []));

        Assert.True(before.IsSuccess, before.IsFailure ? before.Error.Message : string.Empty);

        Assert.True(after.IsSuccess, after.IsFailure ? after.Error.Message : string.Empty);

        Assert.NotEqual(before.Value.Digest, after.Value.Digest);
    }

    [Fact]
    public void Equivalent_json_schema_property_order_keeps_the_frozen_digest()
    {
        using JsonDocument orderedSchema = JsonDocument.Parse("""{"type":"object","additionalProperties":false}""");

        using JsonDocument reversedSchema = JsonDocument.Parse("""{ "additionalProperties": false, "type": "object" }""");

        CovenantProviderCallDescriptor descriptor = Descriptor([new ChatMessage(ChatRole.User, "hello")]);

        Result<ProviderCallEnvelope> ordered = CovenantProviderCallFreezer.TryFreeze(
            descriptor with { Options = new ChatOptions { ResponseFormat = ChatResponseFormat.ForJsonSchema(orderedSchema.RootElement, "summary") } },
            Sensitivity(),
            new ProviderCallMaterializationSnapshot(false, []));

        Result<ProviderCallEnvelope> reversed = CovenantProviderCallFreezer.TryFreeze(
            descriptor with { Options = new ChatOptions { ResponseFormat = ChatResponseFormat.ForJsonSchema(reversedSchema.RootElement, "summary") } },
            Sensitivity(),
            new ProviderCallMaterializationSnapshot(false, []));

        Assert.Equal(ordered.Value.Digest, reversed.Value.Digest);
    }

    [Fact]
    public void Explicit_text_keeps_the_default_provider_options_digest()
    {
        CovenantProviderCallDescriptor descriptor = Descriptor([new ChatMessage(ChatRole.User, "hello")]);

        Result<ProviderCallEnvelope> omitted = CovenantProviderCallFreezer.TryFreeze(
            descriptor, Sensitivity(), new ProviderCallMaterializationSnapshot(false, []));

        Result<ProviderCallEnvelope> explicitText = CovenantProviderCallFreezer.TryFreeze(
            descriptor with { Options = new ChatOptions { ResponseFormat = ChatResponseFormat.Text } },
            Sensitivity(),
            new ProviderCallMaterializationSnapshot(false, []));

        FrozenProviderOptions textProjection = FrozenProviderOptions.Create(new ProviderOptionsDigestInput(
            null, null, null, null, null, null, null, [], ProviderToolChoice.None, null,
            CovenantTriStateBoolean.Absent, ProviderResponseFormat.Text, null, null, null,
            CovenantTriStateBoolean.Absent, null, null, null, CovenantReasoningWireDialect.Standard, default));

        Assert.Equal(textProjection.Digest, omitted.Value.Options.Digest);

        Assert.Equal(omitted.Value.Digest, explicitText.Value.Digest);
    }

    [Fact]
    public void A_clean_sensitivity_still_freezes_but_carries_no_generation()
    {

        Result<ProviderCallEnvelope> frozen = CovenantProviderCallFreezer.TryFreeze(
            Descriptor([new ChatMessage(ChatRole.User, "hello")]),
            Sensitivity(ContentSensitivity.None),
            new ProviderCallMaterializationSnapshot(false, []));

        Assert.True(frozen.IsSuccess);

        Assert.Equal(ContentSensitivity.None, frozen.Value.Sensitivity.Level);

    }

    private static CovenantProviderCallDescriptor Descriptor(IReadOnlyList<ChatMessage> messages) =>
        new(
            "provider.test",
            "model.test",
            CovenantProviderDispatchMode.Buffered,
            "o200k_base",
            128_000,
            0,
            "system prompt",
            null,
            messages,
            null);

    private static ProviderCallSensitivity Sensitivity(
        ContentSensitivity level = ContentSensitivity.CovenantDerived)
    {

        GenerationProvenance provenance = level is ContentSensitivity.CovenantDerived
            ? GenerationProvenance.CreateExact([Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")])
            : GenerationProvenance.CreateExact([]);

        return new ProviderCallSensitivity(
            level,
            provenance,
            CovenantDigests.Sensitivity(new SensitivityDigestInput(
                level,
                provenance.Mode,
                provenance.ExactGenerationIds,
                provenance.BloomBits)));

    }

    private sealed class UnknownContent : AIContent;

}
