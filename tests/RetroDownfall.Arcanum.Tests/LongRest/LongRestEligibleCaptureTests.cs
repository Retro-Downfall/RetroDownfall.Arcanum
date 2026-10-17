using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data.Annals;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

// Capture freezes the sources a sweep could ever use. A retired, pinned or suppressed memory can be neither an anchor nor a neighbour, so
// it is not a leaf: it is not folded, summarized, embedded or sent to a provider, and it costs no page of the corpus.
public sealed class LongRestEligibleCaptureTests
{
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Capture_freezes_only_the_sources_a_sweep_could_use(bool protectedPartition)
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        string[] memories = [.. Enumerable.Range(0, 7).Select(static index => $"00000000-0000-4000-8000-{index + 1:D12}")];

        GenerationProvenance provenance = GenerationProvenance.CreateExact([Guid.NewGuid()]);

        for (int index = 0; index < memories.Length; index++)
        {
            await h.SeedAsync(memories[index], $"opal observation {index}");
        }

        // 0 stays eligible; 1 is retired; 2 is pinned; 3 and 4 are consolidated, with 3 the survivor that stays eligible; 5 stays eligible;
        // 6 is retired as well.
        await h.Saga.Store.RetireAsync(memories[1], AnnalContentDigest.ForSagaMemory("opal observation 1"), DateTimeOffset.UtcNow, CancellationToken.None);

        await h.Saga.Store.SetPinAsync(memories[2], true, DateTimeOffset.UtcNow, CancellationToken.None);

        await h.Saga.Store.RetireAsync(memories[6], AnnalContentDigest.ForSagaMemory("opal observation 6"), DateTimeOffset.UtcNow, CancellationToken.None);

        LongRestReceipt receipt = LongRestSweepHarness.Require(await new SagaLongRestService(h.Saga.Context, TimeProvider.System).ApplyAsync(
            new(LongRestTransformationKind.EquivalentObservations, [await h.TargetAsync(memories[3]), await h.TargetAsync(memories[4])]), CancellationToken.None));

        Assert.Equal(LongRestOutcome.Applied, receipt.Outcome);

        Assert.Equal(1L, await h.ScalarAsync("SELECT count(*) FROM long_rest_suppressions;"));

        CovenantDigest? partition = null;

        if (protectedPartition)
        {
            foreach (string memory in memories)
            {
                partition = await h.ProtectAsync(Guid.Parse(memory), provenance);
            }
        }

        await using CovenantTurnLease authority = (await CovenantOperationGateFixture.CreateGate().AcquireTurnAsync(CanonicalCampaignContext.GlobalOnly, CancellationToken.None)).Value;

        _ = await h.CaptureAsync(null, partition, protectedPartition ? authority : null);

        string[] leaves = await LeavesAsync(h);

        Assert.Equal([memories[0], memories[3], memories[5]], leaves);

        // The leaf nodes, the lexical index and the edges' endpoints cover exactly those leaves and no others.
        Assert.Equal(3L, await h.ScalarAsync("SELECT count(*) FROM long_rest_discovery_nodes WHERE Layer = 0;"));

        Assert.Equal(3L, await h.ScalarAsync("SELECT count(*) FROM " + (protectedPartition ? "long_rest_protected_fts" : "long_rest_clean_fts") + ";"));
    }

    // An operator's correction (and a reinstatement) is stated by no Session: the Annals version it appends names none, and the schema lets an
    // operator-stated version say so. The memory still belongs to its Session, which its row and the Session's immutable binding prove, so the
    // correction does not take the memory out of the sweeps: the operator's statement is the strongest version there is, and the sweep exists to
    // fold what repeats it.
    [SkippableFact]
    public async Task An_operator_corrected_memory_of_a_Session_stays_a_source_and_a_candidate()
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        Guid session = await h.SessionAsync();

        await h.SeedAsync("a-corrected", "opal before the correction", session);

        await h.SeedAsync("b-untouched", "opal untouched", session);

        await h.SeedAsync("c-reinstated", "opal reinstated", session);

        await h.SeedAsync("d-global", "opal global");

        SagaCurationOutcome corrected = await h.Saga.Store.CorrectAsync("a-corrected", AnnalContentDigest.ForSagaMemory("opal before the correction"),
            "opal after the correction", h.Saga.Embedding(), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(SagaCurationOutcomeKind.Applied, corrected.Kind);

        Assert.Equal(SagaCurationOutcomeKind.Applied, (await h.Saga.Store.RetireAsync("c-reinstated", AnnalContentDigest.ForSagaMemory("opal reinstated"),
            DateTimeOffset.UtcNow, CancellationToken.None)).Kind);

        Assert.Equal(SagaCurationOutcomeKind.Applied, (await h.Saga.Store.ReinstateAsync("c-reinstated", AnnalContentDigest.ForSagaMemory("opal reinstated"),
            h.Saga.Embedding(), DateTimeOffset.UtcNow, CancellationToken.None)).Kind);

        // The preconditions: the current version of each of the two is the operator's and names no Session, and the memory still has its owner.
        Assert.Equal(2L, await h.ScalarAsync("""
            SELECT count(*) FROM saga_memories memory JOIN annal_claims claim ON claim.SubjectStoreCode = 1 AND claim.SubjectId = memory.Id
            JOIN annal_heads head ON head.ClaimId = claim.ClaimId JOIN annal_versions version ON version.VersionId = head.CurrentVersionId
            WHERE memory.Id IN ('a-corrected', 'c-reinstated') AND memory.SessionId IS NOT NULL AND version.OriginCode = 1 AND version.SourceSessionId IS NULL;
            """));

        LongRestDiscoveryGeneration generation = await h.CaptureAsync();

        Assert.Equal(["a-corrected", "b-untouched", "c-reinstated", "d-global"], await LeavesAsync(h));

        // The corrected memory is also a candidate beside its Session's other memories, and the frozen corpus re-proves it as it re-proves the rest.
        await h.BuildAsync(generation);

        Assert.Equal(["b-untouched", "c-reinstated", "d-global"], LongRestSweepHarness.Require(await h.Store.DiscoverAsync(generation.GenerationId,
            await h.TargetAsync("a-corrected"), null, CancellationToken.None)).SelectMany(static group => group.Evidence).Select(static candidate => candidate.Target.MemoryId).Order().ToArray());
    }

    // The relaxation is only for the operator's own version of a memory without a sensitivity label. An agent's version that names no Session is
    // a version whose owner cannot be proved, and it stays out.
    [SkippableFact]
    public async Task A_version_an_agent_wrote_without_the_owning_Session_is_not_a_source()
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        Guid session = await h.SessionAsync();

        await h.SeedAsync("a-subject", "opal subject", session);

        await h.SeedAsync("b-other", "opal other");

        // The next revision of the claim is an agent's, and names no Session.
        await using (Microsoft.Data.Sqlite.SqliteTransaction transaction = h.Connection.BeginTransaction())
        {
            Assert.NotNull(await AnnalsClaimWriter.AppendCorrectionAsync(h.Connection, transaction, AnnalSubjectStore.Saga, "a-subject", AnnalOrigin.AgentAsserted,
                SagaMemoryScopeKind.Global, null, ContentSensitivity.None, AnnalContentHashFormat.LegacyStoreDigest, AnnalContentDigest.ForSagaMemory("opal restated"),
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, CancellationToken.None, legacySchema: true));

            await transaction.CommitAsync();
        }

        _ = await h.CaptureAsync();

        Assert.Equal(["b-other"], await LeavesAsync(h));
    }

    // A labelled memory keeps the exact match in a protected partition: its label names the Session and the revision the label proves, and the
    // operator's later text is not what the label covers, so the corrected memory is left out of the partition instead of failing its capture.
    [SkippableFact]
    public async Task A_corrected_memory_in_a_protected_partition_is_left_out_and_does_not_fail_the_capture()
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        Guid session = await h.SessionAsync();

        Guid kept = Guid.Parse("00000000-0000-4000-8000-000000000001");

        Guid corrected = Guid.Parse("00000000-0000-4000-8000-000000000002");

        await h.SeedAsync(kept.ToString("D"), "opal kept", session);

        await h.SeedAsync(corrected.ToString("D"), "opal before", session);

        Assert.Equal(SagaCurationOutcomeKind.Applied, (await h.Saga.Store.CorrectAsync(corrected.ToString("D"), AnnalContentDigest.ForSagaMemory("opal before"),
            "opal after", h.Saga.Embedding(), DateTimeOffset.UtcNow, CancellationToken.None)).Kind);

        GenerationProvenance provenance = GenerationProvenance.CreateExact([Guid.NewGuid()]);

        CovenantDigest partition = await h.ProtectAsync(kept, provenance);

        Assert.Equal(partition, await h.ProtectAsync(corrected, provenance));

        await using CovenantTurnLease authority = (await CovenantOperationGateFixture.CreateGate().AcquireTurnAsync(CanonicalCampaignContext.GlobalOnly, CancellationToken.None)).Value;

        _ = await h.CaptureAsync(null, partition, authority);

        Assert.Equal([kept.ToString("D")], await LeavesAsync(h));
    }

    [SkippableFact]
    public async Task Capture_pages_past_ineligible_sources_without_spending_pages_on_them()
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        const int sources = 140;

        for (int index = 0; index < sources; index++)
        {
            await h.SeedAsync($"00000000-0000-4000-8000-{index + 1:D12}", $"observation {index}");
        }

        // The last twenty memory ids are retired, so a page of 128 holds every eligible source with room to spare.
        for (int index = sources - 20; index < sources; index++)
        {
            await h.Saga.Store.RetireAsync($"00000000-0000-4000-8000-{index + 1:D12}", AnnalContentDigest.ForSagaMemory($"observation {index}"), DateTimeOffset.UtcNow, CancellationToken.None);
        }

        LongRestDiscoveryGeneration generation = await h.StartAsync();

        LongRestSourcePage page = LongRestSweepHarness.Require(await h.Store.CaptureNextSourcePageAsync(generation.GenerationId, generation.Revision, null, CancellationToken.None));

        Assert.Equal(sources - 20, page.Leaves.Length);

        Assert.False(page.HasMore);
    }

    private static async Task<string[]> LeavesAsync(LongRestSweepHarness h)
    {
        List<string> leaves = [];

        await using Microsoft.Data.Sqlite.SqliteCommand command = h.Connection.CreateCommand();

        command.CommandText = "SELECT MemoryId FROM long_rest_discovery_leaves ORDER BY MemoryId;";

        await using Microsoft.Data.Sqlite.SqliteDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            leaves.Add(reader.GetString(0));
        }

        return [.. leaves];
    }
}
