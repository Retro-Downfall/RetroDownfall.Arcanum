using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// The private transport buffer that holds every streamed tool-name and argument fragment until one
/// complete call is frozen and can be classified (§10.14).
/// </summary>
/// <remarks>
/// The whole point of this type is what it refuses to expose. A partial name is the one thing a
/// hostile provider needs to make <c>propose_covenant</c> look like an ordinary tool for long enough
/// to get its arguments onto a public stream, so the buffer is asserted here on its surface as well
/// as its behavior.
/// </remarks>
public sealed class ProviderToolCallBufferTests
{

    [Fact]
    public void Freeze_returns_the_exact_assembled_name_and_arguments()
    {
        ProviderToolCallBuffer buffer = new();

        Assert.True(buffer.AppendNameFragment(0, "call-1", "propose_"u8).IsSuccess);
        Assert.True(buffer.AppendNameFragment(0, "call-1", "covenant"u8).IsSuccess);
        Assert.True(buffer.AppendArgumentFragment(0, "{\"key\":\"a\","u8).IsSuccess);
        Assert.True(buffer.AppendArgumentFragment(0, "\"content\":\"b\"}"u8).IsSuccess);

        Result<FrozenProviderToolCall> frozen = buffer.Freeze(0);

        Assert.True(frozen.IsSuccess, frozen.Error.Message);
        Assert.Equal("propose_covenant", frozen.Value.Name);
        Assert.Equal("call-1", frozen.Value.CallId);
        Assert.Equal(0, frozen.Value.CallIndex);
        Assert.Equal(
            "{\"key\":\"a\",\"content\":\"b\"}",
            Encoding.UTF8.GetString(frozen.Value.ArgumentsUtf8.AsSpan()));
    }

    [Fact]
    public void Freeze_accepts_a_multibyte_sequence_that_crosses_two_name_fragments()
    {
        // "propose_ünicode" split mid-U+00FC: 0xC3 lands in one fragment and 0xBC in the next.
        ProviderToolCallBuffer buffer = new();

        Assert.True(buffer.AppendNameFragment(0, "call-1", [.. "tool_"u8, 0xC3]).IsSuccess);
        Assert.True(buffer.AppendNameFragment(0, "call-1", [0xBC, .. "nicode"u8]).IsSuccess);

        Result<FrozenProviderToolCall> frozen = buffer.Freeze(0);

        Assert.True(frozen.IsSuccess, frozen.Error.Message);
        Assert.Equal("tool_ünicode", frozen.Value.Name);
    }

    [Fact]
    public void Freeze_rejects_a_multibyte_sequence_that_never_completes()
    {
        ProviderToolCallBuffer buffer = new();

        Assert.True(buffer.AppendNameFragment(0, "call-1", [.. "tool_"u8, 0xC3]).IsSuccess);

        Result<FrozenProviderToolCall> frozen = buffer.Freeze(0);

        Assert.Equal(ErrorCodes.Hub.ProviderToolCallInvalid, frozen.Error.Code);
    }

    [Fact]
    public void AppendNameFragment_rejects_malformed_utf8_as_soon_as_it_can_prove_it()
    {
        ProviderToolCallBuffer buffer = new();

        Result malformed = buffer.AppendNameFragment(0, "call-1", [0xC3, 0x28]);

        Assert.Equal(ErrorCodes.Hub.ProviderToolCallInvalid, malformed.Error.Code);
    }

    [Fact]
    public void Arguments_may_arrive_before_between_and_after_the_name_fragments()
    {
        ProviderToolCallBuffer buffer = new();

        Assert.True(buffer.AppendArgumentFragment(3, "{\"key\""u8).IsSuccess);
        Assert.True(buffer.AppendNameFragment(3, "call-9", "retire_"u8).IsSuccess);
        Assert.True(buffer.AppendArgumentFragment(3, ":\"a\","u8).IsSuccess);
        Assert.True(buffer.AppendNameFragment(3, "call-9", "covenant"u8).IsSuccess);
        Assert.True(buffer.AppendArgumentFragment(3, "\"lane\":\"Proposed\"}"u8).IsSuccess);

        Result<FrozenProviderToolCall> frozen = buffer.Freeze(3);

        Assert.True(frozen.IsSuccess, frozen.Error.Message);
        Assert.Equal("retire_covenant", frozen.Value.Name);
        Assert.Equal(
            "{\"key\":\"a\",\"lane\":\"Proposed\"}",
            Encoding.UTF8.GetString(frozen.Value.ArgumentsUtf8.AsSpan()));
    }

    [Fact]
    public void Interleaved_call_indexes_keep_isolated_buffers()
    {
        ProviderToolCallBuffer buffer = new();

        Assert.True(buffer.AppendNameFragment(0, "call-a", "read_"u8).IsSuccess);
        Assert.True(buffer.AppendNameFragment(1, "call-b", "propose_"u8).IsSuccess);
        Assert.True(buffer.AppendArgumentFragment(1, "{\"b\":1}"u8).IsSuccess);
        Assert.True(buffer.AppendNameFragment(0, "call-a", "saga"u8).IsSuccess);
        Assert.True(buffer.AppendArgumentFragment(0, "{\"a\":1}"u8).IsSuccess);
        Assert.True(buffer.AppendNameFragment(1, "call-b", "covenant"u8).IsSuccess);

        FrozenProviderToolCall first = buffer.Freeze(0).Value;
        FrozenProviderToolCall second = buffer.Freeze(1).Value;

        Assert.Equal("read_saga", first.Name);
        Assert.Equal("{\"a\":1}", Encoding.UTF8.GetString(first.ArgumentsUtf8.AsSpan()));
        Assert.Equal("propose_covenant", second.Name);
        Assert.Equal("{\"b\":1}", Encoding.UTF8.GetString(second.ArgumentsUtf8.AsSpan()));
    }

    [Fact]
    public void A_reported_final_name_that_differs_from_the_assembled_one_fails_closed()
    {
        ProviderToolCallBuffer buffer = new();

        _ = buffer.AppendNameFragment(0, "call-1", "propose_covenant"u8);

        Result<FrozenProviderToolCall> prefixTrick = buffer.Freeze(0, "propose_");

        Assert.Equal(ErrorCodes.Hub.ProviderToolCallInvalid, prefixTrick.Error.Code);
    }

    [Fact]
    public void A_reported_final_name_that_matches_the_assembled_one_is_accepted()
    {
        ProviderToolCallBuffer buffer = new();

        _ = buffer.AppendNameFragment(0, "call-1", "propose_covenant"u8);

        Result<FrozenProviderToolCall> frozen = buffer.Freeze(0, "propose_covenant");

        Assert.True(frozen.IsSuccess, frozen.Error.Message);
    }

    [Fact]
    public void A_changed_call_id_on_one_index_fails_closed()
    {
        ProviderToolCallBuffer buffer = new();

        _ = buffer.AppendNameFragment(0, "call-1", "propose_"u8);

        Result changed = buffer.AppendNameFragment(0, "call-2", "covenant"u8);

        Assert.Equal(ErrorCodes.Hub.ProviderToolCallInvalid, changed.Error.Code);
    }

    [Fact]
    public void A_frozen_call_index_can_never_be_reused()
    {
        ProviderToolCallBuffer buffer = new();

        _ = buffer.AppendNameFragment(0, "call-1", "read_saga"u8);
        _ = buffer.Freeze(0);

        Result reusedName = buffer.AppendNameFragment(0, "call-1", "x"u8);
        Result reusedArguments = buffer.AppendArgumentFragment(0, "{}"u8);
        Result<FrozenProviderToolCall> refrozen = buffer.Freeze(0);

        Assert.Equal(ErrorCodes.Hub.ProviderToolCallInvalid, reusedName.Error.Code);
        Assert.Equal(ErrorCodes.Hub.ProviderToolCallInvalid, reusedArguments.Error.Code);
        Assert.Equal(ErrorCodes.Hub.ProviderToolCallInvalid, refrozen.Error.Code);
    }

    [Fact]
    public void An_empty_name_is_never_a_complete_call()
    {
        ProviderToolCallBuffer buffer = new();

        _ = buffer.AppendArgumentFragment(0, "{}"u8);

        Result<FrozenProviderToolCall> frozen = buffer.Freeze(0);

        Assert.Equal(ErrorCodes.Hub.ProviderToolCallInvalid, frozen.Error.Code);
    }

    [Fact]
    public void Crossing_the_call_index_bound_aborts_the_attempt_and_clears_every_buffer()
    {
        ProviderToolCallBuffer buffer = new();

        for (int index = 0; index < CovenantLimits.MaxProviderToolCallIndexes; index++)
        {
            Assert.True(buffer.AppendNameFragment(index, $"call-{index}", "read_saga"u8).IsSuccess);
        }

        Result overflow = buffer.AppendNameFragment(
            CovenantLimits.MaxProviderToolCallIndexes,
            "call-overflow",
            "read_saga"u8);

        Assert.Equal(ErrorCodes.Hub.ProviderToolBufferExceeded, overflow.Error.Code);
        Assert.True(buffer.IsAborted);
        Assert.Equal(0, buffer.OpenCallCount);
        Assert.Equal(0, buffer.BufferedByteCount);
        Assert.Equal(
            ErrorCodes.Hub.ProviderToolBufferExceeded,
            buffer.Freeze(0).Error.Code);
    }

    [Fact]
    public void Crossing_the_name_bound_aborts_before_the_fragment_is_retained()
    {
        ProviderToolCallBuffer buffer = new();

        byte[] oversized = new byte[CovenantLimits.MaxProviderToolNameBytes + 1];

        Array.Fill(oversized, (byte)'a');

        Result overflow = buffer.AppendNameFragment(0, "call-1", oversized);

        Assert.Equal(ErrorCodes.Hub.ProviderToolBufferExceeded, overflow.Error.Code);
        Assert.True(buffer.IsAborted);
        Assert.Equal(0, buffer.BufferedByteCount);
    }

    [Fact]
    public void Crossing_the_per_call_argument_bound_aborts_the_attempt()
    {
        ProviderToolCallBuffer buffer = new();

        byte[] half = new byte[(CovenantLimits.MaxProviderToolArgumentBytes / 2) + 1];

        Array.Fill(half, (byte)'x');

        Assert.True(buffer.AppendArgumentFragment(0, half).IsSuccess);

        Result overflow = buffer.AppendArgumentFragment(0, half);

        Assert.Equal(ErrorCodes.Hub.ProviderToolBufferExceeded, overflow.Error.Code);
        Assert.True(buffer.IsAborted);
    }

    [Fact]
    public void Crossing_the_aggregate_attempt_bound_aborts_the_attempt()
    {
        ProviderToolCallBuffer buffer = new();

        byte[] block = new byte[CovenantLimits.MaxProviderToolArgumentBytes];

        Array.Fill(block, (byte)'x');

        int accepted = 0;

        for (int index = 0; index < CovenantLimits.MaxProviderToolCallIndexes; index++)
        {

            if (buffer.AppendArgumentFragment(index, block).IsFailure)
            {
                break;
            }

            accepted++;

        }

        Assert.Equal(
            CovenantLimits.MaxProviderToolBufferedBytesPerAttempt / CovenantLimits.MaxProviderToolArgumentBytes,
            accepted);
        Assert.True(buffer.IsAborted);
        Assert.Equal(0, buffer.BufferedByteCount);
    }

    [Fact]
    public void The_public_surface_exposes_no_partial_name_or_argument_content()
    {
        string[] surface = [.. typeof(ProviderToolCallBuffer)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(static member => member.Name)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

        // Freeze is the only exit. A peek, a snapshot, or a partial-name property would be a way for
        // a public stream to see profile arguments before classification decided they were private.
        Assert.Equal(
            [
                ".ctor",
                nameof(ProviderToolCallBuffer.AppendArgumentFragment),
                nameof(ProviderToolCallBuffer.AppendNameFragment),
                nameof(ProviderToolCallBuffer.BufferedByteCount),
                nameof(ProviderToolCallBuffer.Clear),
                nameof(ProviderToolCallBuffer.Freeze),
                nameof(ProviderToolCallBuffer.IsAborted),
                nameof(ProviderToolCallBuffer.OpenCallCount),
                "get_" + nameof(ProviderToolCallBuffer.BufferedByteCount),
                "get_" + nameof(ProviderToolCallBuffer.IsAborted),
                "get_" + nameof(ProviderToolCallBuffer.OpenCallCount),
            ],
            surface);
    }

    [Fact]
    public void Clear_releases_every_private_buffer_without_reopening_an_aborted_attempt()
    {
        ProviderToolCallBuffer buffer = new();

        _ = buffer.AppendNameFragment(0, "call-1", "propose_covenant"u8);
        _ = buffer.AppendArgumentFragment(0, "{\"key\":\"secret\"}"u8);

        buffer.Clear();

        Assert.Equal(0, buffer.OpenCallCount);
        Assert.Equal(0, buffer.BufferedByteCount);
        Assert.False(buffer.IsAborted);
        Assert.True(buffer.AppendNameFragment(0, "call-1", "read_saga"u8).IsSuccess);
    }

}
