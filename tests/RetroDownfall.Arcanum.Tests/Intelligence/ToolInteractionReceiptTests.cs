using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class ToolInteractionReceiptTests
{

    [Fact]
    public void Derive_matches_frozen_version_one_vector()
    {

        ToolInteractionReceipt receipt = ToolInteractionReceiptDerivation.Derive(
            new ToolInvocationIdentity(
                InvocationId: "turn-42",
                ProviderToolCallId: "provider-call",
                ToolRoundOrdinal: 3,
                CallOrdinal: 2,
                ToolName: "apply_patch"));

        Assert.Equal(
            Guid.Parse("3cd73c1d-46ac-8d5c-af2b-1aefc922e1e4"),
            receipt.Id);

        Assert.Equal(
            Guid.Parse("9231a2ce-4c73-84b5-a946-b20aa6ac6037"),
            receipt.CallEntryId);

        Assert.Equal(
            Guid.Parse("9f642e2f-8e6e-8fe0-a45b-3f97ff761f66"),
            receipt.ResultEntryId);

    }

    [Fact]
    public void Derive_is_deterministic_and_normalizes_provider_id()
    {

        ToolInteractionReceipt first = ToolInteractionReceiptDerivation.Derive(
            new ToolInvocationIdentity(
                "turn",
                "  provider-e\u0301  ",
                ToolRoundOrdinal: 0,
                CallOrdinal: 0,
                ToolName: " APPLY_PATCH "));

        ToolInteractionReceipt second = ToolInteractionReceiptDerivation.Derive(
            new ToolInvocationIdentity(
                "turn",
                "provider-\u00e9",
                ToolRoundOrdinal: 0,
                CallOrdinal: 0,
                ToolName: "apply_patch"));

        Assert.Equal(first, second);

    }

    [Fact]
    public void Duplicate_provider_ids_in_different_rounds_get_distinct_receipts()
    {

        ToolInteractionReceipt first = Derive(round: 0, call: 0);

        ToolInteractionReceipt second = Derive(round: 1, call: 0);

        Assert.NotEqual(first.Id, second.Id);

        Assert.NotEqual(first.CallEntryId, second.CallEntryId);

        Assert.NotEqual(first.ResultEntryId, second.ResultEntryId);

    }

    [Fact]
    public void Multiple_patch_calls_in_one_round_get_distinct_receipts()
    {

        ToolInteractionReceipt first = Derive(round: 2, call: 0);

        ToolInteractionReceipt second = Derive(round: 2, call: 1);

        Assert.NotEqual(first.Id, second.Id);

    }

    [Fact]
    public void Call_and_result_entry_domains_are_separate()
    {

        ToolInteractionReceipt receipt = Derive(round: 0, call: 0);

        Assert.NotEqual(receipt.Id, receipt.CallEntryId);

        Assert.NotEqual(receipt.Id, receipt.ResultEntryId);

        Assert.NotEqual(receipt.CallEntryId, receipt.ResultEntryId);

    }

    private static ToolInteractionReceipt Derive(int round, int call) =>
        ToolInteractionReceiptDerivation.Derive(
            new ToolInvocationIdentity(
                InvocationId: "run-123",
                ProviderToolCallId: "duplicate-provider-id",
                ToolRoundOrdinal: round,
                CallOrdinal: call,
                ToolName: "apply_patch"));

}
