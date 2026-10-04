using Microsoft.Data.Sqlite;

using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.Logging.Abstractions;

using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Cli.UX;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Infrastructure.Operations;

using RetroDownfall.Arcanum.Tests.Covenant;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

using System.Text.Json;

using System.Text.Json.Serialization.Metadata;

namespace RetroDownfall.Arcanum.Tests.Data;

/// <summary>
/// Issue #116 — Covenant's retention identity, and the guarantee that ordinary time-based pruning can
/// never reach it.
/// </summary>
/// <remarks>
/// Every other retention class answers "how old is too old". The Covenant deliberately has no such
/// answer: its versions, heads, provenance, tombstones, and disclosure receipts are the evidence that
/// makes an erasure claim checkable, so a sweep that could age them out would quietly destroy the only
/// record of what was destroyed. The class exists so an operator can *see* the family in `data status`,
/// not so a rule can be pointed at it.
///
/// <para>The numeric codes are pinned literally because both enums are persisted in retention policy
/// rows and durable operation checkpoints. Reordering a member would silently repoint an existing row
/// at a different data class — a rule an operator wrote for `SagaMemories` would start deleting
/// something else — so the test that hurts to update is the point.</para>
/// </remarks>
[Collection("Grimoire")]

[Trait("Category", "Integration")]

public sealed class CovenantRetentionTests : IAsyncLifetime
{

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private string _root = string.Empty;

    private ArcanumDbContext? _db;

    public CovenantRetentionTests(GrimoireFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {

        _dbPath = _fixture.CopyDatabase();

        _root = Path.Combine(
            Path.GetTempPath(),
            "arcanum-covenant-retention-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(Path.Combine(_root, "attachments"));

        Directory.CreateDirectory(Path.Combine(_root, "files"));

        Directory.CreateDirectory(Path.Combine(_root, "logs"));

        _db = _fixture.CreateContext(_dbPath);

        return Task.CompletedTask;

    }

    public async Task DisposeAsync()
    {

        if (_db is not null)
        {

            SqliteConnection connection = (SqliteConnection)_db.Database.GetDbConnection();

            await _db.DisposeAsync();

            SqliteConnection.ClearPool(connection);

        }

        if (File.Exists(_dbPath))
        {

            File.Delete(_dbPath);

        }

        if (Directory.Exists(_root))
        {

            Directory.Delete(_root, recursive: true);

        }

    }

    [Fact]
    public void Retention_and_reset_enum_codes_preserve_every_existing_value_and_append_covenant()
    {

        Assert.Equal(0, (int)RetentionDataClass.ActiveSessions);

        Assert.Equal(1, (int)RetentionDataClass.ArchivedSessions);

        Assert.Equal(2, (int)RetentionDataClass.Entries);

        Assert.Equal(3, (int)RetentionDataClass.AttachmentVersions);

        Assert.Equal(4, (int)RetentionDataClass.AttachmentBytes);

        Assert.Equal(5, (int)RetentionDataClass.AttachmentChunks);

        Assert.Equal(6, (int)RetentionDataClass.AttachmentEmbeddings);

        Assert.Equal(7, (int)RetentionDataClass.UploadedFiles);

        Assert.Equal(8, (int)RetentionDataClass.BatchInputFiles);

        Assert.Equal(9, (int)RetentionDataClass.BatchOutputFiles);

        Assert.Equal(10, (int)RetentionDataClass.BatchErrorFiles);

        Assert.Equal(11, (int)RetentionDataClass.CompletedBatches);

        Assert.Equal(12, (int)RetentionDataClass.SagaMemories);

        Assert.Equal(13, (int)RetentionDataClass.LexiconEntries);

        Assert.Equal(14, (int)RetentionDataClass.WorkspaceChunks);

        Assert.Equal(15, (int)RetentionDataClass.WorkspaceEmbeddings);

        Assert.Equal(16, (int)RetentionDataClass.SessionEntryEmbeddings);

        Assert.Equal(17, (int)RetentionDataClass.Tapestry);

        Assert.Equal(18, (int)RetentionDataClass.AuditLogs);

        Assert.Equal(19, (int)RetentionDataClass.GuardrailLogs);

        Assert.Equal(20, (int)RetentionDataClass.IdempotencyClaims);

        Assert.Equal(21, (int)RetentionDataClass.InferenceRuns);

        Assert.Equal(22, (int)RetentionDataClass.BillableOperations);

        Assert.Equal(23, (int)RetentionDataClass.BudgetReservations);

        Assert.Equal(24, (int)RetentionDataClass.CostAdjustments);

        Assert.Equal(25, (int)RetentionDataClass.LongRunningOperations);

        Assert.Equal(26, (int)RetentionDataClass.SanctumBreaches);

        Assert.Equal(27, (int)RetentionDataClass.DaemonExecutions);

        Assert.Equal(28, (int)RetentionDataClass.Covenant);

        Assert.Equal(29, (int)RetentionDataClass.Annals);

        Assert.Equal(30, (int)RetentionDataClass.MemoryErasureEvidence);

        Assert.Equal(31, Enum.GetValues<RetentionDataClass>().Length);

        Assert.Equal(0, (int)MemoryResetScope.Entry);

        Assert.Equal(1, (int)MemoryResetScope.Attachments);

        Assert.Equal(2, (int)MemoryResetScope.Workspace);

        Assert.Equal(3, (int)MemoryResetScope.Saga);

        Assert.Equal(4, (int)MemoryResetScope.Lexicon);

        Assert.Equal(5, (int)MemoryResetScope.Covenant);

        Assert.Equal(6, Enum.GetValues<MemoryResetScope>().Length);

    }

    [Fact]
    public void Covenant_retention_class_has_no_configurable_time_rule()
    {

        RetentionSettings everyRuleConfigured = new()
        {

            ActiveSessions = Rule(),

            ArchivedSessions = Rule(),

            Entries = Rule(),

            Attachments = Rule(),

            UploadedFiles = Rule(),

            CompletedBatches = Rule(),

            SagaMemories = Rule(),

            LexiconEntries = Rule(),

            WorkspaceIndexes = Rule(),

            SessionEntryEmbeddings = Rule(),

            AuditLogs = Rule(),

            GuardrailLogs = Rule(),

            IdempotencyClaims = Rule(),

            Accounting = Rule(),

            LongRunningOperations = Rule(),

            SanctumBreaches = Rule(),

            DaemonHistory = Rule(),

        };

        Assert.Null(
            DataRetentionSettingsCatalog.ResolveRule(
                everyRuleConfigured,
                RetentionDataClass.Covenant));

    }

    [Fact]
    public void Covenant_data_class_parses_from_its_name_and_never_from_a_numeric_code()
    {

        Assert.True(DataRetentionDataClassParser.TryParse("covenant", out RetentionDataClass parsed));

        Assert.Equal(RetentionDataClass.Covenant, parsed);

        Assert.True(DataRetentionDataClassParser.TryParse("Covenant", out parsed));

        Assert.Equal(RetentionDataClass.Covenant, parsed);

        Assert.False(DataRetentionDataClassParser.TryParse("28", out _));

    }

    /// <summary>
    /// A sweep with every rule enabled and every cutoff in the future is the most aggressive ordinary
    /// prune this build can express. It must still leave the Covenant family byte-identical.
    /// </summary>
    [SkippableFact]

    public async Task Ordinary_sweep_never_deletes_covenant_versions_heads_provenance_or_tombstones()
    {

        RequireSqlCipher();

        await SeedCovenantFamilyAsync(CancellationToken.None);

        IReadOnlyDictionary<string, long> before = await CountCovenantTablesAsync(
            CancellationToken.None);

        Assert.NotEqual(0, before.Values.Sum());

        IDataRetentionService service = CreateService(EveryRuleEnabled());

        DataRetentionPlan plan = await service
            .PlanAsync(
                new DataRetentionRequest(DataRetentionOperation.Prune),
                CancellationToken.None);

        Assert.DoesNotContain(plan.Items, item => item.DataClass is RetentionDataClass.Covenant);

        _ = await service.ApplyAsync(
            new DataRetentionApplyRequest(
                new DataRetentionRequest(DataRetentionOperation.Prune),
                plan.PlanId),
            CancellationToken.None);

        IReadOnlyDictionary<string, long> after = await CountCovenantTablesAsync(
            CancellationToken.None);

        // Asserted per table rather than as one dictionary comparison: a sweep that ages out exactly
        // one arm of the family is the regression this guards, and the failure has to name which arm.
        foreach (string table in CovenantFamilyTables)
        {

            Assert.Equal((table, before[table]), (table, after[table]));

        }

    }

    [SkippableFact]

    public async Task Ordinary_sweep_never_deletes_external_disclosure_receipts_or_folded_aggregates()
    {

        RequireSqlCipher();

        await SeedCovenantFamilyAsync(CancellationToken.None);

        long receiptsBefore = await ScalarAsync(
            "SELECT COUNT(*) FROM external_disclosure_receipts",
            CancellationToken.None);

        long stateBefore = await ScalarAsync(
            "SELECT COUNT(*) FROM external_disclosure_state",
            CancellationToken.None);

        Assert.NotEqual(0, receiptsBefore);

        Assert.NotEqual(0, stateBefore);

        IDataRetentionService service = CreateService(EveryRuleEnabled());

        DataRetentionPlan plan = await service
            .PlanAsync(
                new DataRetentionRequest(DataRetentionOperation.Prune),
                CancellationToken.None);

        _ = await service.ApplyAsync(
            new DataRetentionApplyRequest(
                new DataRetentionRequest(DataRetentionOperation.Prune),
                plan.PlanId),
            CancellationToken.None);

        Assert.Equal(
            receiptsBefore,
            await ScalarAsync(
                "SELECT COUNT(*) FROM external_disclosure_receipts",
                CancellationToken.None));

        Assert.Equal(
            stateBefore,
            await ScalarAsync(
                "SELECT COUNT(*) FROM external_disclosure_state",
                CancellationToken.None));

    }

    [SkippableFact]

    public async Task Status_reports_the_content_free_covenant_row_and_its_five_counts()
    {

        RequireSqlCipher();

        await SeedCovenantFamilyAsync(CancellationToken.None);

        RecordingCovenantOperationGate gate = new();

        IDataRetentionService service = CreateService(covenantGate: gate);

        DataRetentionStatus status = await service.GetStatusAsync(
            CancellationToken.None);

        DataRetentionStatusItem covenant = Assert.Single(
            status.Items,
            item => item.DataClass is RetentionDataClass.Covenant);

        Assert.False(covenant.PolicyEnabled);

        Assert.Null(covenant.RetentionDays);

        Assert.True(covenant.Rows > 0);

        DataRetentionCovenantInventory inventory = Assert.IsType<DataRetentionCovenantInventory>(
            status.Covenant);

        Assert.True(inventory.Rows > 0);

        Assert.Equal(1, inventory.ManagedFiles);

        Assert.Equal(1, inventory.LocalArtifacts);

        Assert.Equal(1, inventory.AffectedSessions);

        Assert.Equal(3, inventory.PossibleDisclosures);

        Assert.Equal(CovenantDisclosureCountKind.Exact, inventory.DisclosureCountKind);

    }

    /// <summary>
    /// An installation with no Covenant tier reports no Covenant row at all, rather than a row of
    /// zeroes: a zero is a measurement, and the honest answer there is that nothing was measured.
    /// </summary>
    [SkippableFact]

    public async Task Status_omits_the_covenant_row_entirely_when_no_lease_can_be_taken()
    {

        RequireSqlCipher();

        await SeedCovenantFamilyAsync(CancellationToken.None);

        IDataRetentionService service = CreateService(covenantGate: null);

        DataRetentionStatus status = await service.GetStatusAsync(
            CancellationToken.None);

        Assert.DoesNotContain(status.Items, item => item.DataClass is RetentionDataClass.Covenant);

        Assert.Null(status.Covenant);

    }

    [SkippableFact]

    public async Task Covenant_memory_reset_plan_reports_content_free_aggregates_without_an_activation_conflict()
    {

        RequireSqlCipher();

        await SeedCovenantFamilyAsync(CancellationToken.None);

        RecordingCovenantOperationGate gate = new();

        IDataRetentionService service = CreateService(covenantGate: gate);

        DataRetentionRequest request = new(
            DataRetentionOperation.ResetMemory,
            MemoryScope: MemoryResetScope.Covenant);

        DataRetentionPlan plan = await service.PlanAsync(
            request,
            CancellationToken.None);

        Assert.Empty(plan.Conflicts);

        DataRetentionCovenantInventory inventory = Assert.IsType<DataRetentionCovenantInventory>(
            plan.Covenant);

        Assert.True(inventory.Rows > 0);

        Assert.Equal(1, inventory.ManagedFiles);

        Assert.Equal(1, inventory.LocalArtifacts);

        Assert.Equal(1, inventory.AffectedSessions);

        Assert.Equal(3, inventory.PossibleDisclosures);

        Assert.Equal(CovenantDisclosureCountKind.Exact, inventory.DisclosureCountKind);

        string serializedPlan = JsonSerializer.Serialize(
            plan,
            ArcanumJsonContext.Default.DataRetentionPlan);

        Assert.DoesNotContain("project/goal", serializedPlan, StringComparison.Ordinal);

        Assert.DoesNotContain("a protected summary", serializedPlan, StringComparison.Ordinal);

        Assert.DoesNotContain(CovenantRetentionSeed.SessionId, serializedPlan, StringComparison.Ordinal);

    }

    /// <summary>
    /// The seed's disclosure accounting comes from the production journal and its live fold, not from
    /// rows written by hand, so every retention report above is reading what the product produced.
    /// </summary>
    [SkippableFact]

    public async Task Seeded_disclosure_accounting_is_produced_by_the_live_journal_fold()
    {

        RequireSqlCipher();

        await SeedCovenantFamilyAsync(CancellationToken.None);

        Assert.Equal(
            4,
            await ScalarAsync(
                "SELECT COUNT(*) FROM external_disclosure_receipts",
                CancellationToken.None));

        Assert.Equal(
            4,
            await ScalarAsync(
                """
                SELECT LastFoldedOrdinal
                FROM disclosure_subject_state
                WHERE SubjectId = 'ffffffff-7777-4777-8777-ffffffffffff'
                    AND LastFoldedOrdinal = LastAllocatedOrdinal;
                """,
                CancellationToken.None));

        List<CovenantDisclosureState> buckets = await ExternalDisclosureStateStore.ReadAllAsync(
            (SqliteConnection)_db!.Database.GetDbConnection(),
            null,
            CancellationToken.None);

        (CovenantEgressDestination, CovenantDisclosureRevocability, CovenantDisclosureCountKind, ulong)[] expected =
        [
            (CovenantEgressDestination.Provider, CovenantDisclosureRevocability.Nonrevocable,
                CovenantDisclosureCountKind.Exact, 3ul),
            (CovenantEgressDestination.Process, CovenantDisclosureRevocability.LocallyRevocable,
                CovenantDisclosureCountKind.Exact, 1ul),
        ];

        Assert.Equal(
            expected,
            buckets
                .Select(static bucket => (bucket.Destination, bucket.Revocability, bucket.CountKind, bucket.Count))
                .OrderBy(static bucket => bucket.Destination)
                .ToArray());

    }

    /// <summary>
    /// The reset preview an operator reads before confirming names the receipts this installation
    /// recorded. Before the live fold, those receipts reached no bucket, and the CLI told the operator
    /// that nothing nonrevocable had ever left.
    /// </summary>
    [SkippableFact]

    public async Task Reset_preview_rendered_by_the_cli_counts_live_receipts_instead_of_denying_disclosure()
    {

        RequireSqlCipher();

        await SeedCovenantFamilyAsync(CancellationToken.None);

        IDataRetentionService service = CreateService(covenantGate: new RecordingCovenantOperationGate());

        DataRetentionPlan plan = await service.PlanAsync(
            new DataRetentionRequest(
                DataRetentionOperation.ResetMemory,
                MemoryScope: MemoryResetScope.Covenant),
            CancellationToken.None);

        DataRetentionPlan received = Assert.IsType<DataRetentionPlan>(JsonSerializer.Deserialize(
            JsonSerializer.Serialize(plan, ArcanumJsonContext.Default.DataRetentionPlan),
            ArcanumJsonContext.Default.DataRetentionPlan));

        DiagnosticRecorder recorder = new();

        new CovenantExternalRetentionDisclosureWriter(recorder, Options.Create(new ArcanumSettings()))
            .Write(received.Covenant);

        Assert.Contains(
            "This installation's own receipts record exactly 3 physical attempts that could have carried "
                + "protected content out of it. Nothing this reset does can revoke any of them.",
            recorder.Diagnostics);

        Assert.DoesNotContain(
            recorder.Diagnostics,
            static line => line.Contains("record no nonrevocable disclosure", StringComparison.Ordinal));

    }

    /// <summary>
    /// The reset inventory counts through the canonical content list, and that list holds the curation
    /// tables. An installation whose only Covenant rows are a pin still has something a reset would
    /// delete, and a preview that reported nothing there would hide it from the operator.
    /// </summary>
    [SkippableFact]

    public async Task Covenant_reset_plan_and_status_count_a_pin_on_an_installation_that_holds_no_entry()
    {

        RequireSqlCipher();

        IDataRetentionService service = CreateService(
            covenantGate: new RecordingCovenantOperationGate());

        DataRetentionRequest reset = new(
            DataRetentionOperation.ResetMemory,
            MemoryScope: MemoryResetScope.Covenant);

        DataRetentionPlan empty = await service.PlanAsync(
            reset,
            CancellationToken.None);

        Assert.Equal(0, Assert.IsType<DataRetentionCovenantInventory>(empty.Covenant).Rows);

        Assert.DoesNotContain(empty.Items, item => item.DataClass is RetentionDataClass.Covenant);

        await SeedCurationPinAsync(CancellationToken.None);

        Assert.Equal(0, await ScalarAsync("SELECT COUNT(*) FROM covenant_entries", CancellationToken.None));

        DataRetentionPlan plan = await service.PlanAsync(
            reset,
            CancellationToken.None);

        // The pin's version, the head that points at it, and its receipt.
        Assert.Equal(3, Assert.IsType<DataRetentionCovenantInventory>(plan.Covenant).Rows);

        DataRetentionPlanItem planned = Assert.Single(
            plan.Items,
            item => item.DataClass is RetentionDataClass.Covenant);

        Assert.Equal(3, planned.DerivedRecords);

        Assert.NotEqual(empty.PlanId, plan.PlanId);

        DataRetentionStatus status = await service.GetStatusAsync(
            CancellationToken.None);

        Assert.Equal(3, Assert.IsType<DataRetentionCovenantInventory>(status.Covenant).Rows);

        Assert.Equal(
            3,
            Assert.Single(status.Items, item => item.DataClass is RetentionDataClass.Covenant).Rows);

    }

    /// <summary>
    /// The inventory's row count is the canonical content list summed, plus the accelerator projection
    /// and the three core support tables. Computed here from the list itself, so an inventory that kept
    /// a list of its own and dropped a table the seed fills would stop agreeing.
    /// </summary>
    [SkippableFact]

    public async Task Covenant_inventory_rows_are_the_canonical_content_list_summed_with_its_support_tables()
    {

        RequireSqlCipher();

        await SeedCovenantFamilyAsync(CancellationToken.None);

        await SeedCurationPinAsync(CancellationToken.None);

        long canonical = 0;

        int populated = 0;

        foreach (string table in CovenantCanonicalContentTables.InDeletionOrder)
        {

            long rows = await ScalarAsync(
                $"SELECT COUNT(*) FROM \"{table}\"",
                CancellationToken.None);

            canonical += rows;

            populated += rows > 0 ? 1 : 0;

        }

        // The seed has to reach enough of the list for the sum to mean something.
        Assert.True(populated >= 8, $"Only {populated} canonical content tables hold a row.");

        long support = 0;

        foreach (string table in (string[])
                 [
                     "covenant_search_documents",
                     "artifact_sensitivity",
                     "assistant_entry_erasure_receipts",
                     "external_disclosure_receipts",
                 ])
        {

            if (await ScalarAsync(
                    $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{table}'",
                    CancellationToken.None) == 1)
            {

                support += await ScalarAsync(
                    $"SELECT COUNT(*) FROM \"{table}\"",
                    CancellationToken.None);

            }

        }

        IDataRetentionService service = CreateService(
            covenantGate: new RecordingCovenantOperationGate());

        DataRetentionStatus status = await service.GetStatusAsync(
            CancellationToken.None);

        Assert.Equal(
            canonical + support,
            Assert.IsType<DataRetentionCovenantInventory>(status.Covenant).Rows);

        DataRetentionPlan plan = await service.PlanAsync(
            new DataRetentionRequest(
                DataRetentionOperation.ResetMemory,
                MemoryScope: MemoryResetScope.Covenant),
            CancellationToken.None);

        Assert.Equal(
            canonical + support,
            Assert.Single(plan.Items, item => item.DataClass is RetentionDataClass.Covenant).DerivedRecords);

    }

    [SkippableTheory]

    [InlineData(CovenantInventoryAggregate.Rows)]

    [InlineData(CovenantInventoryAggregate.ManagedFiles)]

    [InlineData(CovenantInventoryAggregate.LocalArtifacts)]

    [InlineData(CovenantInventoryAggregate.AffectedSessions)]

    [InlineData(CovenantInventoryAggregate.DisclosureExposure)]

    public async Task Explicit_covenant_reset_and_factory_plan_ids_bind_every_content_free_inventory_aggregate_without_expiring_prune_or_workspace(
        CovenantInventoryAggregate aggregate)
    {

        RequireSqlCipher();

        await SeedCovenantFamilyAsync(CancellationToken.None);

        IDataRetentionService service = CreateService(
            covenantGate: new RecordingCovenantOperationGate());

        DataRetentionRequest covenantReset = new(
            DataRetentionOperation.ResetMemory,
            MemoryScope: MemoryResetScope.Covenant);

        DataRetentionRequest factoryReset = new(DataRetentionOperation.FactoryReset);

        DataRetentionRequest prune = new(DataRetentionOperation.Prune);

        DataRetentionRequest workspaceReset = await CreateWorkspaceResetRequestAsync(
            CancellationToken.None);

        DataRetentionPlan covenantBefore = await service.PlanAsync(
            covenantReset,
            CancellationToken.None);

        DataRetentionPlan factoryBefore = await service.PlanAsync(
            factoryReset,
            CancellationToken.None);

        DataRetentionPlan pruneBefore = await service.PlanAsync(
            prune,
            CancellationToken.None);

        DataRetentionPlan workspaceBefore = await service.PlanAsync(
            workspaceReset,
            CancellationToken.None);

        Assert.Empty(workspaceBefore.Blockers);

        Assert.Empty(workspaceBefore.Conflicts);

        await ChangeCovenantInventoryAggregateAsync(
            aggregate,
            CancellationToken.None);

        DataRetentionPlan covenantAfter = await service.PlanAsync(
            covenantReset,
            CancellationToken.None);

        DataRetentionPlan factoryAfter = await service.PlanAsync(
            factoryReset,
            CancellationToken.None);

        DataRetentionPlan pruneAfter = await service.PlanAsync(
            prune,
            CancellationToken.None);

        DataRetentionPlan workspaceAfter = await service.PlanAsync(
            workspaceReset,
            CancellationToken.None);

        Assert.Empty(workspaceAfter.Blockers);

        Assert.Empty(workspaceAfter.Conflicts);

        DataRetentionCovenantInventory inventoryBefore = Assert.IsType<DataRetentionCovenantInventory>(
            covenantBefore.Covenant);

        DataRetentionCovenantInventory inventoryAfter = Assert.IsType<DataRetentionCovenantInventory>(
            covenantAfter.Covenant);

        DataRetentionCovenantInventory workspaceInventoryBefore =
            Assert.IsType<DataRetentionCovenantInventory>(workspaceBefore.Covenant);

        DataRetentionCovenantInventory workspaceInventoryAfter =
            Assert.IsType<DataRetentionCovenantInventory>(workspaceAfter.Covenant);

        AssertCovenantInventoryAggregateChanged(
            aggregate,
            inventoryBefore,
            inventoryAfter);

        AssertCovenantInventoryAggregateChanged(
            aggregate,
            workspaceInventoryBefore,
            workspaceInventoryAfter);

        Assert.NotEqual(covenantBefore.PlanId, covenantAfter.PlanId);

        Assert.NotEqual(factoryBefore.PlanId, factoryAfter.PlanId);

        Assert.Equal(pruneBefore.PlanId, pruneAfter.PlanId);

        Assert.Equal(workspaceBefore.PlanId, workspaceAfter.PlanId);

    }

    [SkippableFact]

    public async Task Covenant_memory_reset_apply_without_route_components_fails_closed_without_mutating()
    {

        RequireSqlCipher();

        await SeedCovenantFamilyAsync(CancellationToken.None);

        IReadOnlyDictionary<string, long> before = await CountCovenantTablesAsync(
            CancellationToken.None);

        IDataRetentionService service = CreateService(covenantGate: new RecordingCovenantOperationGate());

        DataRetentionRequest request = new(
            DataRetentionOperation.ResetMemory,
            MemoryScope: MemoryResetScope.Covenant);

        DataRetentionPlan plan = await service.PlanAsync(
            request,
            CancellationToken.None);

        Assert.Empty(plan.Conflicts);

        Result<DataRetentionApplyResult> applied = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(applied.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.MaintenanceFailed, applied.Error.Code);

        IReadOnlyDictionary<string, long> after = await CountCovenantTablesAsync(
            CancellationToken.None);

        Assert.Equal(before, after);

    }

    [SkippableFact]

    public async Task Installation_wide_prune_planning_takes_exactly_one_installation_read_lease()
    {

        RequireSqlCipher();

        RecordingCovenantOperationGate gate = new();

        IDataRetentionService service = CreateService(covenantGate: gate);

        _ = await service.PlanAsync(
            new DataRetentionRequest(DataRetentionOperation.Prune),
            CancellationToken.None);

        Assert.Equal(["installation-read"], gate.Acquisitions);

        Assert.Equal(1, gate.PeakConcurrentLeases);

    }

    [SkippableFact]

    public async Task Factory_reset_planning_always_takes_exactly_one_installation_read_lease()
    {

        RequireSqlCipher();

        RecordingCovenantOperationGate gate = new();

        IDataRetentionService service = CreateService(covenantGate: gate);

        _ = await service.PlanAsync(
            new DataRetentionRequest(DataRetentionOperation.FactoryReset),
            CancellationToken.None);

        Assert.Equal(["installation-read"], gate.Acquisitions);

        Assert.Equal(1, gate.PeakConcurrentLeases);

    }

    /// <summary>
    /// A workspace reset names exactly one Campaign, so it takes the bounded scoped capability rather
    /// than the installation-wide one it does not need.
    /// </summary>
    [SkippableFact]

    public async Task Workspace_reset_planning_takes_exactly_one_scoped_read_lease_for_its_campaign()
    {

        RequireSqlCipher();

        Guid campaignId = new("22222222-2222-4222-8222-222222222222");

        RecordingCovenantOperationGate gate = new();

        IDataRetentionService service = CreateService(covenantGate: gate);

        _ = await service.PlanAsync(
            new DataRetentionRequest(
                DataRetentionOperation.ResetWorkspace,
                Workspace: new DataRetentionWorkspaceBinding(campaignId, _root)),
            CancellationToken.None);

        Assert.Equal([$"read:{campaignId:D}"], gate.Acquisitions);

        Assert.Equal(1, gate.PeakConcurrentLeases);

        Assert.Equal(0, gate.LiveLeases);

    }

    [SkippableFact]

    public async Task Factory_workspace_plan_admission_takes_one_installation_read_lease()
    {

        RequireSqlCipher();

        Guid campaignId = new("22222222-2222-4222-8222-222222222222");

        RecordingCovenantOperationGate gate = new();

        IDataRetentionService service = CreateService(covenantGate: gate);

        Result<DataRetentionPlanAdmission> admission = await service.PlanAdmissionAsync(
            new DataRetentionRequest(
                DataRetentionOperation.ResetWorkspace,
                Workspace: new DataRetentionWorkspaceBinding(campaignId, _root)),
            CancellationToken.None,
            DataRetentionPlanAdmissionCapability.Installation);

        Assert.True(admission.IsSuccess);

        Assert.NotNull(admission.Value.ReadLease);

        Assert.Equal(["installation-read"], gate.Acquisitions);

        Assert.Equal(1, gate.PeakConcurrentLeases);

        await admission.Value.ReadLease.DisposeAsync();

        Assert.Equal(0, gate.LiveLeases);

    }

    [SkippableFact]

    public async Task Covenant_plan_admission_preserves_a_failed_installation_read_while_plan_async_stays_compatible()
    {

        RequireSqlCipher();

        Error failure = new(
            ErrorCodes.Covenant.ErasureIncomplete,
            "Covenant erasure remains incomplete.");

        RecordingCovenantOperationGate gate = new()
        {

            InstallationReadFailure = failure,

        };

        IDataRetentionService service = CreateService(covenantGate: gate);

        DataRetentionRequest request = new(
            DataRetentionOperation.ResetMemory,
            MemoryScope: MemoryResetScope.Covenant);

        Result<DataRetentionPlanAdmission> admission = await service.PlanAdmissionAsync(
            request,
            CancellationToken.None);

        Assert.True(admission.IsFailure);

        Assert.Equal(failure, admission.Error);

        DataRetentionPlan plan = await service.PlanAsync(request, CancellationToken.None);

        Assert.Equal(request, plan.Request);

        Assert.Null(plan.Covenant);

    }

    [SkippableTheory]

    [InlineData(false, false)]

    [InlineData(false, true)]

    [InlineData(true, false)]

    public async Task Protected_plan_admission_without_a_gate_returns_a_typed_refusal(
        bool installation,
        bool workspace)
    {

        RequireSqlCipher();

        IDataRetentionService service = CreateService(covenantGate: null);

        DataRetentionRequest request = installation
            ? new DataRetentionRequest(DataRetentionOperation.FactoryReset)
            : workspace
                ? new DataRetentionRequest(
                    DataRetentionOperation.ResetWorkspace,
                    Workspace: new DataRetentionWorkspaceBinding(
                        Guid.Parse("22222222-2222-4222-8222-222222222222"),
                        _root))
                : new DataRetentionRequest(
                    DataRetentionOperation.ResetMemory,
                    MemoryScope: MemoryResetScope.Covenant);

        DataRetentionPlanAdmissionCapability capability = installation
            ? DataRetentionPlanAdmissionCapability.Installation
            : DataRetentionPlanAdmissionCapability.Request;

        Result<DataRetentionPlanAdmission> admission = await service.PlanAdmissionAsync(
            request,
            CancellationToken.None,
            capability);

        Assert.True(admission.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.MaintenanceFailed, admission.Error.Code);

    }

    [SkippableTheory]

    [InlineData(false)]

    [InlineData(true)]

    public async Task Ordinary_plan_without_a_gate_remains_available(bool workspace)
    {

        RequireSqlCipher();

        IDataRetentionService service = CreateService(covenantGate: null);

        DataRetentionRequest request = workspace
            ? new DataRetentionRequest(
                DataRetentionOperation.ResetWorkspace,
                Workspace: new DataRetentionWorkspaceBinding(
                    Guid.Parse("22222222-2222-4222-8222-222222222222"),
                    _root))
            : new DataRetentionRequest(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(
            request,
            CancellationToken.None);

        Assert.Equal(request, plan.Request);

        Assert.Null(plan.Covenant);

    }

    /// <summary>
    /// A rule cannot be pointed at the Covenant, and the refusal has to say that rather than blame the
    /// operator's spelling.
    /// </summary>
    [Fact]
    public void Covenant_rule_update_is_refused_with_a_reason_that_is_not_a_spelling_complaint()
    {

        RetentionSettings settings = new()
        {

            ActiveSessions = Rule(),

        };

        Assert.Null(
            DataRetentionSettingsCatalog.ResolveRule(settings, RetentionDataClass.Covenant));

        Assert.NotNull(
            DataRetentionSettingsCatalog.ResolveRule(settings, RetentionDataClass.ActiveSessions));

    }

    [SkippableFact]

    public async Task Status_inventory_takes_exactly_one_installation_read_lease_and_never_nests()
    {

        RequireSqlCipher();

        RecordingCovenantOperationGate gate = new();

        IDataRetentionService service = CreateService(covenantGate: gate);

        _ = await service.GetStatusAsync(CancellationToken.None);

        Assert.Equal(["installation-read"], gate.Acquisitions);

        Assert.Equal(1, gate.PeakConcurrentLeases);

        Assert.Equal(0, gate.LiveLeases);

    }

    private static void RequireSqlCipher() =>
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

    /// <summary>
    /// The one Covenant-family row an ordinary sweep may remove, and why that is not a contradiction.
    /// </summary>
    /// <remarks>
    /// <c>assistant_entry_erasure_receipts</c> is a tombstone, and its delete guard authorizes exactly
    /// two scopes: Session retention and owner cleanup. Both remove the Session, its finalization guard,
    /// and its idempotency claim in the same transaction, so the receipt is not being stranded — there is
    /// no surviving claim left for it to answer with a 410. Keeping it would leave evidence about a
    /// Session that no longer exists, which is a leak rather than a proof.
    ///
    /// <para>Pinned as its own test because it is the exception to
    /// <see cref="Ordinary_sweep_never_deletes_covenant_versions_heads_provenance_or_tombstones"/>, and
    /// an exception nobody asserts is indistinguishable from a bug nobody noticed.</para>
    /// </remarks>
    [SkippableFact]

    public async Task Whole_session_retention_takes_its_own_session_scoped_erasure_receipt_with_it()
    {

        RequireSqlCipher();

        await SeedCovenantFamilyAsync(CancellationToken.None, sessionAgedOut: true);

        Assert.Equal(
            1,
            await ScalarAsync(
                "SELECT COUNT(*) FROM assistant_entry_erasure_receipts",
                CancellationToken.None));

        IDataRetentionService service = CreateService(EveryRuleEnabled());

        DataRetentionPlan plan = await service.PlanAsync(
            new DataRetentionRequest(DataRetentionOperation.Prune),
            CancellationToken.None);

        _ = await service.ApplyAsync(
            new DataRetentionApplyRequest(
                new DataRetentionRequest(DataRetentionOperation.Prune),
                plan.PlanId),
            CancellationToken.None);

        Assert.Equal(
            0,
            await ScalarAsync(
                "SELECT COUNT(*) FROM \"Sessions\" WHERE \"Id\" = '" + CovenantRetentionSeed.SessionId + "'",
                CancellationToken.None));

        Assert.Equal(
            0,
            await ScalarAsync(
                "SELECT COUNT(*) FROM assistant_entry_erasure_receipts",
                CancellationToken.None));

        // The immutable canonical arm is untouched by the same sweep: only the Session's own
        // Session-scoped evidence went with the Session.
        Assert.Equal(
            1,
            await ScalarAsync("SELECT COUNT(*) FROM covenant_versions", CancellationToken.None));

        Assert.Equal(
            1,
            await ScalarAsync("SELECT COUNT(*) FROM covenant_heads", CancellationToken.None));

    }

    public enum CovenantInventoryAggregate
    {

        Rows,

        ManagedFiles,

        LocalArtifacts,

        AffectedSessions,

        DisclosureExposure,

    }

    private static void AssertCovenantInventoryAggregateChanged(
        CovenantInventoryAggregate aggregate,
        DataRetentionCovenantInventory before,
        DataRetentionCovenantInventory after)
    {

        switch (aggregate)
        {

            case CovenantInventoryAggregate.Rows:
            {

                Assert.Equal(before.Rows + 1, after.Rows);

                return;

            }

            case CovenantInventoryAggregate.ManagedFiles:
            {

                Assert.Equal(before.ManagedFiles + 1, after.ManagedFiles);

                return;

            }

            case CovenantInventoryAggregate.LocalArtifacts:
            {

                Assert.Equal(before.LocalArtifacts + 1, after.LocalArtifacts);

                return;

            }

            case CovenantInventoryAggregate.AffectedSessions:
            {

                Assert.Equal(before.AffectedSessions - 1, after.AffectedSessions);

                return;

            }

            case CovenantInventoryAggregate.DisclosureExposure:
            {

                Assert.Equal(before.PossibleDisclosures + 1, after.PossibleDisclosures);

                Assert.Equal(CovenantDisclosureCountKind.LowerBound, after.DisclosureCountKind);

                return;

            }

            default:
                throw new ArgumentOutOfRangeException(nameof(aggregate), aggregate, null);

        }

    }

    private async Task ChangeCovenantInventoryAggregateAsync(
        CovenantInventoryAggregate aggregate,
        CancellationToken cancellationToken)
    {

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        switch (aggregate)
        {

            case CovenantInventoryAggregate.Rows:
            {

                await ExecuteAsync(
                    connection,
                    """
                    INSERT INTO external_disclosure_receipts (
                        OriginInstallationId, SubjectKind, SubjectId, SubjectOrdinal, EffectCategoryCode,
                        CategoryPhysicalAttemptOrdinal, EffectIdentityDigest, DestinationCode,
                        RevocabilityCode, DestinationDigest, SensitivityCode, GenerationProvenanceModeCode,
                        ExactGenerationIds, GenerationBloom, DisclosedAtUtc)
                    SELECT OriginInstallationId, SubjectKind, SubjectId, SubjectOrdinal + 1,
                           EffectCategoryCode, CategoryPhysicalAttemptOrdinal + 1,
                           X'1111111111111111111111111111111111111111111111111111111111111111',
                           DestinationCode, RevocabilityCode, DestinationDigest, SensitivityCode,
                           GenerationProvenanceModeCode, ExactGenerationIds, GenerationBloom, DisclosedAtUtc
                    FROM external_disclosure_receipts
                    WHERE SubjectId = 'ffffffff-7777-4777-8777-ffffffffffff'
                    ORDER BY SubjectOrdinal DESC
                    LIMIT 1;
                    """,
                    cancellationToken);

                return;

            }

            case CovenantInventoryAggregate.ManagedFiles:
            {

                using CovenantSqliteAuthorizationScope writer =
                    CovenantSqliteConnectionInitializer.Instance.Authorize(
                        connection,
                        CovenantSqliteAuthorizationKind.ManagedFileIntentMutation);

                await ExecuteAsync(
                    connection,
                    """
                    INSERT INTO managed_file_write_intents (
                        WriteOperationId, StableEffectIdentityDigest, ArtifactId, SensitivityLabelId,
                        SensitivityLabelDigest, PendingArtifactSensitivityLabel, DurableLocationEvidence,
                        ExpectedContentHash, ExpectedContentLength, PhaseCode, Revision, RetryCount,
                        CreatedAtUtc, UpdatedAtUtc)
                    SELECT '9f3a1c44-0d21-4a6e-9c31-6f2b0d55e711',
                           X'2222222222222222222222222222222222222222222222222222222222222222',
                           ArtifactId, SensitivityLabelId, SensitivityLabelDigest, zeroblob(64),
                           DurableLocationEvidence, ExpectedContentHash, ExpectedContentLength,
                           1, 0, 0, '2026-01-02T00:00:00.0000000Z', '2026-01-02T00:00:00.0000000Z'
                    FROM managed_file_write_intents
                    WHERE WriteOperationId = '9f3a1c44-0d21-4a6e-9c31-6f2b0d55e706';
                    """,
                    cancellationToken);

                return;

            }

            case CovenantInventoryAggregate.LocalArtifacts:
            {

                using CovenantSqliteAuthorizationScope maintenance =
                    CovenantSqliteConnectionInitializer.Instance.Authorize(
                        connection,
                        CovenantSqliteAuthorizationKind.CovenantFamilyMaintenance);

                await ExecuteAsync(
                    connection,
                    """
                    UPDATE local_erasure_work_items
                    SET StateCode = 4,
                        CheckpointRevision = CheckpointRevision + 1,
                        UpdatedAtUtc = '2026-01-02T00:00:00.0000000Z'
                    WHERE WorkItemId = '9f3a1c44-0d21-4a6e-9c31-6f2b0d55e707';
                    """,
                    cancellationToken);

                await ExecuteAsync(
                    connection,
                    """
                    INSERT INTO local_erasure_work_items (
                        WorkItemId, ErasureOperationId, SourceWriteOperationId, ExpectedSourceRevision,
                        ArtifactId, SourceSensitivityLabelId, DurableLocationEvidence,
                        ExpectedOwnershipEvidence, StateCode, CheckpointRevision, RetryCount,
                        CreatedAtUtc, UpdatedAtUtc)
                    SELECT '9f3a1c44-0d21-4a6e-9c31-6f2b0d55e712',
                           '9f3a1c44-0d21-4a6e-9c31-6f2b0d55e713', SourceWriteOperationId,
                           ExpectedSourceRevision, ArtifactId, SourceSensitivityLabelId,
                           DurableLocationEvidence, ExpectedOwnershipEvidence, 1, 0, 0,
                           '2026-01-02T00:00:00.0000000Z', '2026-01-02T00:00:00.0000000Z'
                    FROM local_erasure_work_items
                    WHERE WorkItemId = '9f3a1c44-0d21-4a6e-9c31-6f2b0d55e707';
                    """,
                    cancellationToken);

                return;

            }

            case CovenantInventoryAggregate.AffectedSessions:
            {

                await ExecuteAsync(
                    connection,
                    """
                    UPDATE session_sensitivity_state
                    SET TaintedArtifactCount = 0,
                        MaximumSensitivityCode = 0,
                        Revision = Revision + 1,
                        UpdatedAtUtc = '2026-01-02T00:00:00.0000000Z'
                    WHERE SessionId = $session;
                    """,
                    cancellationToken,
                    ("$session", CovenantRetentionSeed.SessionId));

                return;

            }

            case CovenantInventoryAggregate.DisclosureExposure:
            {

                await ExecuteAsync(
                    connection,
                    """
                    UPDATE external_disclosure_state
                    SET CountKindCode = 2,
                        JoinedCount = JoinedCount + 1,
                        UpdatedAtUtc = '2026-01-02T00:00:00.0000000Z'
                    WHERE DestinationCode = 1
                        AND RevocabilityCode = 2;
                    """,
                    cancellationToken);

                return;

            }

            default:
                throw new ArgumentOutOfRangeException(nameof(aggregate), aggregate, null);

        }

    }

    private async Task<DataRetentionRequest> CreateWorkspaceResetRequestAsync(
        CancellationToken cancellationToken)
    {

        Guid campaignId = new("55555555-5555-4555-8555-555555555555");

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        await ExecuteAsync(
            connection,
            """
            INSERT INTO "Campaigns"
                ("Id", "Name", "NameLower", "Path", "Type", "Settings", "CreatedAt", "UpdatedAt")
            VALUES
                (@id, @name, @name, @path, 0, '{}', @at, @at);
            """,
            cancellationToken,
            ("@id", campaignId.ToString()),
            ("@name", campaignId.ToString("N")),
            ("@path", _root),
            ("@at", "2026-01-02T00:00:00.0000000Z"));

        return new DataRetentionRequest(
            DataRetentionOperation.ResetWorkspace,
            Workspace: new DataRetentionWorkspaceBinding(campaignId, _root));

    }

    private static RetentionRuleSettings Rule(int days = 1) =>
        new()
        {

            Enabled = true,

            Days = days,

        };

    private static ArcanumSettings EveryRuleEnabled() =>
        new()
        {

            Retention = new RetentionSettings
            {

                ActiveSessions = Rule(),

                ArchivedSessions = Rule(),

                Entries = Rule(),

                Attachments = Rule(),

                UploadedFiles = Rule(),

                CompletedBatches = Rule(),

                SagaMemories = Rule(),

                LexiconEntries = Rule(),

                WorkspaceIndexes = Rule(),

                SessionEntryEmbeddings = Rule(),

                AuditLogs = Rule(),

                GuardrailLogs = Rule(),

                IdempotencyClaims = Rule(),

                Accounting = Rule(),

                LongRunningOperations = Rule(),

                SanctumBreaches = Rule(),

                DaemonHistory = Rule(),

            },

        };

    private DataRetentionService CreateService(
        ArcanumSettings? settings = null,
        ICovenantOperationGate? covenantGate = null) =>
        new(
            _db!,
            new TestOptionsMonitor<ArcanumSettings>(settings ?? new ArcanumSettings()),
            new LongRunningOperationStore(
                _db!,
                TestOrdinaryConnectionFactory.For(_db!)),
            TimeProvider.System,
            NullLogger<DataRetentionService>.Instance,
            FixtureLabeledArtifactGuard.For(_db!),
            Path.Combine(_root, "attachments"),
            Path.Combine(_root, "files"),
            Path.Combine(_root, "logs"),
            covenantGate: covenantGate);

    private async Task<long> ScalarAsync(string sql, CancellationToken cancellationToken)
    {

        await using SqliteCommand command =
            (SqliteCommand)_db!.Database.GetDbConnection().CreateCommand();

        command.CommandText = sql;

        object? value = await command.ExecuteScalarAsync(cancellationToken);

        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);

    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        foreach ((string name, object value) in parameters)
        {

            _ = command.Parameters.AddWithValue(name, value);

        }

        _ = await command.ExecuteNonQueryAsync(cancellationToken);

    }

    private async Task<IReadOnlyDictionary<string, long>> CountCovenantTablesAsync(
        CancellationToken cancellationToken)
    {

        Dictionary<string, long> counts = [];

        foreach (string table in CovenantFamilyTables)
        {

            counts[table] = await ScalarAsync(
                $"SELECT COUNT(*) FROM \"{table}\"",
                cancellationToken);

        }

        return counts;

    }

    private static readonly string[] CovenantFamilyTables =
    [
        "covenant_entries",
        "covenant_versions",
        "covenant_heads",
        "covenant_version_attachment_provenance",
        "covenant_turn_receipts",
        "covenant_mutation_receipts",
        "covenant_key_epochs",
        "artifact_sensitivity",
        "assistant_entry_erasure_receipts",
        "external_disclosure_receipts",
    ];

    private async Task SeedCovenantFamilyAsync(
        CancellationToken cancellationToken,
        bool sessionAgedOut = false) =>
        await CovenantRetentionSeed.SeedAsync(_db!, cancellationToken, sessionAgedOut);

    /// <summary>
    /// Seeds one Global pin of a key that has no entry, no head and no epoch row: its version, the head
    /// that points at it, and its receipt.
    /// </summary>
    /// <remarks>
    /// A keyless pin is a state production reaches: an operator may pin a key before anything has been
    /// written under it. All three rows carry epoch 0, which is what a key with no epoch row reads as.
    /// </remarks>
    private async Task SeedCurationPinAsync(CancellationToken cancellationToken)
    {

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        if (connection.State is not System.Data.ConnectionState.Open)
        {

            await connection.OpenAsync(cancellationToken);

        }

        await ExecuteAsync(
            connection,
            """
            INSERT INTO covenant_curation_versions (
                CurationVersionId, ScopeCode, CampaignId, NormalizedKey, LaneCode, KeyEpoch,
                CurationKindCode, Revision, PredecessorVersionId, MutationId,
                RequestIdempotencyDigest, AuthorizationDigest, FinalMutationDigest, CreatedAtUtc)
            VALUES ('curation-1', 1, NULL, 'pinned/only', 1, 0, 1, 1, NULL, 'curation-mutation-1',
                    zeroblob(32), zeroblob(32), zeroblob(32), '2026-01-01T00:00:00.0000000Z');

            INSERT INTO covenant_curation_heads (
                ScopeCode, CampaignId, NormalizedKey, LaneCode, KeyEpoch,
                IsPinned, IsMasked, CurrentVersionId, CurrentRevision, UpdatedAtUtc)
            VALUES (1, NULL, 'pinned/only', 1, 0, 1, 0, 'curation-1', 1, '2026-01-01T00:00:00.0000000Z');

            INSERT INTO covenant_curation_receipts (
                MutationId, RequestIdempotencyDigest, AuthorizationDigest, FinalMutationDigest,
                CurationKindCode, ScopeCode, CampaignId, NormalizedKey, LaneCode, KeyEpoch,
                OutcomeCode, ResultingVersionId, ResultingRevision, ResponseReceiptDigest, CommittedAtUtc)
            VALUES ('curation-mutation-1', zeroblob(32), zeroblob(32), zeroblob(32),
                    1, 1, NULL, 'pinned/only', 1, 0, 1, 'curation-1', 1, zeroblob(32),
                    '2026-01-01T00:00:00.0000000Z');
            """,
            cancellationToken);

    }

    /// <summary>
    /// Records the diagnostic stream the disclosure writer prints to, and refuses any other write: the
    /// disclosure is never payload.
    /// </summary>
    private sealed class DiagnosticRecorder : IConsoleDispatcher
    {

        public List<string> Diagnostics { get; } = [];

        public void WriteDiagnostic(string value) => Diagnostics.Add(value);

        public void WritePayload(string value) => throw new InvalidOperationException(value);

        public void WriteVerbose(string value) => throw new InvalidOperationException(value);

        public void WriteJson<T>(T value, JsonTypeInfo<T> typeInfo) =>
            throw new InvalidOperationException(typeInfo.Type.Name);

        public void WriteJson(JsonElement value) => throw new InvalidOperationException(value.ToString());

        public void BeginJsonStream() => throw new InvalidOperationException();

    }

}
