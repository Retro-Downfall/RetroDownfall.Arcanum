using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed class LongRestDiscoveryPartitionTests
{
    [SkippableTheory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task Exact_scope_and_protection_filter_before_candidate_ranking(bool campaignBound, bool protectedPartition, bool accelerated)
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        h.Saga.VectorAccelerator.SetAvailable(accelerated);

        Guid campaign = Guid.NewGuid();

        Guid ownSession = await h.SessionAsync(campaignBound ? campaign : null);

        Guid otherSession = await h.SessionAsync(Guid.NewGuid());

        Guid unresolved = await h.SessionAsync(binding: 3);

        string anchor = Guid.NewGuid().ToString("D");

        string eligible = Guid.NewGuid().ToString("D");

        await h.SeedAsync(anchor, "opal anchor", ownSession);

        await h.SeedAsync(eligible, "opal eligible", ownSession);

        for (int index = 0; index < 10; index++)
        {
            await h.SeedAsync(Guid.NewGuid().ToString("D"), "opal tempting other " + index, otherSession);

            await h.SeedAsync(Guid.NewGuid().ToString("D"), "opal tempting unresolved " + index, unresolved);

            if (campaignBound)
            {
                await h.SeedAsync(Guid.NewGuid().ToString("D"), "opal tempting global " + index);
            }
        }

        GenerationProvenance provenance = GenerationProvenance.CreateExact([Guid.NewGuid()]);

        string protectedOther = Guid.NewGuid().ToString("D");

        await h.SeedAsync(protectedOther, "opal tempting differently protected", ownSession);

        await h.ProtectAsync(Guid.Parse(protectedOther), GenerationProvenance.CreateExact([Guid.NewGuid()]));

        List<string> excluded = [];

        for (int index = 0; index < 12; index++)
        {
            string memory = Guid.NewGuid().ToString("D");

            await h.SeedAsync(memory, "opal tempting inactive " + index, ownSession);

            await h.ExecuteAsync(index < 6
                ? "UPDATE saga_memories SET PinnedAtUtc = '2026-10-10T00:00:00.0000000Z' WHERE Id = $id;"
                : "UPDATE saga_memories SET RetiredAtUtc = '2026-10-10T00:00:00.0000000Z' WHERE Id = $id;", ("$id", memory));

            excluded.Add(memory);
        }

        string survivor = "00000000-0000-4000-8000-000000000001";

        await h.SeedAsync(survivor, "opal previously consolidated", ownSession);

        List<LongRestTarget> consolidation = [await h.TargetAsync(survivor)];

        for (int index = 0; index < 10; index++)
        {
            string memory = Guid.NewGuid().ToString("D");

            await h.SeedAsync(memory, "opal previously consolidated " + index, ownSession);

            excluded.Add(memory);

            consolidation.Add(await h.TargetAsync(memory));
        }

        var receipt = LongRestSweepHarness.Require(await new SagaLongRestService(h.Saga.Context, TimeProvider.System).ApplyAsync(
            new(LongRestTransformationKind.EquivalentObservations, [.. consolidation]), CancellationToken.None));

        Assert.Equal(LongRestOutcome.Applied, receipt.Outcome);

        await h.ExecuteAsync("UPDATE saga_memories SET PinnedAtUtc = '2026-10-10T00:00:00.0000000Z' WHERE Id = $id;", ("$id", survivor));

        excluded.Add(survivor);

        CovenantDigest? partition = null;

        if (protectedPartition)
        {
            partition = await h.ProtectAsync(Guid.Parse(anchor), provenance);

            await h.ProtectAsync(Guid.Parse(eligible), provenance);

            foreach (string memory in excluded)
            {
                await h.ProtectAsync(Guid.Parse(memory), provenance);
            }
        }

        CanonicalCampaignContext canonical = campaignBound
            ? CanonicalCampaignContext.Create(SessionCampaignBinding.ForCampaign(campaign), 1, 1, 1, new CovenantDigest(new byte[32]))
            : CanonicalCampaignContext.GlobalOnly;

        await using CovenantTurnLease authority = (await CovenantOperationGateFixture.CreateGate().AcquireTurnAsync(canonical, CancellationToken.None)).Value;

        LongRestDiscoveryGeneration generation = await h.CaptureAsync(campaignBound ? campaign : null, partition, protectedPartition ? authority : null);

        await h.BuildAsync(generation);

        LongRestCandidateGroup[] groups = LongRestSweepHarness.Require(await h.Store.DiscoverAsync(generation.GenerationId, await h.TargetAsync(anchor), h.Capability, CancellationToken.None));

        Assert.Equal([eligible], groups.SelectMany(group => group.Evidence).Select(candidate => candidate.Target.MemoryId));

        if (protectedPartition)
        {
            Assert.True((await h.Store.DiscoverAsync(generation.GenerationId, await h.TargetAsync(anchor), null, CancellationToken.None)).IsFailure);

            Assert.True((long)(await h.ScalarAsync("SELECT count(*) FROM artifact_sensitivity WHERE ArtifactKindCode = 16;"))! > 0);
        }
    }

    [SkippableTheory]
    [InlineData("missing-binding")]
    [InlineData("missing-session")]
    [InlineData("annals-owner")]
    public async Task Stale_session_owners_are_not_captured_as_global_candidates(string corruption)
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        Guid staleSession = await h.SessionAsync();

        await h.SeedAsync("a-stale", "opal tempting", staleSession);

        await h.SeedAsync("anchor", "opal anchor");

        await h.SeedAsync("z-eligible", "opal eligible");

        if (corruption == "annals-owner")
        {
            Guid replacement = await h.SessionAsync();

            await h.ExecuteAsync("UPDATE saga_memories SET SessionId = $session WHERE Id = 'a-stale';", ("$session", replacement.ToString("D").ToUpperInvariant()));
        }
        else
        {
            using CovenantSqliteAuthorizationScope scope = CovenantSqliteConnectionInitializer.Instance.Authorize(h.Connection, CovenantSqliteAuthorizationKind.SessionRetention);

            await h.ExecuteAsync("PRAGMA foreign_keys = OFF;");

            string table = corruption == "missing-session" ? "Sessions" : "session_campaign_bindings";

            string key = corruption == "missing-session" ? "Id" : "SessionId";

            await h.ExecuteAsync($"DELETE FROM {table} WHERE {key} = $id;", ("$id", staleSession.ToString("D").ToUpperInvariant()));
        }

        LongRestDiscoveryGeneration generation = await h.CaptureAsync();

        Assert.Equal(2L, await h.ScalarAsync("SELECT count(*) FROM long_rest_discovery_leaves;"));

        await h.BuildAsync(generation);

        LongRestCandidateGroup[] groups = LongRestSweepHarness.Require(await h.Store.DiscoverAsync(generation.GenerationId, await h.TargetAsync("anchor"), null, CancellationToken.None));

        Assert.Equal(["z-eligible"], groups.SelectMany(group => group.Evidence).Select(candidate => candidate.Target.MemoryId));
    }

    [SkippableFact]
    public async Task A_frozen_owner_losing_its_binding_refuses_fold_reuse()
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        Guid session = await h.SessionAsync();

        await h.SeedAsync("anchor", "opal anchor", session);

        await h.SeedAsync("other", "opal other", session);

        LongRestDiscoveryGeneration generation = await h.CaptureAsync();

        Assert.NotNull(LongRestSweepHarness.Require(await h.Store.ReadNextFoldAsync(generation.GenerationId, null, CancellationToken.None)));

        using (CovenantSqliteAuthorizationScope scope = CovenantSqliteConnectionInitializer.Instance.Authorize(h.Connection, CovenantSqliteAuthorizationKind.SessionRetention))
        {
            await h.ExecuteAsync("DELETE FROM session_campaign_bindings WHERE SessionId = $id;", ("$id", session.ToString("D").ToUpperInvariant()));
        }

        Assert.True((await h.Store.ReadNextFoldAsync(generation.GenerationId, null, CancellationToken.None)).IsFailure);
    }
}
