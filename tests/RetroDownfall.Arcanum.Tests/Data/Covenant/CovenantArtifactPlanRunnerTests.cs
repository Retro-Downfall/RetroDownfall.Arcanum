using System.Data.Common;
using System.Globalization;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Lexicon;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// The one runner both artifact-erasure consumers share, in the mode that measures and the mode that
/// deletes.
/// </summary>
/// <remarks>
/// A plan is only as honest as the agreement between its two modes: a preflight that counted with one
/// predicate and an apply that deleted with another would describe a purge nobody ran. So every case
/// here counts, deletes, and counts again over the same rows, and a bystander row proves the predicate
/// reached no further than the one artifact it was bound to.
/// </remarks>
public sealed class CovenantArtifactPlanRunnerTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => CancellationToken.None;

    [SkippableFact]
    public async Task Count_measures_exactly_what_Delete_removes_for_a_saga_memory()
    {
        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        await harness.CreatePlainVectorMirrorAsync();

        harness.VectorAccelerator.SetAvailable(true);

        Guid target = Guid.NewGuid();

        await InsertAsync(harness, target, "the target memory");

        await InsertAsync(harness, Guid.NewGuid(), "an unrelated memory");

        Assert.Equal(2, await harness.CountAsync("saga_memory_embeddings_vec", "1 = 1"));

        // Off before the run: whether the mirror is purged is a property of the database, not the flag.
        harness.VectorAccelerator.SetAvailable(false);

        // One review event per claim, because every head insert fires annal_review_events_head_insert.
        IReadOnlyList<MemoryErasureTableCount> expected =
        [
            new("annal_review_decision_receipts", 0), new("annal_review_events", 1), new("annal_dependencies", 0),
            new("annal_heads", 1), new("annal_versions", 1), new("annal_claims", 1),
            new("saga_memory_embeddings", 1), new("saga_memory_embeddings_vec", 1),
            new("saga_memory_attachment_provenance", 0), new("saga_memories", 1),
        ];

        CovenantArtifactPlanTally counted = await RunAsync(
            harness.Connection,
            SensitiveArtifactKind.Saga,
            target,
            CovenantArtifactPlanMode.Count);

        Assert.Equal(expected, counted.Targets);

        Assert.Equal(SagaVectorMirrorKind.PlainTable, counted.VectorMirror);

        // The artifact is last, and only there.
        Assert.Equal("saga_memories", counted.Targets[^1].Table);

        Assert.Equal(counted.Targets[^1].Rows, counted.ArtifactRows);

        Assert.Equal(1L, counted.ArtifactRows);

        Assert.Equal(
            expected,
            (await RunAsync(harness.Connection, SensitiveArtifactKind.Saga, target, CovenantArtifactPlanMode.Delete)).Targets);

        Assert.All(
            (await RunAsync(harness.Connection, SensitiveArtifactKind.Saga, target, CovenantArtifactPlanMode.Count)).Targets,
            static remaining => Assert.Equal(0L, remaining.Rows));

        // The unrelated memory survives, with its claim.
        Assert.Equal(1, await harness.CountAsync("saga_memories", "1 = 1"));

        Assert.Equal(1, await harness.CountAsync("annal_claims", "1 = 1"));
    }

    [SkippableFact]
    public async Task Count_measures_exactly_what_Delete_removes_for_a_lexicon_entry()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using GrimoireFixture grimoire = new();

        await using CorrectionFixture lexicon = new(grimoire, annals: true);

        LexiconEntryDetail seeded = await lexicon.SeedAsync();

        // The only entry in the database, so each table's whole count is what the entry owns.
        List<MemoryErasureTableCount> expected = [];

        foreach (string table in new[]
                 {
                     "lexicon_annal_fact_provenance", "annal_review_decision_receipts", "annal_review_events",
                     "annal_dependencies", "annal_heads", "annal_versions", "annal_claims",
                     "lexicon_fact_attachment_provenance", "lexicon_entries",
                 })
        {
            expected.Add(new(table, Convert.ToInt64(await lexicon.ScalarAsync($"SELECT count(*) FROM {table};"), CultureInfo.InvariantCulture)));
        }

        CovenantArtifactPlanTally counted = await RunAsync(
            lexicon.Connection,
            SensitiveArtifactKind.Lexicon,
            seeded.Entry.Id,
            CovenantArtifactPlanMode.Count);

        Assert.Equal(expected, counted.Targets);

        Assert.Equal(SagaVectorMirrorKind.Absent, counted.VectorMirror);

        Assert.Equal(new MemoryErasureTableCount("lexicon_entries", 1), counted.Targets[^1]);

        Assert.Equal(1L, counted.ArtifactRows);

        Assert.Equal(
            expected,
            (await RunAsync(lexicon.Connection, SensitiveArtifactKind.Lexicon, seeded.Entry.Id, CovenantArtifactPlanMode.Delete)).Targets);

        Assert.All(
            (await RunAsync(lexicon.Connection, SensitiveArtifactKind.Lexicon, seeded.Entry.Id, CovenantArtifactPlanMode.Count)).Targets,
            static remaining => Assert.Equal(0L, remaining.Rows));
    }

    [SkippableFact]
    public async Task A_legacy_virtual_mirror_is_skipped_counted_as_zero_and_reported()
    {
        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        await harness.CreateLegacyVectorMirrorAsync();

        Guid target = Guid.NewGuid();

        await InsertAsync(harness, target, "the target memory");

        // Nothing in this build can write into a virtual mirror, so its row is seeded as residue.
        await harness.SeedVectorMirrorRowAsync(target.ToString());

        CovenantArtifactPlanTally deleted = await RunAsync(
            harness.Connection,
            SensitiveArtifactKind.Saga,
            target,
            CovenantArtifactPlanMode.Delete);

        Assert.Equal(SagaVectorMirrorKind.LegacyVirtualTable, deleted.VectorMirror);

        Assert.Contains(new MemoryErasureTableCount("saga_memory_embeddings_vec", 0), deleted.Targets);

        Assert.Equal(1L, deleted.ArtifactRows);

        // Unreachable residue, reported rather than opened.
        Assert.Equal(1, await harness.CountAsync("saga_memory_embeddings_vec", "1 = 1"));

        Assert.Equal(0, await harness.CountAsync("saga_memories", "1 = 1"));
    }

    [Fact]
    public void Every_materialized_plan_has_at_most_one_conditional_mirror_and_never_mixes_projections_with_a_pointer_or_redaction()
    {
        Assert.All(
            CovenantArtifactPurgePlans.MaterializedKinds,
            static kind =>
            {
                CovenantArtifactPurgePlan plan = CovenantArtifactPurgePlans.Resolve(kind);

                // A tally reports one mirror classification, so a plan may carry at most one mirror.
                Assert.True(
                    plan.Projections.Count(static target => target.ExistsConditionally) <= 1,
                    $"{kind} has more than one conditional mirror.");

                // The consumers run the pointer and redaction before the runner's projections, which is
                // only order-neutral while no plan has both.
                Assert.False(
                    plan.Projections.Count > 0 && (plan.CurrentPointerTable is not null || plan.RedactionSql is not null),
                    $"{kind} mixes projections with a current pointer or a redaction.");

                // A table can appear in a tally only once.
                string[] tables =
                [
                    .. plan.Projections.Select(static target => target.Table),
                    .. plan.Artifact is { } artifact ? new[] { artifact.Table } : [],
                ];

                Assert.Equal(tables.Length, tables.Distinct(StringComparer.Ordinal).Count());
            });
    }

    private static async Task InsertAsync(SagaStoreHarness harness, Guid id, string content)
    {
        SagaMemoryWriteOutcome outcome = await harness.Store.InsertAsync(
            id.ToString(),
            content,
            CreatedAt,
            sessionId: null,
            tags: null,
            source: "saga-extraction",
            harness.Embedding(content.Length),
            Token);

        Assert.Equal(SagaMemoryWriteOutcome.Written, outcome);
    }

    private static async Task<CovenantArtifactPlanTally> RunAsync(
        DbConnection connection,
        SensitiveArtifactKind kind,
        Guid artifactId,
        CovenantArtifactPlanMode mode)
    {
        SqliteConnection sqlite = (SqliteConnection)connection;

        await using SqliteTransaction transaction = sqlite.BeginTransaction();

        CovenantArtifactPlanTally tally = await CovenantArtifactPlanRunner.RunAsync(
            sqlite,
            transaction,
            kind,
            CovenantIdentitySql.Key(artifactId),
            mode,
            Token);

        await transaction.CommitAsync(Token);

        return tally;
    }
}
