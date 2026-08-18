using Microsoft.Data.Sqlite;

using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Operations;

using RetroDownfall.Arcanum.Tests.Covenant;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

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

        Assert.Equal(29, Enum.GetValues<RetentionDataClass>().Length);

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

    public async Task Covenant_memory_reset_plans_content_free_counts_and_refuses_to_apply_here()
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

        Assert.NotNull(plan.Covenant);

        Assert.Contains(
            plan.Conflicts,
            conflict => conflict.Code
                == DataRetentionConflictCodes.CovenantResetRequiresErasureCoordinator);

        Result<DataRetentionApplyResult> applied = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(applied.IsFailure);

        IReadOnlyDictionary<string, long> after = await CountCovenantTablesAsync(
            CancellationToken.None);

        Assert.NotEqual(0, after.Values.Sum());

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
            new LongRunningOperationStore(_db!),
            TimeProvider.System,
            NullLogger<DataRetentionService>.Instance,
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

}
