using System.Globalization;
using System.Text;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed partial class LongRestMaintenanceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Paid_participant_common_plan_counts_complete_closure_and_native_erasure_preserves_unrelated_history(
        bool protectedSources, bool foreignKeys)
    {
        await using MaintenanceFixture f = await CreatePaidLifecycleFixtureAsync(protectedSources);

        string unrelated = await SeedUnrelatedLifecycleHistoryAsync(f);

        string[] before = await ReadLifecycleHistoryAsync(f, unrelated);

        Assert.NotEmpty(before);

        await f.H.ExecuteAsync(foreignKeys ? "PRAGMA foreign_keys = ON;" : "PRAGMA foreign_keys = OFF;");

        Assert.Equal(foreignKeys ? 1L : 0L, await f.H.ScalarAsync("PRAGMA foreign_keys;"));

        Dictionary<string, long> expected = await ReadLifecycleCountsAsync(f, PaidGenerationTables.Concat(PaidReceiptTables));

        expected.Add("long_rest_sources", 1);

        // Count and erasure are separate obligations. The latter runs even when the former is RED,
        // so a preview omission cannot hide a leak or an unrelated-scope mutation.
        CovenantArtifactPlanTally measured = await CovenantArtifactPlanRunner.RunAsync(f.H.Connection, null,
            SensitiveArtifactKind.Saga, Guid.Parse(f.Source).ToString("N"), CovenantArtifactPlanMode.Count, CancellationToken.None);

        Dictionary<string, long> billed = await ReadLifecycleCountsAsync(f, ["BillableOperations", "InferenceRuns", "LongRunningOperations"]);

        if (protectedSources)
        {
            await ErasePaidParticipantAsync(f, foreignKeys);
        }
        else
        {
            Assert.True(await f.H.Saga.Store.DeleteAsync(f.Source, CancellationToken.None));
        }

        await AssertPaidLifecycleAbsentAsync(f);

        Assert.Equal(before, await ReadLifecycleHistoryAsync(f, unrelated));

        Assert.Equal(billed.OrderBy(item => item.Key),
            (await ReadLifecycleCountsAsync(f, ["BillableOperations", "InferenceRuns", "LongRunningOperations"])).OrderBy(item => item.Key));

        foreach ((string table, long rows) in expected)
        {
            MemoryErasureTableCount target = Assert.Single(measured.Targets, item => item.Table == table);

            Assert.Equal(rows, target.Rows);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Paid_whole_saga_delete_removes_all_generation_and_receipt_companions(bool foreignKeys)
    {
        await using MaintenanceFixture f = await CreatePaidLifecycleFixtureAsync(protectedSources: false);

        await f.H.ExecuteAsync(foreignKeys ? "PRAGMA foreign_keys = ON;" : "PRAGMA foreign_keys = OFF;");

        Assert.Equal(foreignKeys ? 1L : 0L, await f.H.ScalarAsync("PRAGMA foreign_keys;"));

        Dictionary<string, long> billed = await ReadLifecycleCountsAsync(f, ["BillableOperations", "InferenceRuns", "LongRunningOperations"]);

        await f.H.Saga.Store.DeleteAllAsync(CancellationToken.None);

        await AssertPaidLifecycleAbsentAsync(f);

        foreach ((string table, long count) in await ReadLifecycleCountsAsync(f, PaidSagaTables.Append("long_rest_sources")))
        {
            Assert.True(count == 0, $"{table} retained {count} Saga lifecycle rows.");
        }

        Assert.Equal(billed.OrderBy(item => item.Key),
            (await ReadLifecycleCountsAsync(f, ["BillableOperations", "InferenceRuns", "LongRunningOperations"])).OrderBy(item => item.Key));

        Assert.Equal(foreignKeys ? 1L : 0L, await f.H.ScalarAsync("PRAGMA foreign_keys;"));
    }

    [Fact]
    public async Task Paid_campaign_saga_reset_preview_and_committed_counts_include_all_companions()
    {
        await using MaintenanceFixture f = await CreatePaidLifecycleFixtureAsync(protectedSources: false);

        string unrelated = await SeedUnrelatedLifecycleHistoryAsync(f);

        string[] before = await ReadLifecycleHistoryAsync(f, unrelated);

        await using LifecycleRetentionService retention = new(f);

        DataRetentionRequest request = new(DataRetentionOperation.ResetMemory, f.CampaignId, MemoryResetScope.Saga);

        DataRetentionPlan plan = await retention.Service.PlanAsync(request);

        Assert.Empty(plan.Blockers);

        Assert.Empty(plan.Conflicts);

        long expected = (await ReadLifecycleCountsAsync(f, PaidGenerationTables.Concat(PaidReceiptTables))).Values.Sum();

        expected += await CountLifecycleAsync(f, "long_rest_sources", "MemoryId <> $unrelated", ("$unrelated", unrelated));

        // A Saga reset classifies both the subjects and their companions as derived records.
        foreach (string table in PaidSagaTables)
        {
            expected += await CountLifecycleAsync(f, table, SagaLifecyclePredicate(table), ("$unrelated", unrelated));
        }

        Result<DataRetentionApplyResult> applied = await retention.Service.ApplyAsync(new(request, plan.PlanId));

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : "");

        Assert.True(applied.Value.Reconciled);

        Assert.Equal(0, applied.Value.RowsDeleted);

        await AssertPaidLifecycleAbsentAsync(f);

        Assert.Equal(before, await ReadLifecycleHistoryAsync(f, unrelated));

        Assert.Equal(expected, plan.DerivedRecords);

        Assert.Equal(expected, applied.Value.DerivedRecordsDeleted);
    }

    [Fact]
    public async Task Paid_clean_factory_preview_and_registered_v0_apply_account_for_complete_v18_inventory()
    {
        await using MaintenanceFixture f = await CreatePaidLifecycleFixtureAsync(protectedSources: false);

        await using LifecycleRetentionService retention = new(f);

        DataRetentionRequest request = new(DataRetentionOperation.FactoryReset);

        DataRetentionPlan plan = await retention.Service.PlanAsync(request);

        Assert.Empty(plan.Blockers);

        Assert.Empty(plan.Conflicts);

        long expected = await ExpectedPaidFactoryDerivedAsync(f);

        DateTimeOffset crashed = DateTimeOffset.UtcNow.AddMinutes(-10);

        LongRunningOperation operation = await f.Operations.CreateAsync(new(
            LongRunningOperationKinds.DataRetentionFactoryReset, LongRunningOperationRecoveryPolicy.RestartIdempotently,
            "Interrupted version-zero paid lifecycle reset.", crashed));

        Assert.True((await f.Operations.TryAcquireLeaseAsync(operation.Id, "crashed-paid-factory", crashed, crashed.AddMinutes(1))).Acquired);

        LongRunningOperationReconciler reconciler = new(f.Operations,
            [new DataRetentionFactoryResetRecoveryHandler(retention.Service)], TimeProvider.System,
            NullLogger<LongRunningOperationReconciler>.Instance, new LongRunningOperationOwnership());

        LongRunningOperationReconciliationSummary recovered = await reconciler.ReconcileNowAsync(
            "paid-factory-recovery", 10, 1, CancellationToken.None);

        // Recovery rebuilds its exact preview, checks physical/derived deleted counts inside apply,
        // and excludes only its own launch row. Completion therefore attests that joined apply.
        Assert.Equal(1, recovered.Completed);

        Assert.Equal(0, recovered.RequiresAttention);

        Assert.Equal(LongRunningOperationState.Completed, (await f.Operations.GetAsync(operation.Id))!.State);

        await AssertPaidLifecycleAbsentAsync(f);

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_sources;"));

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM saga_memories;"));

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM InferenceRuns;"));

        DataRetentionPlan after = await retention.Service.PlanAsync(request);

        Assert.Equal(0, after.DerivedRecords);

        Assert.Equal(1, after.Rows); // The completed recovery launch is the only remaining planned row.

        Assert.Equal(expected, plan.DerivedRecords);
    }

    [Fact]
    public async Task Paid_protected_factory_preview_includes_exact_retained_receipt_and_generation_companions()
    {
        await using MaintenanceFixture f = await CreatePaidLifecycleFixtureAsync(protectedSources: true);

        await using LifecycleRetentionService retention = new(f);

        DataRetentionPlan plan = await retention.Service.PlanAsync(new(DataRetentionOperation.FactoryReset));

        Assert.True((long)(await f.H.ScalarAsync("SELECT count(*) FROM long_rest_protected_receipt_evidence;"))! > 0);

        Assert.True((long)(await f.H.ScalarAsync("SELECT count(*) FROM long_rest_protected_receipt_inputs;"))! >= 2);

        Assert.Equal(await ExpectedPaidFactoryDerivedAsync(f), plan.DerivedRecords);
    }

    // This inventory is the complete generation-owned logical content/evidence closure. It excludes
    // virtual-table implementation shadows, canonical source rows and the retained operation journal.
    private static readonly string[] PaidGenerationTables =
    [
        "long_rest_clean_fts", "long_rest_protected_fts", "long_rest_discovery_embeddings",
        "long_rest_discovery_edges", "long_rest_accepted_summaries", "long_rest_adjudications",
        "long_rest_provider_effects", "long_rest_work_inputs", "long_rest_work_items",
        "long_rest_discovery_nodes", "long_rest_discovery_leaves", "long_rest_sweeps",
        "long_rest_discovery_generations",
    ];

    private static readonly string[] PaidReceiptTables =
    [
        "long_rest_protected_receipt_inputs", "long_rest_protected_receipt_evidence",
        "long_rest_receipts", "long_rest_suppressions", "long_rest_receipt_inputs",
    ];

    // These are the nonzero Saga/Annals companion families in this real paid fixture. It creates no
    // attachment, Lexicon, workspace, Tapestry or Entry projections. Subject rows are separate below.
    private static readonly string[] PaidSagaTables =
    [
        "annal_review_events", "annal_review_decision_receipts", "annal_review_markers",
        "annal_dependencies", "annal_heads", "annal_versions", "annal_claims",
        "saga_memory_embeddings", "saga_memory_attachment_provenance", "saga_memories",
    ];

    private static async Task<MaintenanceFixture> CreatePaidLifecycleFixtureAsync(bool protectedSources)
    {
        MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, campaign: true, protectedSources: protectedSources);

        try
        {
            f.Handler.InsideSend = () =>
            {
                LongRestProviderPayload payload = f.Payloads[^1];

                f.Handler.ChatText = payload.Kind == LongRestProviderPayloadKind.Adjudication
                    ? "{\"kind\":\"EquivalentObservations\",\"survivorVersionId\":null}"
                    : "{\"summary\":\"Accepted summary\"}";

                return Task.CompletedTask;
            };

            LongRestMaintenanceCapability? capability = protectedSources
                ? LongRestSweepHarness.Require(await f.Maintenance.CreateCapabilityAsync(f.Operation, f.Invocation,
                    f.Claim, f.Scope, f.Authority, CancellationToken.None)) : null;

            LongRestSweepProgress completed = LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, capability, CancellationToken.None));

            Assert.True(completed.Phase == LongRestSweepPhase.Completed,
                $"Deferred {completed.DeferredReason}; sends {f.Handler.SendCount}; generation phase {await f.H.ScalarAsync("SELECT PhaseCode FROM long_rest_discovery_generations;")}; summaries {await f.H.ScalarAsync("SELECT count(*) FROM long_rest_accepted_summaries;")}; adjudications {await f.H.ScalarAsync("SELECT count(*) FROM long_rest_adjudications;")}; provider {(f.ProviderResult is { } result ? result.IsFailure ? result.Error.Code : "success" : "none")}.");

            Assert.Equal(LongRunningOperationState.Completed, (await f.Operations.GetAsync(f.Operation))!.State);

            Assert.True(f.Handler.SendCount >= 3);

            Assert.Equal((long)f.Handler.SendCount, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));

            foreach (string table in new[] { "long_rest_accepted_summaries", "long_rest_discovery_embeddings", "long_rest_adjudications", "long_rest_receipts" })
            {
                Assert.True((long)(await f.H.ScalarAsync($"SELECT count(*) FROM {table};"))! > 0, table);
            }

            Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_provider_effects WHERE StateCode = 3;"));

            Assert.Equal((long)f.Handler.SendCount, await f.H.ScalarAsync("SELECT sum(PaidCount) FROM long_rest_provider_effects;"));

            Assert.Equal(0L, await f.H.ScalarAsync("SELECT sum(InvalidCount) FROM long_rest_provider_effects;"));

            return f;
        }
        catch
        {
            await f.DisposeAsync();

            throw;
        }
    }

    private static async Task<string> SeedUnrelatedLifecycleHistoryAsync(MaintenanceFixture f)
    {
        string id = Guid.NewGuid().ToString("D");

        Guid session = await f.H.SessionAsync(Guid.NewGuid());

        const string original = "An unrelated Campaign keeps its original observation.";

        await f.H.SeedAsync(id, original, session);

        SagaCurationOutcome corrected = await f.H.Saga.Store.CorrectAsync(id, AnnalContentDigest.ForSagaMemory(original),
            "An unrelated Campaign keeps its corrected history.", f.H.Saga.Embedding(2), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(SagaCurationOutcomeKind.Applied, corrected.Kind);

        return id;
    }

    private static string SagaLifecyclePredicate(string table) => table switch
    {
        "saga_memories" => "Id <> $unrelated",
        "saga_memory_embeddings" or "saga_memory_attachment_provenance" => "MemoryId <> $unrelated",
        "annal_claims" => "SubjectStoreCode = 1 AND SubjectId <> $unrelated",
        "annal_heads" or "annal_versions" => "ClaimId IN (SELECT ClaimId FROM annal_claims WHERE SubjectStoreCode = 1 AND SubjectId <> $unrelated)",
        "annal_review_events" => "VersionId IN (SELECT VersionId FROM annal_versions WHERE ClaimId IN (SELECT ClaimId FROM annal_claims WHERE SubjectStoreCode = 1 AND SubjectId <> $unrelated))",
        "annal_review_decision_receipts" => "ReviewEventSequence IN (SELECT Sequence FROM annal_review_events WHERE VersionId IN (SELECT VersionId FROM annal_versions WHERE ClaimId IN (SELECT ClaimId FROM annal_claims WHERE SubjectStoreCode = 1 AND SubjectId <> $unrelated)))",
        "annal_dependencies" => "DependentVersionId IN (SELECT VersionId FROM annal_versions WHERE ClaimId IN (SELECT ClaimId FROM annal_claims WHERE SubjectStoreCode = 1 AND SubjectId <> $unrelated)) OR DependencyVersionId IN (SELECT VersionId FROM annal_versions WHERE ClaimId IN (SELECT ClaimId FROM annal_claims WHERE SubjectStoreCode = 1 AND SubjectId <> $unrelated))",
        "annal_review_markers" => "SubjectStoreCode = 1 AND CampaignId IN (SELECT CampaignId FROM saga_memories WHERE Id <> $unrelated)",
        _ => throw new ArgumentOutOfRangeException(nameof(table)),
    };

    private static async Task<long> ExpectedPaidFactoryDerivedAsync(MaintenanceFixture f)
    {
        long expected = (await ReadLifecycleCountsAsync(f, PaidGenerationTables.Concat(PaidReceiptTables)
            .Concat(PaidSagaTables.Where(table => table != "saga_memories")).Append("long_rest_sources"))).Values.Sum();

        return expected;
    }

    private static async Task<Dictionary<string, long>> ReadLifecycleCountsAsync(MaintenanceFixture f, IEnumerable<string> tables)
    {
        Dictionary<string, long> counts = new(StringComparer.Ordinal);

        foreach (string table in tables)
        {
            counts.Add(table, await CountLifecycleAsync(f, table, "1 = 1"));
        }

        return counts;
    }

    private static async Task<long> CountLifecycleAsync(MaintenanceFixture f, string table, string predicate,
        params (string Name, object Value)[] parameters)
    {
        await using SqliteCommand command = f.H.Connection.CreateCommand();

        command.CommandText = $"SELECT count(*) FROM {table} WHERE {predicate};";

        foreach ((string name, object value) in parameters) command.Parameters.AddWithValue(name, value);

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task AssertPaidLifecycleAbsentAsync(MaintenanceFixture f)
    {
        foreach ((string table, long count) in await ReadLifecycleCountsAsync(f, PaidGenerationTables.Concat(PaidReceiptTables)))
        {
            Assert.True(count == 0, $"{table} retained {count} paid lifecycle rows.");
        }

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM artifact_sensitivity WHERE ArtifactKindCode = 16;"));
    }

    private static async Task ErasePaidParticipantAsync(MaintenanceFixture f, bool foreignKeys)
    {
        using CovenantConnectionSource connections = new(f.H.Saga.Context, FixtureOrdinaryConnectionFactory.For(f.H.Saga.Context));

        ArtifactSensitivityLabel label = LongRestSweepHarness.Require(await new ArtifactSensitivityLedger(connections)
            .TryReadLabelAsync(SensitiveArtifactKind.Saga, Guid.Parse(f.Source), CancellationToken.None))!;

        await f.H.ExecuteAsync(foreignKeys ? "PRAGMA foreign_keys = ON;" : "PRAGMA foreign_keys = OFF;");

        Assert.Equal(foreignKeys ? 1L : 0L, await f.H.ScalarAsync("PRAGMA foreign_keys;"));

        await f.Authority.DisposeAsync();

        await using CovenantExclusiveLease lease = LongRestSweepHarness.Require(await f.Gate.AcquireExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantFamilyReinitialize), CancellationToken.None));

        CovenantArtifactErasureAuthority authority = LongRestSweepHarness.Require(CovenantArtifactErasureAuthority.ForExclusive(
            lease, CovenantExclusiveOperation.CovenantFamilyReinitialize));

        CovenantProtectedArtifactErasureKernel kernel = new(connections, CovenantSqliteConnectionInitializer.Instance, TimeProvider.System);

        Result<CovenantArtifactErasureProgress> erased = await kernel.ErasePageAsync(new(
            lease.Snapshot.DatasetGeneration!.Value,
            [new(label.ArtifactId, label.ArtifactKind, label.SessionId, label.LabelId, label, label.ArtifactContentDigest, label.ArtifactRevision)]),
            authority, CancellationToken.None);

        Assert.True(erased.IsSuccess, erased.IsFailure ? erased.Error.Message : "");

        Assert.Equal(CovenantErasureBlocker.None, erased.Value.Blocker);

        Assert.Equal(1UL, erased.Value.ErasedCount);

        Assert.Equal(foreignKeys ? 1L : 0L, await f.H.ScalarAsync("PRAGMA foreign_keys;"));
    }

    private static async Task<string[]> ReadLifecycleHistoryAsync(MaintenanceFixture f, string subject)
    {
        List<string> rows = [];

        // Every field of the unrelated subject, vector, source registration, and its complete Annals
        // graph is compared. Types, NULL, byte arrays and exact UTF-8 text all remain distinguishable.
        foreach (string table in PaidSagaTables.Append("long_rest_sources"))
        {
            string predicate = table == "long_rest_sources" ? "MemoryId = $unrelated"
                : SagaLifecyclePredicate(table).Replace("<> $unrelated", "= $unrelated", StringComparison.Ordinal);

            await using SqliteCommand command = f.H.Connection.CreateCommand();

            command.CommandText = $"SELECT * FROM {table} WHERE {predicate};";

            command.Parameters.AddWithValue("$unrelated", subject);

            await using SqliteDataReader reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                List<string> fields = [];

                for (int column = 0; column < reader.FieldCount; column++)
                {
                    object value = reader.GetValue(column);

                    fields.Add(value switch
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

    private sealed class LifecycleRetentionService : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "arcanum-paid-lifecycle-" + Guid.NewGuid().ToString("N"));

        internal DataRetentionService Service { get; }

        internal LifecycleRetentionService(MaintenanceFixture f)
        {
            Directory.CreateDirectory(root);

            foreach (string child in new[] { "attachments", "files", "logs" }) Directory.CreateDirectory(Path.Combine(root, child));

            Service = new(f.H.Saga.Context, new TestOptionsMonitor<ArcanumSettings>(f.Options.CurrentValue), f.Operations,
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
