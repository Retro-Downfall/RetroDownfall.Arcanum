using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed partial class LongRestSweepStoreTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Attempt_transitions_require_current_durable_source_owner_metadata(bool protect, bool sent)
    {
        await using Fixture fixture = await Fixture.CreateAsync(protect, campaign: true);

        LongRestSweep sweep = await fixture.BeginAsync();

        LongRestSweepWork work = LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(sweep, fixture.Capability, CancellationToken.None))!;

        LongRestAttemptStore store = new(fixture.H.Saga.Context, TimeProvider.System);

        CovenantDigest digest = new(new byte[32]);

        LongRestDispatchAttempt attempt = LongRestSweepHarness.Require(await store.PrepareAttemptAsync(work.Identity, LongRestProviderPayloadKind.Adjudication, digest, digest, CancellationToken.None));

        if (sent)
        {
            if (protect)
            {
                attempt = LongRestSweepHarness.Require(await store.RecordDisclosureAsync(attempt, digest, CancellationToken.None));
            }

            attempt = LongRestSweepHarness.Require(await store.MarkSentAsync(attempt, CancellationToken.None));
        }

        using (CovenantSqliteAuthorizationScope authorization = CovenantSqliteConnectionInitializer.Instance.Authorize(fixture.H.Connection, CovenantSqliteAuthorizationKind.OwnerCleanup))
        {
            await fixture.H.ExecuteAsync("DELETE FROM session_campaign_bindings WHERE SessionId = $session;", ("$session", work.Inputs[0].Snapshot.SourceSessionId!.Value.ToString("D").ToUpperInvariant()));
        }

        long changes = (long)(await fixture.H.ScalarAsync("SELECT total_changes();"))!;

        Assert.True((await store.ValidateAttemptAsync(attempt, CancellationToken.None)).IsFailure);

        Assert.True((await store.PrepareAttemptAsync(work.Identity, LongRestProviderPayloadKind.Adjudication, digest, digest, CancellationToken.None)).IsFailure);

        if (sent)
        {
            Assert.True((await store.CompleteAttemptAsync(attempt, new(LongRestDispatchAttemptState.Accepted, new(1, 1, 2, 0, 0), 0.1m, null), CancellationToken.None)).IsFailure);
        }
        else
        {
            Assert.True((await store.RecordDisclosureAsync(attempt, digest, CancellationToken.None)).IsFailure);

            Assert.True((await store.MarkSentAsync(attempt, CancellationToken.None)).IsFailure);
        }

        Assert.Equal(changes, await fixture.H.ScalarAsync("SELECT total_changes();"));
    }
}
