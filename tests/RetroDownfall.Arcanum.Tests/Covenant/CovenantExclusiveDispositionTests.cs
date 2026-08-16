using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Covenant;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// The single decision an exclusive Covenant operation makes about how it leaves admission.
/// </summary>
public sealed class CovenantExclusiveDispositionTests
{

    private static CancellationToken Token => CancellationToken.None;

    [Fact]
    public void Every_evidence_combination_selects_exactly_one_disposition()
    {

        List<(CovenantExclusiveDispositionEvidence Evidence, CovenantExclusiveLeaseDisposition Expected)> cases = [];

        foreach (bool storage in (bool[])[false, true])
        {

            foreach (bool authority in (bool[])[false, true])
            {

                foreach (bool mutated in (bool[])[false, true])
                {

                    foreach (bool published in (bool[])[false, true])
                    {

                        CovenantExclusiveDispositionEvidence evidence =
                            new(storage, authority, mutated, published);

                        CovenantExclusiveLeaseDisposition expected = !storage || !authority
                            ? CovenantExclusiveLeaseDisposition.KeepClosed
                            : !mutated
                                ? CovenantExclusiveLeaseDisposition.RollbackAndReopen
                                : published
                                    ? CovenantExclusiveLeaseDisposition.CommitAndReopen
                                    : CovenantExclusiveLeaseDisposition.KeepClosed;

                        cases.Add((evidence, expected));

                    }

                }

            }

        }

        Assert.Equal(16, cases.Count);

        Assert.All(
            cases,
            entry => Assert.Equal(entry.Expected, CovenantExclusiveDisposition.Select(entry.Evidence)));

    }

    [Fact]
    public void Unverified_storage_or_authority_never_reopens_however_much_else_is_proven()
    {

        Assert.Equal(
            CovenantExclusiveLeaseDisposition.KeepClosed,
            CovenantExclusiveDisposition.Select(new CovenantExclusiveDispositionEvidence(false, true, true, true)));

        Assert.Equal(
            CovenantExclusiveLeaseDisposition.KeepClosed,
            CovenantExclusiveDisposition.Select(new CovenantExclusiveDispositionEvidence(true, false, true, true)));

    }

    [Fact]
    public void Only_a_proven_no_mutation_rolls_back_and_only_a_published_commit_reopens()
    {

        Assert.Equal(
            CovenantExclusiveLeaseDisposition.RollbackAndReopen,
            CovenantExclusiveDisposition.Select(new CovenantExclusiveDispositionEvidence(true, true, false, false)));

        Assert.Equal(
            CovenantExclusiveLeaseDisposition.CommitAndReopen,
            CovenantExclusiveDisposition.Select(new CovenantExclusiveDispositionEvidence(true, true, true, true)));

        Assert.Equal(
            CovenantExclusiveLeaseDisposition.KeepClosed,
            CovenantExclusiveDisposition.Select(new CovenantExclusiveDispositionEvidence(true, true, true, false)));

    }

    [Theory]
    [InlineData(CovenantExclusiveLeaseDisposition.CommitAndReopen, true)]
    [InlineData(CovenantExclusiveLeaseDisposition.RollbackAndReopen, true)]
    [InlineData(CovenantExclusiveLeaseDisposition.KeepClosed, false)]
    public void Only_a_reopening_disposition_permits_a_terminal_journal_phase(
        CovenantExclusiveLeaseDisposition disposition,
        bool terminalizing) =>
        Assert.Equal(terminalizing, CovenantExclusiveDisposition.IsTerminalizing(disposition));

    [Fact]
    public async Task Repair_finalizer_advances_to_completed_only_after_a_successful_commit()
    {

        List<CovenantSchemaRepairPhase> advanced = [];

        CovenantSchemaRepairPostDispositionFinalizer finalizer = new(
            (phase, _) =>
            {

                advanced.Add(phase);

                return Task.FromResult(Result.Success());

            });

        Result finalized = await finalizer.FinalizeAfterSuccessfulDispositionAsync(
            CovenantExclusiveLeaseDisposition.CommitAndReopen,
            Token);

        Assert.True(finalized.IsSuccess);

        Assert.Equal([CovenantSchemaRepairPhase.Completed], advanced);

        Assert.True(finalizer.WasInvoked);

    }

    [Fact]
    public async Task Repair_finalizer_abandons_after_a_proven_rollback_and_never_runs_twice()
    {

        List<CovenantSchemaRepairPhase> advanced = [];

        CovenantSchemaRepairPostDispositionFinalizer finalizer = new(
            (phase, _) =>
            {

                advanced.Add(phase);

                return Task.FromResult(Result.Success());

            });

        _ = await finalizer.FinalizeAfterSuccessfulDispositionAsync(
            CovenantExclusiveLeaseDisposition.RollbackAndReopen,
            Token);

        Result second = await finalizer.FinalizeAfterSuccessfulDispositionAsync(
            CovenantExclusiveLeaseDisposition.RollbackAndReopen,
            Token);

        Assert.Equal([CovenantSchemaRepairPhase.Abandoned], advanced);

        Assert.True(second.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.LifecycleConflict, second.Error.Code);

    }

    [Fact]
    public async Task Repair_finalizer_leaves_the_journal_untouched_after_a_successful_keep_closed()
    {

        List<CovenantSchemaRepairPhase> advanced = [];

        CovenantSchemaRepairPostDispositionFinalizer finalizer = new(
            (phase, _) =>
            {

                advanced.Add(phase);

                return Task.FromResult(Result.Success());

            });

        Result finalized = await finalizer.FinalizeAfterSuccessfulDispositionAsync(
            CovenantExclusiveLeaseDisposition.KeepClosed,
            Token);

        Assert.True(finalized.IsSuccess);

        Assert.Empty(advanced);

    }

    /// <summary>
    /// The lease claims its one disposition before the attempt, so a failed disposition can never be
    /// retried under a different code and the finalizer never runs behind it.
    /// </summary>
    [Fact]
    public async Task A_failed_disposition_skips_the_finalizer_and_consumes_the_one_decision()
    {

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        RecordingPostDispositionFinalizer finalizer = new();

        await using CovenantExclusiveLease lease = (await gate.AcquireExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.SchemaRepair),
            Token)).Value;

        Result first = await lease.CompleteAsync(
            CovenantExclusiveLeaseDisposition.CommitAndReopen,
            finalizer,
            Token);

        Result second = await lease.CompleteAsync(
            CovenantExclusiveLeaseDisposition.KeepClosed,
            finalizer,
            Token);

        Assert.True(first.IsSuccess);

        Assert.True(second.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.LifecycleConflict, second.Error.Code);

        Assert.Equal(1, finalizer.Invocations);

    }

}
