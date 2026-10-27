using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Covenant;

public sealed partial class CovenantErasureCoordinatorTests
{
    [Theory]
    [InlineData("terminal-ledger", "physically close its terminal ledger window")]
    [InlineData("journal", "disposition to the offline transition journal")]
    [InlineData("attention-ledger", "physically close its attention ledger window")]
    [InlineData("attention-proof", "prove its parked operation attention state")]
    [InlineData("disposition", "could not record its")]
    [InlineData("retirement", "could not retire the offline transition journal")]
    [InlineData("grimoire-disposition", "disposition on the Grimoire closure")]
    [InlineData("grimoire-abort", "could not abort the exact Grimoire closing owner")]
    public async Task Terminal_failure_diagnostics_correlate_the_actual_operation_and_phase(
        string scenario,
        string message)
    {
        using CancellationTokenSource cancellation = new();

        CleanupRefusingLedgerDrain? drain = scenario switch
        {
            "terminal-ledger" => new(LedgerCleanupFailure.ExactPoolClear, failOnClearCall: 3),
            "attention-ledger" => new(LedgerCleanupFailure.ExactPoolClear, failOnClearCall: 0),
            _ => null,
        };

        CoordinatorHarness harness = new(
            drain: drain,
            dispositionFailure: scenario == "disposition" ? DispositionFailureMode.ReturnedFailure : DispositionFailureMode.None,
            beforeGrimoireStageTwo: scenario == "grimoire-abort" ? cancellation.Cancel : null,
            terminalSuffixFaultSeam: (boundary, _) => Task.FromResult(
                scenario == "journal" && boundary == GrimoireOfflineTransitionTerminalSuffixBoundary.DatabaseTerminalized
                    || scenario == "retirement" && boundary == GrimoireOfflineTransitionTerminalSuffixBoundary.CovenantDispositionInFlight
                    ? Result.Failure(PrivateTerminalRefusal())
                    : Result.Success()));

        if (scenario == "attention-proof")
        {
            harness.Transition.FailingStep = "publish";

            harness.Store.TryTransitionOverride = static _ => false;
        }

        if (scenario == "grimoire-disposition")
        {
            harness.GrimoireBoundaries.ClosedDispositionFailure = PrivateTerminalRefusal();
        }

        if (scenario == "grimoire-abort")
        {
            harness.GrimoireBoundaries.AbortClosingFailure = PrivateTerminalRefusal();
        }

        Result<CovenantErasureCompletion> completion = await harness.RunAsync(
            CovenantResetPhase.InventoryPrepared,
            cancellation.Token).WaitAsync(BoundedWait);

        Assert.True(completion.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.MaintenanceFailed, completion.Error.Code);

        TestLogEntry diagnostic = Assert.Single(harness.Diagnostics.Entries,
            entry => entry.Level == LogLevel.Error && entry.Message.Contains(message, StringComparison.Ordinal));

        CovenantResetPhase expectedPhase = scenario is "attention-ledger" or "grimoire-abort"
            ? CovenantResetPhase.InventoryPrepared
            : CovenantResetPhase.ReopenedVerified;

        Assert.Equal(OperationId, Assert.IsType<Guid>(Assert.Single(diagnostic.Properties!, property => property.Key == "OperationId").Value));

        Assert.Equal(expectedPhase, Assert.IsType<CovenantResetPhase>(Assert.Single(diagnostic.Properties!, property => property.Key == "ResetPhase").Value));

        Assert.All(harness.Diagnostics.Entries, entry =>
        {
            Assert.Null(entry.Exception);

            Assert.DoesNotContain("PRIVATE-TERMINAL-CANARY", entry.Message, StringComparison.Ordinal);

            Assert.All(entry.Properties!, property => Assert.DoesNotContain("PRIVATE-TERMINAL-CANARY", property.Value?.ToString() ?? string.Empty, StringComparison.Ordinal));
        });

        bool covenantReopened = scenario is "retirement" or "grimoire-disposition" or "grimoire-abort";

        Assert.Equal(covenantReopened, await harness.AdmissionIsOpenAsync());

        Assert.False(await harness.GrimoireAdmissionIsOpenAsync());

        Assert.Equal(scenario is "disposition" || covenantReopened ? 1 : 0, harness.Gate.DispositionAttempts);

        Assert.Equal(0, harness.Gate.KeepClosedAttempts);

        Assert.Equal(1, harness.Gate.DisposalCount);

        Assert.Equal(scenario is not "grimoire-disposition" and not "grimoire-abort", File.Exists(harness.Phases.JournalPath));

        LongRunningOperation durable = Assert.Single(harness.Store.Operations);

        Assert.Equal(scenario switch
        {
            "attention-ledger" => LongRunningOperationState.ReconciliationRequired,
            "attention-proof" => LongRunningOperationState.Running,
            "grimoire-abort" => LongRunningOperationState.Failed,
            _ => LongRunningOperationState.Completed,
        }, durable.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Compatible_phase_ten_reverifies_from_open_or_closed_gate_without_repeating_erasure(bool alreadyClosed)
    {
        CoordinatorHarness harness = new();

        if (alreadyClosed)
        {
            await harness.CloseAndAdoptAsync();
        }

        Result<CovenantErasureCompletion> completion = await harness.RunAsync(CovenantResetPhase.ReopenedVerified);

        Assert.True(completion.IsSuccess, completion.IsFailure ? completion.Error.Message : null);

        Assert.Equal(CovenantExclusiveLeaseDisposition.CommitAndReopen, completion.Value.Disposition);

        Assert.Equal(1, harness.Transition.VerifyReopenCalls);

        Assert.Same(harness.Transition.VerifiedCandidate, harness.Transition.PublishedCandidate);

        Assert.Equal(0, harness.Artifacts.Calls);

        Assert.Equal(0, harness.ManagedFiles.Calls);

        Assert.Equal(0, harness.Operations.CheckpointCalls);

        Assert.True(await harness.AdmissionIsOpenAsync());

        Assert.True(await harness.GrimoireAdmissionIsOpenAsync());
    }

    [Fact]
    public async Task Fresh_verification_transfers_one_candidate_without_a_durable_phase_ten_write()
    {
        List<CovenantResetPhase> observed = [];

        CoordinatorHarness harness = new(faultSeam: (_, phase, _) =>
        {
            observed.Add(phase);

            return Task.FromResult(Result.Success());
        });

        Result<CovenantErasureCompletion> completion = await harness.RunAsync(CovenantResetPhase.InventoryPrepared);

        Assert.True(completion.IsSuccess);

        Assert.Equal(1, harness.Transition.VerifyReopenCalls);

        Assert.Same(harness.Transition.VerifiedCandidate, harness.Transition.PublishedCandidate);

        Assert.Equal(CandidateGeneration, harness.Transition.PublishedGeneration);

        Assert.Equal(0, harness.Operations.CheckpointCalls);

        Assert.NotEmpty(observed);

        Assert.Contains(CovenantResetPhase.SidecarsVerified, observed);

        Assert.DoesNotContain(CovenantResetPhase.ReopenedVerified, observed);

        Assert.True(await harness.AdmissionIsOpenAsync());
    }

    private static Error PrivateTerminalRefusal() => new(
        ErrorCodes.Covenant.MaintenanceFailed,
        "PRIVATE-TERMINAL-CANARY /private/credentials/catalog");
}
