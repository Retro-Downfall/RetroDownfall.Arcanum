using System.Data.Common;
using System.Globalization;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Lexicon;
using RetroDownfall.Arcanum.Tests.Support;

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
    private static IReadOnlyList<MemoryErasureTableCount> EmptyEvaluationAndLongRestGenerationTargets(long sourceRows) =>
    [
        new("memory_evaluation_reports", 0), new("memory_evaluation_sources", 0), new("memory_evaluations", 0),
        new("long_rest_discovery_generations", 0), new("long_rest_clean_fts", 0), new("long_rest_protected_fts", 0),
        new("long_rest_discovery_embeddings", 0), new("long_rest_discovery_edges", 0),
        new("long_rest_accepted_summaries", 0), new("long_rest_adjudications", 0),
        new("long_rest_provider_effects", 0), new("long_rest_work_inputs", 0), new("long_rest_work_items", 0),
        new("long_rest_discovery_nodes", 0), new("long_rest_discovery_leaves", 0), new("long_rest_sweeps", 0),
        new("long_rest_sources", sourceRows),
    ];

    private const string GatedTablesPresent =
        "SELECT count(*) FROM sqlite_master WHERE name IN ('annal_review_events', 'annal_review_decision_receipts', 'long_rest_discovery_generations', 'long_rest_provider_effects', 'long_rest_clean_fts', 'long_rest_protected_fts', 'long_rest_sources', 'long_rest_protected_receipt_inputs', 'long_rest_protected_receipt_evidence', 'saga_committed_uses');";

    private static readonly DateTimeOffset CreatedAt = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    static CovenantArtifactPlanRunnerTests() => SqliteNativeRuntime.Instance.Initialize();

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
            .. EmptyEvaluationAndLongRestGenerationTargets(sourceRows: 1),
            new("long_rest_protected_receipt_inputs", 0), new("long_rest_protected_receipt_evidence", 0),
            new("long_rest_receipts", 0), new("long_rest_suppressions", 0), new("long_rest_receipt_inputs", 0),
            new("annal_review_decision_receipts", 0), new("annal_review_events", 1), new("annal_dependencies", 0),
            new("annal_heads", 1), new("saga_committed_uses", 0), new("annal_versions", 1), new("annal_claims", 1),
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
    public async Task Saga_erasure_counts_and_removes_only_its_native_committed_use_without_foreign_key_cascades()
    {
        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        Guid target = Guid.NewGuid();

        Guid bystander = Guid.NewGuid();

        await InsertAsync(harness, target, "the target memory");

        await InsertAsync(harness, bystander, "an unrelated memory");

        SagaUseCoordinate targetUse = await CoordinateAsync(harness, target);

        SagaUseCoordinate bystanderUse = await CoordinateAsync(harness, bystander);

        Guid sessionId = Guid.NewGuid();

        Guid assistantEntryId = Guid.NewGuid();

        DateTimeOffset now = DateTimeOffset.UtcNow;

        _ = harness.Context.Sessions.Add(new Session
        {
            Id = sessionId,
            CreatedAt = now,
            UpdatedAt = now,
            Status = "active",
            Title = "committed-use erasure",
            UnsummarizedEntryCount = 2,
        });

        _ = harness.Context.Entries.Add(new Entry
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            Role = MessageRole.User,
            Content = "hello",
            ModelUsed = "test-model",
            CreatedAt = now,
            Sequence = 1L,
        });

        _ = harness.Context.Entries.Add(new Entry
        {
            Id = assistantEntryId,
            SessionId = sessionId,
            Role = MessageRole.Assistant,
            Content = string.Empty,
            ModelUsed = "test-model",
            CreatedAt = now,
            Sequence = 2L,
        });

        _ = await harness.Context.SaveChangesAsync(Token);

        IGrimoireTurnCommitter committer = new GrimoireRepository(
            harness.Context,
            new NoOpSessionAttachmentStore(),
            NullLogger<GrimoireRepository>.Instance,
            new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings()),
            covenantKernel: null,
            availabilityRepublisher: null,
            FixtureOrdinaryConnectionFactory.For(harness.Context),
            FixtureLabeledArtifactGuard.For(harness.Context));

        Result<TurnCommitReceipt> committed = await committer.CommitTurnAsync(
            new TurnCommitRequest(
                assistantEntryId,
                sessionId,
                AssistantFinalizationOutcome.Committed,
                "answer",
                new CovenantDigest(new byte[32]),
                ContentSensitivity.None,
                GenerationProvenance.CreateExact([]),
                sagaUses: [targetUse, bystanderUse]),
            Token);

        Assert.True(committed.IsSuccess, committed.Error.Message);

        Assert.False(committed.Value.Replayed);

        Assert.Equal(2, await harness.CountAsync("saga_committed_uses", "1 = 1"));

        SqliteConnection connection = (SqliteConnection)harness.Connection;

        await using (SqliteCommand disableCascades = connection.CreateCommand())
        {
            disableCascades.CommandText = "PRAGMA foreign_keys = OFF";

            _ = await disableCascades.ExecuteNonQueryAsync(Token);
        }

        Assert.Equal(0L, await ScalarAsync(connection, "PRAGMA foreign_keys"));

        CovenantArtifactPlanTally counted = await RunAsync(connection, SensitiveArtifactKind.Saga, target, CovenantArtifactPlanMode.Count);

        Assert.Equal(1L, Assert.Single(counted.Targets, static row => row.Table == "saga_committed_uses").Rows);

        CovenantArtifactPlanTally deleted = await RunAsync(connection, SensitiveArtifactKind.Saga, target, CovenantArtifactPlanMode.Delete);

        Assert.Equal(counted.Targets, deleted.Targets);

        Assert.All(
            (await RunAsync(connection, SensitiveArtifactKind.Saga, target, CovenantArtifactPlanMode.Count)).Targets,
            static remaining => Assert.Equal(0L, remaining.Rows));

        Assert.Equal(0L, await ScalarAsync(connection, "SELECT count(*) FROM saga_committed_uses WHERE MemoryId = $memory", ("$memory", target.ToString())));

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT count(*) FROM saga_committed_uses WHERE MemoryId = $memory", ("$memory", bystander.ToString())));

        Assert.Equal(1, await harness.CountAsync("saga_committed_uses", "1 = 1"));

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT count(*) FROM assistant_entry_finalizations WHERE AssistantEntryId = $assistant", ("$assistant", assistantEntryId.ToString("D").ToUpperInvariant())));

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT count(*) FROM Entries WHERE Id = $assistant AND Content = 'answer'", ("$assistant", assistantEntryId.ToString("D").ToUpperInvariant())));
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
                     "memory_evaluation_reports", "memory_evaluation_sources", "memory_evaluations",
                     "long_rest_protected_receipt_inputs", "long_rest_protected_receipt_evidence",
                     "long_rest_receipts", "long_rest_suppressions", "long_rest_receipt_inputs",
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

        // The whole ordered tally, so a skipped mirror that was duplicated or moved would show.
        IReadOnlyList<MemoryErasureTableCount> expected =
        [
            .. EmptyEvaluationAndLongRestGenerationTargets(sourceRows: 1),
            new("long_rest_protected_receipt_inputs", 0), new("long_rest_protected_receipt_evidence", 0),
            new("long_rest_receipts", 0), new("long_rest_suppressions", 0), new("long_rest_receipt_inputs", 0),
            new("annal_review_decision_receipts", 0), new("annal_review_events", 1), new("annal_dependencies", 0),
            new("annal_heads", 1), new("saga_committed_uses", 0), new("annal_versions", 1), new("annal_claims", 1),
            new("saga_memory_embeddings", 1), new("saga_memory_embeddings_vec", 0),
            new("saga_memory_attachment_provenance", 0), new("saga_memories", 1),
        ];

        Assert.Equal(expected, deleted.Targets);

        Assert.Equal(1L, deleted.ArtifactRows);

        // Unreachable residue, reported rather than opened.
        Assert.Equal(1, await harness.CountAsync("saga_memory_embeddings_vec", "1 = 1"));

        Assert.Equal(0, await harness.CountAsync("saga_memories", "1 = 1"));
    }

    /// <summary>
    /// A target whose table arrives after the committed Core version is listed at zero in its plan
    /// position, and no statement is issued against it.
    /// </summary>
    /// <remarks>
    /// A genuine Core 11 database, built by the version-11 chain, so the two review tables version 12
    /// adds are absent rather than emptied. A statement against either would fail on the missing table,
    /// which is what proves the gate issued none, and the tally still names both, because a plan's
    /// target list has one shape whatever version it runs against.
    /// </remarks>
    [Fact]
    public async Task A_target_gated_above_the_committed_Core_version_is_listed_at_zero_and_never_touched()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(Token);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, CoreSchemaVersionElevenFixture.ChainSet(), 64, Token);

        Assert.Equal(11, await GrimoireCoreSchemaVersion.ReadAsync(connection, Token));

        Assert.Equal(0L, await ScalarAsync(connection, GatedTablesPresent));

        await using ArcanumDbContext db = SagaMemoryMidUpgradeWriteTests.CreateContext(file);

        SagaMemoryStore store = new(
            db,
            new WeaveIndexAvailability(),
            new TestOptionsMonitor<ArcanumSettings>(
                new ArcanumSettings
                {
                    Features = new FeatureSettings { Annals = true },
                    Integrations = new IntegrationSettings
                    {
                        Embeddings = new EmbeddingIntegrationSettings { Dimensions = 64 },
                    },
                }),
            MemoryErasureTestKeys.Isolated());

        Guid target = Guid.NewGuid();

        foreach ((Guid id, string content) in new[] { (target, "the target memory"), (Guid.NewGuid(), "an unrelated memory") })
        {
            Assert.Equal(
                SagaMemoryWriteOutcome.Written,
                await store.InsertAsync(id.ToString(), content, CreatedAt, null, null, "saga-extraction", new float[64], Token));
        }

        IReadOnlyList<MemoryErasureTableCount> expected =
        [
            .. EmptyEvaluationAndLongRestGenerationTargets(sourceRows: 0),
            new("long_rest_protected_receipt_inputs", 0), new("long_rest_protected_receipt_evidence", 0),
            new("long_rest_receipts", 0), new("long_rest_suppressions", 0), new("long_rest_receipt_inputs", 0),
            new("annal_review_decision_receipts", 0), new("annal_review_events", 0), new("annal_dependencies", 0),
            new("annal_heads", 1), new("saga_committed_uses", 0), new("annal_versions", 1), new("annal_claims", 1),
            new("saga_memory_embeddings", 1), new("saga_memory_embeddings_vec", 0),
            new("saga_memory_attachment_provenance", 0), new("saga_memories", 1),
        ];

        Assert.Equal(expected, (await RunAsync(connection, SensitiveArtifactKind.Saga, target, CovenantArtifactPlanMode.Count)).Targets);

        Assert.Equal(expected, (await RunAsync(connection, SensitiveArtifactKind.Saga, target, CovenantArtifactPlanMode.Delete)).Targets);

        // The gated tables are still absent, and the unrelated memory and its claim survive.
        Assert.Equal(0L, await ScalarAsync(connection, GatedTablesPresent));

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT count(*) FROM saga_memories;"));

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT count(*) FROM annal_claims;"));
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

    private static async Task<SagaUseCoordinate> CoordinateAsync(SagaStoreHarness harness, Guid id)
    {
        AnnalClaimHead claim = Assert.IsType<AnnalClaimHead>(await harness.Annals.GetClaimAsync(AnnalSubjectStore.Saga, id.ToString(), Token));

        AnnalClaimVersion version = Assert.Single(await harness.Annals.GetVersionsAsync(claim.ClaimId, Token));

        Assert.Equal(claim.CurrentVersionId, version.VersionId);

        return new SagaUseCoordinate(id.ToString(), claim.ClaimId, version.VersionId, (int)version.ContentHashFormat, new CovenantDigest(Assert.IsType<byte[]>(version.ContentHash)));
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        foreach ((string name, object value) in parameters)
        {
            _ = command.Parameters.AddWithValue(name, value);
        }

        return (long)(await command.ExecuteScalarAsync(Token))!;
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
