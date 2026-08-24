using System.Collections.Immutable;

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
