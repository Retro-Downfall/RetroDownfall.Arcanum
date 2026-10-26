using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Infrastructure.Operations.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

// A refusal that only the provider layer can see (the daily budget, a credential) comes after the attempt is prepared. It must not leave
// that attempt behind: a Prepared row cannot be certified as not sent by a same-boot abort, so it would stop a clean readmission for good.
public sealed partial class LongRestMaintenanceTests
{
    private static async Task RetryAfterDrainedReopenAsync(MaintenanceFixture f, string reason)
    {
        // A same-boot drained maintenance abort (a backup or a reset) reopens admission at a later generation.
        await AdditionalReopenEdgeAsync(f, drained: true);

        // An operator retry returns the parked sweep to the worker, which readmits it past the drained interval.
        LongRunningOperation parked = (await f.Operations.GetAsync(f.Operation))!;

        Assert.True(await f.Operations.ResetForRetryAsync(f.Operation, parked.Revision, DateTimeOffset.UtcNow));

        LongRunningOperationReconciler reconciler = new(f.Operations, [new LongRestSweepRecoveryHandler()], TimeProvider.System,
            NullLogger<LongRunningOperationReconciler>.Instance, new LongRunningOperationOwnership());

        Assert.Equal(1, (await reconciler.ReconcileNowAsync(reason)).RequiresAttention);

        Assert.Equal(LongRestParkCodes.ReadmitRequested, (await f.Operations.GetAsync(f.Operation))!.TerminalErrorCode);
    }

    [Fact]
    public async Task A_daily_budget_refusal_after_the_attempt_was_prepared_leaves_no_effect_row_so_a_drained_reopen_still_readmits_the_clean_sweep()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false,
            configure: settings => settings.Cost.Budget = new() { Enabled = true, DailyLimitUsd = 0.0001m });

        f.Handler.InsideSend = () =>
        {
            f.Handler.ChatText = DecisionOrSummary(f.Handler.LastBody!);

            return Task.CompletedTask;
        };

        LongRestDiscoveryGeneration original = LongRestSweepHarness.Require(await f.H.Store.ReadGenerationMetadataAsync(f.Operation, CancellationToken.None))!;

        LongRestSweepProgress first = LongRestSweepHarness.Require(await f.ResumeAdmittedAsync(f.Operation, CancellationToken.None));

        Assert.Equal(ErrorCodes.Budget.Exceeded, first.DeferredReason);

        // The refusal came from the budget reservation, after the attempt was prepared and before any send. The attempt is withdrawn:
        // the unit is exactly what it was before it, with no effect row of any state.
        Assert.Equal(0, f.Handler.SendCount);

        Assert.Equal(0L, await ScalarLongAsync(f, "SELECT count(*) FROM long_rest_provider_effects;"));

        Assert.Equal(0L, await UnsettledEffectsAsync(f));

        await RetryAfterDrainedReopenAsync(f, "budget-retry");

        LongRestSweepProgress second = LongRestSweepHarness.Require(await ResumeAdmittedWithAsync(f.Coordinator, f.RecordedAdmission, f.Operation, CancellationToken.None));

        // The readmission was not blocked by an unsettled effect: the generation adopted the current admission and the sweep asked again.
        LongRestDiscoveryGeneration readmitted = LongRestSweepHarness.Require(await f.H.Store.ReadGenerationMetadataAsync(f.Operation, CancellationToken.None))!;

        Assert.True(f.Admission.CurrentGeneration > original.Fence.AdmissionGeneration);

        Assert.Equal(f.Admission.CurrentGeneration, readmitted.Fence.AdmissionGeneration);

        Assert.Equal(ErrorCodes.Budget.Exceeded, second.DeferredReason);

        Assert.Equal(0, f.Handler.SendCount);

        Assert.Equal(0L, await UnsettledEffectsAsync(f));
    }

    // The summary is paid and accepted; the embedding that follows is the second attempt of the same unit, and its credential cannot be
    // resolved. That attempt had a sent predecessor, so it cannot simply disappear: it is withdrawn into the failed shape of a send that
    // never left the process, which is not charged to the Unknown-Usage Cap. The sweep then readmits, reuses the paid summary and finishes.
    [Fact]
    public async Task A_credential_refusal_of_a_second_attempt_is_withdrawn_uncharged_and_the_readmitted_sweep_finishes()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false);

        CredentialRefusals keys = new(refuseResolution: 2);

        f.Keys = keys;

        f.Handler.InsideSend = () =>
        {
            f.Handler.ChatText = DecisionOrSummary(f.Handler.LastBody!);

            return Task.CompletedTask;
        };

        LongRestSweepProgress first = LongRestSweepHarness.Require(await f.ResumeAdmittedAsync(f.Operation, CancellationToken.None));

        Assert.Equal(ErrorCodes.LongRest.CredentialUnavailable, first.DeferredReason);

        Assert.Equal(2, keys.Resolutions);

        // Only the summary was sent. Its effect row is still there, now settled as a send that never left the process for the second attempt.
        Assert.Equal(1, f.Handler.SendCount);

        Assert.Equal(1L, await ScalarLongAsync(f, "SELECT count(*) FROM long_rest_provider_effects;"));

        Assert.Equal(0L, await UnsettledEffectsAsync(f));

        Assert.Equal(2L, await ScalarLongAsync(f, "SELECT PhysicalOrdinal FROM long_rest_provider_effects;"));

        Assert.Equal(1L, await ScalarLongAsync(f, "SELECT SentCount FROM long_rest_provider_effects;"));

        Assert.Equal(0L, await ScalarLongAsync(f, "SELECT FailureCount FROM long_rest_provider_effects;"));

        Assert.Equal((long)LongRestDispatchAttemptState.Failed, await ScalarLongAsync(f, "SELECT StateCode FROM long_rest_provider_effects;"));

        Assert.Equal(ErrorCodes.LongRest.ProviderUnreachable, await f.H.ScalarAsync("SELECT FailureCode FROM long_rest_provider_effects;"));

        await RetryAfterDrainedReopenAsync(f, "credential-retry");

        LongRestSweepProgress second = LongRestSweepHarness.Require(await ResumeAdmittedWithAsync(f.Coordinator, f.RecordedAdmission, f.Operation, CancellationToken.None));

        Assert.True(second.Phase == LongRestSweepPhase.Completed, second.DeferredReason);

        Assert.Equal(LongRunningOperationState.Completed, (await f.Operations.GetAsync(f.Operation))!.State);

        Assert.Equal(0L, await UnsettledEffectsAsync(f));

        // The paid summary was reused, not paid again; the embedding the first run could not send was asked for twice and sent once.
        Assert.Equal(1, f.Payloads.Count(static payload => payload.Kind == LongRestProviderPayloadKind.Summary));

        Assert.Equal(2, f.Payloads.Count(static payload => payload.Kind == LongRestProviderPayloadKind.Embedding));
    }

    private sealed class CredentialRefusals(int refuseResolution) : IProviderApiKeyResolver
    {
        internal int Resolutions;

        public Task<string?> ResolveAsync(ProviderSettings provider, CancellationToken cancellationToken = default)
        {
            if (++Resolutions == refuseResolution)
            {
                throw new InvalidOperationException("The credential store refused this resolution.");
            }

            return Task.FromResult<string?>(null);
        }
    }
}
