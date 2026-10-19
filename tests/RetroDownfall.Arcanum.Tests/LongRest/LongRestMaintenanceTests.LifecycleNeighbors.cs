using RetroDownfall.Arcanum.Tests.ProvingGrounds;
using System.Globalization;
using System.Text;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed partial class LongRestMaintenanceTests
{
    [Fact]
    public async Task Paid_saga_embedding_reset_closes_the_graph_with_foreign_keys_off_and_preserves_other_stores_and_billing()
    {
        await using MaintenanceFixture f = await CreatePaidLifecycleFixtureAsync(protectedSources: false);

        await f.H.ExecuteAsync("""
            INSERT INTO lexicon_entries (Id, Name, NameNormalized, Type, FactsJson, FactsText, UpdatedAt)
            VALUES ('paid-reset-unrelated', 'Unrelated', 'unrelated', 'note', '["Retained"]', 'Retained', '2026-10-10T00:00:00.0000000Z');
            """);

        string[] lexicon = await SnapshotNeighborRowsAsync(f, ["lexicon_entries", "lexicon_fts"]);

        string[] billing = await SnapshotNeighborPaidHistoryAsync(f);

        await f.H.ExecuteAsync("PRAGMA foreign_keys = OFF;");

        ServiceCollection services = [];

        services.AddSingleton<IGrimoireOrdinaryConnectionFactory>(FixtureOrdinaryConnectionFactory.For(f.H.Saga.Context));

        services.AddSingleton(FixtureLabeledArtifactGuard.For(f.H.Saga.Context));

        using ServiceProvider provider = services.BuildServiceProvider();

        EmbeddingsResetService reset = new(f.H.Saga.Context, provider);

        EmbeddingsResetResult applied = await reset.ResetAsync(EmbeddingsResetScope.Saga, CancellationToken.None);

        Assert.True(applied.DeletedRowCounts["saga_memories"] > 0);

        Assert.Equal(lexicon, await SnapshotNeighborRowsAsync(f, ["lexicon_entries", "lexicon_fts"]));

        Assert.Equal(billing, await SnapshotNeighborPaidHistoryAsync(f));

        await AssertPaidLifecycleAbsentAsync(f);

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_sources;"));

        Assert.Equal(0L, await f.H.ScalarAsync("PRAGMA foreign_keys;"));
    }

    [Fact]
    public async Task Paid_saga_prune_counts_and_closes_the_selected_graph_without_changing_unrelated_history()
    {
        await using MaintenanceFixture f = await CreatePaidLifecycleFixtureAsync(protectedSources: false);

        string unrelated = await SeedUnrelatedLifecycleHistoryAsync(f);

        await SetNeighborSagaAgesAsync(f, oldSource: true);

        string[] before = await ReadLifecycleHistoryAsync(f, unrelated);

        string[] billing = await SnapshotNeighborPaidHistoryAsync(f);

        long expected = (await ReadLifecycleCountsAsync(f, PaidGenerationTables.Concat(PaidReceiptTables))).Values.Sum();

        expected += 1; // The selected source registration, counted once through the shared plan.

        expected += await CountLifecycleAsync(f, "saga_memory_embeddings", "MemoryId = $id", ("$id", f.Source));

        expected += await CountLifecycleAsync(f, "saga_memory_attachment_provenance", "MemoryId = $id", ("$id", f.Source));

        expected += await CountLifecycleAsync(f, "annal_claims", "SubjectStoreCode = 1 AND SubjectId = $id", ("$id", f.Source));

        await f.H.ExecuteAsync("PRAGMA foreign_keys = OFF;");

        await using NeighborRetentionService retention = new(f, SagaOnlyNeighborRetention(f));

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await retention.Service.PlanAsync(request);

        Assert.Empty(plan.Blockers);

        Assert.Empty(plan.Conflicts);

        Assert.Equal("saga:" + f.Source, Assert.Single(plan.CandidateIds));

        Result<DataRetentionApplyResult> applied = await retention.Service.ApplyAsync(new(request, plan.PlanId));

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : "");

        Assert.True(applied.Value.Reconciled);

        Assert.Equal(1, applied.Value.RowsDeleted);

        await AssertPaidLifecycleAbsentAsync(f);

        Assert.Equal(0L, await CountLifecycleAsync(f, "saga_memories", "Id = $id", ("$id", f.Source)));

        Assert.Equal(before, await ReadLifecycleHistoryAsync(f, unrelated));

        Assert.Equal(billing, await SnapshotNeighborPaidHistoryAsync(f));

        Assert.Equal(expected, plan.DerivedRecords);

        Assert.Equal(expected, applied.Value.DerivedRecordsDeleted);

        Assert.Equal(0L, await f.H.ScalarAsync("PRAGMA foreign_keys;"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Paid_saga_prune_refuses_a_new_pin_or_cutoff_change_after_preview_without_additional_erasure(bool pin)
    {
        await using MaintenanceFixture f = await CreatePaidLifecycleFixtureAsync(protectedSources: false);

        await SetNeighborSagaAgesAsync(f, oldSource: true);

        await using NeighborRetentionService retention = new(f, SagaOnlyNeighborRetention(f));

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await retention.Service.PlanAsync(request);

        Assert.Equal("saga:" + f.Source, Assert.Single(plan.CandidateIds));

        if (pin)
        {
            SagaCurationOutcome pinned = await f.H.Saga.Store.SetPinAsync(f.Source, true, DateTimeOffset.UtcNow, CancellationToken.None);

            Assert.Equal(SagaCurationOutcomeKind.Applied, pinned.Kind);
        }
        else
        {
            await f.H.ExecuteAsync("UPDATE saga_memories SET CreatedAt = $at WHERE Id = $id;",
                ("$at", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture)), ("$id", f.Source));
        }

        // Pinning itself legitimately invalidates its captured graph. Compare the state after that
        // production mutation so a stale prune cannot perform any additional content deletion.
        string[] before = await SnapshotNeighborRowsAsync(f,
            PaidGenerationTables.Concat(PaidReceiptTables).Concat(PaidSagaTables).Append("long_rest_sources").Append("artifact_sensitivity"));

        string[] billing = await SnapshotNeighborPaidHistoryAsync(f);

        int sends = f.Handler.SendCount;

        Result<DataRetentionApplyResult> applied = await retention.Service.ApplyAsync(new(request, plan.PlanId));

        Assert.True(applied.IsFailure);

        Assert.Equal(ErrorCodes.Data.PlanChanged, applied.Error.Code);

        Assert.Equal(before, await SnapshotNeighborRowsAsync(f,
            PaidGenerationTables.Concat(PaidReceiptTables).Concat(PaidSagaTables).Append("long_rest_sources").Append("artifact_sensitivity")));

        Assert.Equal(billing, await SnapshotNeighborPaidHistoryAsync(f));

        Assert.Equal(sends, f.Handler.SendCount);

        DataRetentionPlan refreshed = await retention.Service.PlanAsync(request);

        Assert.DoesNotContain("saga:" + f.Source, refreshed.CandidateIds);

        if (pin)
        {
            Assert.NotNull(refreshed.SagaCuration);

            Assert.Equal(1, refreshed.SagaCuration.PinnedRowsExemptFromPlan);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Paid_owner_deletion_closes_only_discovery_state_and_preserves_canonical_receipts_and_unrelated_history(bool sessionOwner)
    {
        await using MaintenanceFixture f = await CreatePaidLifecycleFixtureAsync(protectedSources: false);

        var evaluationSource = MemoryEvaluationEvidenceStoreTests.Source() with { CanonicalOwnerSessionId = f.SourceSession.ToString("D").ToUpperInvariant() };
        var evaluation = await MemoryEvaluationLifecycleTests.SeedAsync(f.H.Connection, [evaluationSource], sessionOwner ? null : f.CampaignId);

        string unrelated = await SeedUnrelatedLifecycleHistoryAsync(f);

        Guid foreignCampaign = Guid.NewGuid();

        Guid foreignSession = await f.H.SessionAsync(foreignCampaign);

        await f.H.SeedAsync(Guid.NewGuid().ToString("D"), "Foreign discovery retains its own exact searchable source.", foreignSession);

        var foreign = await f.H.CaptureAsync(foreignCampaign);

        string foreignId = foreign.GenerationId.ToString("D").ToUpperInvariant();

        Assert.Equal(1L, await CountLifecycleAsync(f, "long_rest_discovery_leaves", "GenerationId = $id", ("$id", foreignId)));

        Assert.Equal(1L, await CountLifecycleAsync(f, "long_rest_clean_fts", "GenerationId = $id", ("$id", foreignId)));

        Assert.True(await f.Operations.TryTransitionAsync(foreign.OperationId, 0, null,
            LongRunningOperationState.Abandoned, DateTimeOffset.UtcNow, cancellationToken: CancellationToken.None));

        string[] foreignBefore = await SnapshotNeighborGenerationAsync(f, foreignId);

        string[] unrelatedBefore = await ReadLifecycleHistoryAsync(f, unrelated);

        string[] canonicalBefore = await SnapshotNeighborRowsAsync(f, PaidSagaTables.Concat(PaidReceiptTables).Append("long_rest_sources"));

        string[] billing = await SnapshotNeighborPaidHistoryAsync(f);

        Assert.True((long)(await f.H.ScalarAsync("SELECT count(*) FROM long_rest_discovery_nodes WHERE Layer > 0;"))! > 0);

        await f.H.ExecuteAsync("PRAGMA foreign_keys = OFF;");

        if (sessionOwner)
        {
            await using NeighborRetentionService retention = new(f, SagaOnlyNeighborRetention(f));

            DataRetentionRequest request = new(DataRetentionOperation.DeleteSession, f.SourceSession);

            DataRetentionPlan plan = await retention.Service.PlanAsync(request);

            Assert.Empty(plan.Blockers);

            Assert.Empty(plan.Conflicts);

            long graphRows = (await ReadLifecycleCountsAsync(f, PaidGenerationTables)).Values.Sum() - foreignBefore.LongLength;

            Assert.True(graphRows > 0);

            Assert.Equal(graphRows + 3, plan.DerivedRecords);

            Result<DataRetentionApplyResult> applied = await retention.Service.ApplyAsync(new(request, plan.PlanId));

            Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : "");

            Assert.True(applied.Value.Reconciled);

            Assert.Equal(plan.DerivedRecords, applied.Value.DerivedRecordsDeleted);

            Assert.Equal(0L, await CountLifecycleAsync(f, "Sessions", "Id = $id", ("$id", f.SourceSession.ToString("D").ToUpperInvariant())));
        }
        else
        {
            CampaignRepository campaigns = new(f.H.Saga.Context, NullLogger<CampaignRepository>.Instance,
                new TestOptionsSnapshot<ArcanumSettings>(f.Options.CurrentValue), FixtureLabeledArtifactGuard.For(f.H.Saga.Context));

            Assert.True(await campaigns.DeleteAsync(f.CampaignId!.Value, CancellationToken.None));

            Assert.Equal(0L, await CountLifecycleAsync(f, "Campaigns", "Id = $id", ("$id", f.CampaignId!.Value.ToString("D").ToUpperInvariant())));
        }

        await MemoryEvaluationLifecycleTests.AssertAbsentAsync(f.H.Connection, evaluation.EvaluationId);

        Assert.Equal(canonicalBefore, await SnapshotNeighborRowsAsync(f, PaidSagaTables.Concat(PaidReceiptTables).Append("long_rest_sources")));

        Assert.Equal(unrelatedBefore, await ReadLifecycleHistoryAsync(f, unrelated));

        Assert.Equal(billing, await SnapshotNeighborPaidHistoryAsync(f));

        // Owner deletion is not participant erasure: its graph/work disappears while source/Annals
        // history and whole canonical receipts retain their established lifecycle meaning.
        // The complete remaining graph, including exact FTS contents, must equal the foreign
        // generation's full rows: this proves both target closure and bystander preservation.
        Assert.Equal(foreignBefore, await SnapshotNeighborRowsAsync(f, PaidGenerationTables));

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM artifact_sensitivity WHERE ArtifactKindCode = 16;"));

        Assert.Equal(0L, await f.H.ScalarAsync("PRAGMA foreign_keys;"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Paid_store_reset_closes_generations_even_after_their_leaf_selector_is_empty(bool invalidated)
    {
        await using MaintenanceFixture f = await CreatePaidLifecycleFixtureAsync(protectedSources: false);

        if (invalidated)
        {
            SagaCurationOutcome pinned = await f.H.Saga.Store.SetPinAsync(f.Source, true, DateTimeOffset.UtcNow, CancellationToken.None);

            Assert.Equal(SagaCurationOutcomeKind.Applied, pinned.Kind);

            Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_discovery_leaves;"));

            Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_discovery_generations WHERE PhaseCode = 7;"));
        }
        else
        {
            // A real admitted generation before capture has no leaf selector. Terminalize only its
            // fixture operation so the explicit store reset has no unrelated active-operation blocker.
            var empty = await f.H.StartAsync(f.CampaignId);

            Assert.Equal(2L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_discovery_generations;"));

            Assert.Equal(0L, await CountLifecycleAsync(f, "long_rest_discovery_leaves", "GenerationId = $id", ("$id", empty.GenerationId.ToString("D").ToUpperInvariant())));

            Assert.True(await f.Operations.TryTransitionAsync(empty.OperationId, 0, null,
                LongRunningOperationState.Abandoned, DateTimeOffset.UtcNow, cancellationToken: CancellationToken.None));
        }

        string[] billing = await SnapshotNeighborPaidHistoryAsync(f);

        long expected = (await ReadLifecycleCountsAsync(f,
            PaidGenerationTables.Concat(PaidReceiptTables).Concat(PaidSagaTables).Append("long_rest_sources"))).Values.Sum();

        await f.H.ExecuteAsync("PRAGMA foreign_keys = OFF;");

        await using NeighborRetentionService retention = new(f, SagaOnlyNeighborRetention(f));

        DataRetentionRequest request = new(DataRetentionOperation.ResetMemory, null, MemoryResetScope.Saga);

        DataRetentionPlan plan = await retention.Service.PlanAsync(request);

        Assert.Empty(plan.Blockers);

        Assert.Empty(plan.Conflicts);

        Result<DataRetentionApplyResult> applied = await retention.Service.ApplyAsync(new(request, plan.PlanId));

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : "");

        Assert.True(applied.Value.Reconciled);

        await AssertPaidLifecycleAbsentAsync(f);

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_sources;"));

        Assert.Equal(billing, await SnapshotNeighborPaidHistoryAsync(f));

        Assert.Equal(expected, plan.DerivedRecords);

        Assert.Equal(expected, applied.Value.DerivedRecordsDeleted);
    }

    private static async Task SetNeighborSagaAgesAsync(MaintenanceFixture f, bool oldSource)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await f.H.ExecuteAsync("UPDATE saga_memories SET CreatedAt = $at;", ("$at", now.ToString("o", CultureInfo.InvariantCulture)));

        if (oldSource)
        {
            await f.H.ExecuteAsync("UPDATE saga_memories SET CreatedAt = $at WHERE Id = $id;",
                ("$at", now.AddDays(-3).ToString("o", CultureInfo.InvariantCulture)), ("$id", f.Source));
        }
    }

    private static ArcanumSettings SagaOnlyNeighborRetention(MaintenanceFixture f)
    {
        RetentionRuleSettings disabled = new() { Enabled = false, Days = 30 };

        return f.Options.CurrentValue with
        {
            Retention = new()
            {
                ActiveSessions = disabled,
                ArchivedSessions = disabled,
                Entries = disabled,
                Attachments = disabled,
                UploadedFiles = disabled,
                CompletedBatches = disabled,
                SagaMemories = new() { Enabled = true, Days = 1 },
                LexiconEntries = disabled,
                CampaignSummaries = disabled,
                WorkspaceIndexes = disabled,
                SessionEntryEmbeddings = disabled,
                AuditLogs = disabled,
                GuardrailLogs = disabled,
                IdempotencyClaims = disabled,
                Accounting = disabled,
                LongRunningOperations = disabled,
                SanctumBreaches = disabled,
                DaemonHistory = disabled,
            },
        };
    }

    private static async Task<string[]> SnapshotNeighborGenerationAsync(MaintenanceFixture f, string generationId)
    {
        List<string> rows = [];

        foreach (string table in PaidGenerationTables)
        {
            // Independent test inventory; do not borrow the production closure predicates.
            string predicate = table switch
            {
                "long_rest_discovery_embeddings" => "NodeId IN (SELECT NodeId FROM long_rest_discovery_nodes WHERE GenerationId = $id)",
                "long_rest_adjudications" or "long_rest_provider_effects" or "long_rest_work_inputs" =>
                    "WorkItemId IN (SELECT WorkItemId FROM long_rest_work_items WHERE GenerationId = $id)",
                _ => "GenerationId = $id",
            };

            rows.AddRange(await SnapshotNeighborRowsAsync(f, [table], predicate, ("$id", generationId)));
        }

        return rows.Order(StringComparer.Ordinal).ToArray();
    }

    private static async Task<string[]> SnapshotNeighborPaidHistoryAsync(MaintenanceFixture f)
    {
        string[] billing = await SnapshotNeighborRowsAsync(f, ["BillableOperations", "InferenceRuns", "external_disclosure_receipts"]);

        string[] operation = await SnapshotNeighborRowsAsync(f, ["LongRunningOperations"],
            "Kind = $kind", ("$kind", LongRunningOperationKinds.LongRestSweep));

        return [.. billing, .. operation];
    }

    private static async Task<string[]> SnapshotNeighborRowsAsync(MaintenanceFixture f, IEnumerable<string> tables,
        string predicate = "1 = 1", params (string Name, object Value)[] parameters)
    {
        List<string> rows = [];

        foreach (string table in tables.Distinct(StringComparer.Ordinal))
        {
            await using SqliteCommand command = f.H.Connection.CreateCommand();

            command.CommandText = $"SELECT * FROM \"{table}\" WHERE {predicate};";

            foreach ((string name, object value) in parameters) command.Parameters.AddWithValue(name, value);

            await using SqliteDataReader reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                List<string> fields = [];

                for (int column = 0; column < reader.FieldCount; column++)
                {
                    fields.Add(reader.GetValue(column) switch
                    {
                        DBNull => "null",
                        byte[] bytes => "blob:" + Convert.ToHexString(bytes),
                        string text => "text:" + Convert.ToHexString(Encoding.UTF8.GetBytes(text)),
                        long integer => "integer:" + integer.ToString(CultureInfo.InvariantCulture),
                        double number => "real:" + number.ToString("R", CultureInfo.InvariantCulture),
                        _ => throw new InvalidDataException("Unexpected native SQLite field type."),
                    });
                }

                rows.Add(table + ":" + string.Join("|", fields));
            }
        }

        return rows.Order(StringComparer.Ordinal).ToArray();
    }

    private sealed class NeighborRetentionService : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "arcanum-paid-neighbor-" + Guid.NewGuid().ToString("N"));

        internal DataRetentionService Service { get; }

        internal NeighborRetentionService(MaintenanceFixture f, ArcanumSettings settings)
        {
            Directory.CreateDirectory(root);

            foreach (string child in new[] { "attachments", "files", "logs" }) Directory.CreateDirectory(Path.Combine(root, child));

            Service = new(f.H.Saga.Context, new TestOptionsMonitor<ArcanumSettings>(settings), f.Operations,
                TimeProvider.System, NullLogger<DataRetentionService>.Instance, FixtureLabeledArtifactGuard.For(f.H.Saga.Context),
                Path.Combine(root, "attachments"), Path.Combine(root, "files"), Path.Combine(root, "logs"));
        }

        public ValueTask DisposeAsync()
        {
            Directory.Delete(root, recursive: true);

            return ValueTask.CompletedTask;
        }
    }
}
