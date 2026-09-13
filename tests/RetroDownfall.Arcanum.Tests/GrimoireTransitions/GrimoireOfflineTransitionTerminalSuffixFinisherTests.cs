using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

namespace RetroDownfall.Arcanum.Tests.GrimoireTransitions;

public sealed class GrimoireOfflineTransitionTerminalSuffixFinisherTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("claimed")]
    [InlineData("foreign-winner")]
    [InlineData("wrong-binding")]
    public async Task An_already_satisfied_bound_parent_requires_the_exact_completed_winner(
        string mutation)
    {
        CovenantDigest binding = Digest(0x31);

        CovenantDigest winner = Digest(0x51);

        RecordingParentReceipt? parent = mutation is "missing"
            ? null
            : new RecordingParentReceipt(
                binding,
                mutation switch
                {
                    "claimed" => Result<CovenantDigest>.Failure(new Error(
                        ErrorCodes.Covenant.ManualRecoveryRequired,
                        "The parent receipt remains claimed.")),
                    "foreign-winner" => Result<CovenantDigest>.Failure(new Error(
                        ErrorCodes.Covenant.ManualRecoveryRequired,
                        "The parent receipt names another winner.")),
                    _ => Result<CovenantDigest>.Success(Digest(0x71)),
                });

        Result result = await GrimoireOfflineTransitionTerminalSuffixFinisher
            .VerifyRecordedParentAsync(
                binding,
                GrimoireOfflineTransitionReconciliationStep.ParentReceiptSatisfied,
                parent,
                winner,
                CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(0, parent?.PublishCalls ?? 0);

        Assert.Equal(parent is null ? 0 : 1, parent?.VerifyCalls ?? 0);
    }

    [Fact]
    public async Task An_already_satisfied_bound_parent_is_verified_without_republication()
    {
        CovenantDigest binding = Digest(0x31);

        RecordingParentReceipt parent = new(
            binding,
            Result<CovenantDigest>.Success(binding));

        Result result = await GrimoireOfflineTransitionTerminalSuffixFinisher
            .VerifyRecordedParentAsync(
                binding,
                GrimoireOfflineTransitionReconciliationStep.CovenantDispositionVerified,
                parent,
                Digest(0x51),
                CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(1, parent.VerifyCalls);

        Assert.Equal(0, parent.PublishCalls);
    }

    private static CovenantDigest Digest(byte value) =>
        new([.. Enumerable.Repeat(value, 32)]);

    private sealed class RecordingParentReceipt(
        CovenantDigest binding,
        Result<CovenantDigest> verification) : IGrimoireOfflineTransitionParentReceiptSink
    {
        internal int PublishCalls { get; private set; }

        internal int VerifyCalls { get; private set; }

        public CovenantDigest BindingDigest => binding;

        public Task<Result<CovenantDigest>> PublishAndRereadAsync(
            CovenantDigest terminalWinnerDigest,
            CancellationToken cancellationToken)
        {
            PublishCalls++;

            return Task.FromResult(Result<CovenantDigest>.Success(binding));
        }

        public Task<Result<CovenantDigest>> VerifyCompletedAsync(
            CovenantDigest terminalWinnerDigest,
            CancellationToken cancellationToken)
        {
            VerifyCalls++;

            return Task.FromResult(verification);
        }
    }
}
