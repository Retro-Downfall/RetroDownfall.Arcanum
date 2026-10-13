using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed partial class LongRestAttemptStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Claim_renewal_requires_exact_real_pending_claim_and_authoritative_scope(bool campaign)
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        Guid? campaignId = campaign ? Guid.NewGuid() : null;

        Guid session = await h.SessionAsync(campaignId);

        SessionTurnClaimLease lease = LongRestSweepHarness.Require(await new SessionTurnClaimStore(new FixedCovenantConnectionSource(h.Connection), new CovenantQuotaGuard(), Guid.NewGuid())
            .AcquireAsync(new(Guid.NewGuid(), 0, Guid.NewGuid(), session, SessionTurnSurface.Intelligence, Payload, Destination, null, 0, 0), CancellationToken.None));

        CanonicalCampaignContext scope = campaign ? CanonicalCampaignContext.Create(SessionCampaignBinding.ForCampaign(campaignId!.Value),
            (long)(await h.ScalarAsync("SELECT RegistryEpoch FROM campaign_registry_state WHERE StateKey = 1;"))!, 1, null, null) : CanonicalCampaignContext.GlobalOnly;

        LongRestAttemptStore store = new(h.Saga.Context, TimeProvider.System);

        SessionTurnClaimLease renewed = LongRestSweepHarness.Require(await store.RenewClaimAsync(lease, scope, CancellationToken.None));

        Assert.Equal(lease.Claim, renewed.Claim);

        Assert.Equal(lease.ExecutorId, renewed.ExecutorId);

        Assert.Equal(lease.OwnerBootId, renewed.OwnerBootId);

        Assert.True(renewed.LeaseDeadlineUtc >= lease.LeaseDeadlineUtc);

        foreach (SessionTurnClaimLease forged in new[]
        {
            lease with { ExecutorId = Guid.NewGuid() }, lease with { OwnerBootId = Guid.NewGuid() },
            lease with { Claim = lease.Claim with { RequestDigest = Destination } },
            lease with { Claim = lease.Claim with { DependencyDigest = Payload } },
            lease with { Claim = lease.Claim with { OriginInstallationId = Guid.NewGuid() } },
            lease with { Claim = lease.Claim with { OriginRestoreEpoch = 1 } },
            lease with { Claim = lease.Claim with { PreRequestHistoryRevision = 1 } },
            lease with { Claim = lease.Claim with { InputSensitivityRevision = 1 } },
            lease with { FutureAssistantEntryId = Guid.NewGuid() },
        })
        {
            Assert.True((await store.RenewClaimAsync(forged, scope, CancellationToken.None)).IsFailure);
        }

        Assert.True((await store.RenewClaimAsync(lease, default, CancellationToken.None)).IsFailure);

        Assert.True((await store.RenewClaimAsync(lease, campaign ? CanonicalCampaignContext.GlobalOnly : CanonicalCampaignContext.Create(SessionCampaignBinding.ForCampaign(Guid.NewGuid()), 1, 1, null, null), CancellationToken.None)).IsFailure);

        await h.ExecuteAsync("UPDATE session_turn_claims SET LeaseDeadlineUtc = '2000-01-01T00:00:00.0000000Z';");

        Assert.True((await store.RenewClaimAsync(lease, scope, CancellationToken.None)).IsFailure);

        Assert.Equal(0L, await h.ScalarAsync("SELECT count(*) FROM long_rest_provider_effects;"));
    }

    [Fact]
    public async Task Protected_attempt_requires_exact_disclosure_before_one_send()
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        Guid a = Guid.NewGuid();

        Guid b = Guid.NewGuid();

        await h.SeedAsync(a.ToString("D"), "first");

        await h.SeedAsync(b.ToString("D"), "second");

        GenerationProvenance provenance = GenerationProvenance.CreateExact([Guid.NewGuid()]);

        CovenantDigest partition = await h.ProtectAsync(a, provenance);

        await h.ProtectAsync(b, provenance);

        await using CovenantTurnLease authority = (await CovenantOperationGateFixture.CreateGate().AcquireTurnAsync(CanonicalCampaignContext.GlobalOnly, CancellationToken.None)).Value;

        await h.CaptureAsync(partition: partition, authority: authority);

        Guid generation = Guid.Parse((string)(await h.ScalarAsync("SELECT GenerationId FROM long_rest_discovery_generations;"))!);

        LongRestWorkIdentity work = LongRestSweepHarness.Require(await h.Store.ReadNextFoldAsync(generation, h.Capability, CancellationToken.None))!.Fold.Work;

        LongRestAttemptStore store = new(h.Saga.Context, TimeProvider.System);

        LongRestDispatchAttempt attempt = LongRestSweepHarness.Require(await store.PrepareAttemptAsync(work, LongRestProviderPayloadKind.Summary, Payload, Destination, CancellationToken.None));

        Assert.True((await store.MarkSentAsync(attempt, CancellationToken.None)).IsFailure);

        Assert.True((await store.RecordDisclosureAsync(attempt, default, CancellationToken.None)).IsFailure);

        LongRestDispatchAttempt disclosed = LongRestSweepHarness.Require(await store.RecordDisclosureAsync(attempt, Payload, CancellationToken.None));

        Assert.Equal(LongRestDispatchAttemptState.Disclosed, disclosed.State);

        Assert.Equal(Payload, disclosed.DisclosureReceiptDigest);

        Assert.True((await store.RecordDisclosureAsync(attempt, Destination, CancellationToken.None)).IsFailure);

        Assert.True((await store.MarkSentAsync(attempt, CancellationToken.None)).IsFailure);

        LongRestDispatchAttempt sent = LongRestSweepHarness.Require(await store.MarkSentAsync(disclosed, CancellationToken.None));

        Assert.True((await store.MarkSentAsync(disclosed, CancellationToken.None)).IsFailure);

        Assert.True((await store.RecordDisclosureAsync(sent, Destination, CancellationToken.None)).IsFailure);

        Assert.Equal(1L, await h.ScalarAsync("SELECT SentCount FROM long_rest_provider_effects;"));
    }
}
