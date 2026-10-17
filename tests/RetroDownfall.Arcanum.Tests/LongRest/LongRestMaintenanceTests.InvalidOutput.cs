using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

// A paid answer that is not an instance of the closed shape is paid for and cannot be applied. The sweep it parks must say so: the code the
// fault policy, the Command Reference and the operator all read for "the provider answered and the answer was unusable" is LongRest.InvalidOutput,
// not LongRest.InvalidRequest, which says the caller's own request was malformed.
public sealed partial class LongRestMaintenanceTests
{
    [Theory]
    [InlineData("malformed")]
    [InlineData("survivor-shaped")]
    [InlineData("not-json")]
    public async Task A_paid_adjudication_answer_that_cannot_be_applied_parks_the_sweep_as_invalid_output_and_keeps_the_usage_billed(string answer)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false);

        f.Handler.InsideSend = () =>
        {
            f.Handler.ChatText = LongRestScriptedVerdicts.IsAdjudication(f.Handler.LastBody!)
                ? answer switch
                {
                    "malformed" => LongRestScriptedVerdicts.Malformed,
                    "survivor-shaped" => """{"kind":"EquivalentObservations","survivorVersionId":"v-1"}""",
                    _ => "not json at all",
                }
                : "{\"summary\":\"Accepted summary.\"}";

            return Task.CompletedTask;
        };

        Result<LongRestSweepProgress> result = await f.ResumeAdmittedAsync(f.Operation, CancellationToken.None);

        string? code = result.IsFailure ? result.Error.Code : result.Value.DeferredReason;

        Assert.Equal(ErrorCodes.LongRest.InvalidOutput, code);

        LongRunningOperation parked = (await f.Operations.GetAsync(f.Operation))!;

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, parked.State);

        Assert.Equal(ErrorCodes.LongRest.InvalidOutput, parked.TerminalErrorCode);

        // Paid for, not applied: the answer's usage is billed and the attempt is settled Invalid, not left in flight.
        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations WHERE Purpose = 'long-rest-adjudication';"));

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_adjudications;"));

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_receipts;"));

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_provider_effects WHERE StateCode IN (1, 2, 3);"));

        // The code is one the fault policy classifies: an unusable answer needs an operator, it is not retried by the worker.
        Assert.Equal(LongRestFaultClass.NeedsOperator, LongRestFaultPolicy.Classify(parked.TerminalErrorCode));
    }
}
