using System.Globalization;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// The schema-repair journal and the pre-readiness recovery that resumes it.
/// </summary>
/// <remarks>
/// Repair mutates the catalog, so the journal is the only thing that can say who owned an interrupted
/// operation. These assertions are about what it refuses to forget: an immutable owner, one edge at a
/// time, and a terminal phase only after a disposition actually succeeded (§10.17).
/// </remarks>
public sealed class CovenantSchemaRepairTests
{

    private static CancellationToken Token => CancellationToken.None;

    [Fact]
    public async Task A_prepared_journal_is_committed_before_any_repair_and_carries_its_exact_owner()
    {

        await using RepairFixture fixture = await RepairFixture.CreateAsync();

        CovenantSchemaRepairIntent intent = fixture.Intent(CovenantSchemaRepairPhase.Prepared);

        Result committed = await CovenantSchemaRepairJournal.CommitPreparedAsync(
            fixture.Connection,
            CovenantSqliteConnectionInitializer.Instance,
            intent,
            DateTimeOffset.UnixEpoch,
            Token);

        Assert.True(committed.IsSuccess);

        Result<CovenantSchemaRepairIntent?> active = await CovenantSchemaRepairJournal
            .TryReadActiveAsync(fixture.Connection, Token);

        Assert.True(active.IsSuccess);

        Assert.NotNull(active.Value);

        Assert.Equal(intent.OperationId, active.Value.Owner.OperationId);

        Assert.Equal(CovenantExclusiveOperation.SchemaRepair, active.Value.Owner.Operation);

        Assert.Equal(intent.EffectDigest, active.Value.Owner.EffectDigest);

        Assert.True(active.Value.IsActive);

    }

    [Fact]
    public async Task The_journal_follows_only_the_committed_path_or_the_proven_no_mutation_path()
    {

        await using RepairFixture fixture = await RepairFixture.CreateAsync();

        CovenantSchemaRepairIntent prepared = await fixture.CommitAsync();

        // Prepared may reach CatalogCommitted or, on the proven no-mutation path, ReopenPending. It
        // may never jump straight to a terminal phase; only the finalizer gets there.
        Assert.True((await fixture.AdvanceAsync(prepared, CovenantSchemaRepairPhase.Completed)).IsFailure);

        Result<bool> committed = await fixture.AdvanceAsync(prepared, CovenantSchemaRepairPhase.CatalogCommitted);

        Assert.True(committed.IsSuccess);

        Assert.True(committed.Value);

        CovenantSchemaRepairIntent atCommitted = prepared with
        {
            Phase = CovenantSchemaRepairPhase.CatalogCommitted,
            Revision = prepared.Revision + 1,
        };

        Assert.True((await fixture.AdvanceAsync(atCommitted, CovenantSchemaRepairPhase.ReopenPending)).IsFailure);

        Assert.True((await fixture.AdvanceAsync(atCommitted, CovenantSchemaRepairPhase.HealthVerified)).Value);

    }

    [Fact]
    public async Task A_stale_revision_never_advances_the_journal_twice()
    {

        await using RepairFixture fixture = await RepairFixture.CreateAsync();

        CovenantSchemaRepairIntent prepared = await fixture.CommitAsync();

        Assert.True((await fixture.AdvanceAsync(prepared, CovenantSchemaRepairPhase.CatalogCommitted)).Value);

        Result<bool> replayed = await fixture.AdvanceAsync(prepared, CovenantSchemaRepairPhase.CatalogCommitted);

        Assert.True(replayed.IsSuccess);

        Assert.False(replayed.Value);

    }

    [Fact]
    public async Task Recovery_with_no_journal_reports_no_active_work_and_never_closes_admission()
    {

        await using RepairFixture fixture = await RepairFixture.CreateAsync();

        using MaintenanceLockScope heldLock = fixture.AcquireLock();

        StubSchemaRepairExecutor executor = new();

        CovenantSchemaRepairStartupRecovery recovery = fixture.Recovery(executor);

        Result<CovenantSchemaRepairStartupRecoveryPreparation> prepared = await recovery
            .PrepareBeforeEffectsAsync(heldLock.Lock, heldLock.Directory, fixture.Connection, Token);

        Assert.True(prepared.IsSuccess);

        Assert.Equal(0, executor.InspectCalls);

        Result<CovenantSchemaRepairStartupRecoveryOutcome> recovered = await recovery
            .RecoverPreparedAsync(
                heldLock.Lock,
                heldLock.Directory,
                fixture.Connection,
                prepared.Value,
                Token);

        Assert.True(recovered.IsSuccess);

        Assert.Equal(CovenantSchemaRepairStartupRecoveryOutcome.NoActiveJournal, recovered.Value);

    }

    [Fact]
    public async Task Owner_conflict_is_refused_in_the_effect_free_prepass()
    {

        await using RepairFixture fixture = await RepairFixture.CreateAsync();

        _ = await fixture.CommitAsync();

        fixture.Gate.AdoptDurableRecoveryOwner(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantReset),
            scope: null,
            cleanupOnlyHistoricalCampaign: false);

        using MaintenanceLockScope heldLock = fixture.AcquireLock();

        StubSchemaRepairExecutor executor = new();

        Result<CovenantSchemaRepairStartupRecoveryPreparation> prepared = await fixture
            .Recovery(executor)
            .PrepareBeforeEffectsAsync(
                heldLock.Lock,
                heldLock.Directory,
                fixture.Connection,
                Token);

        Assert.True(prepared.IsFailure);

        Assert.Equal(0, executor.InspectCalls);

        Assert.Equal(0, executor.RepairCalls);

    }

    [Fact]
    public async Task Malformed_journal_values_fail_content_free_before_any_effect()
    {

        await using RepairFixture fixture = await RepairFixture.CreateAsync();

        await using (SqliteCommand command = fixture.Connection.CreateCommand())
        {

            command.CommandText =
                """
                PRAGMA ignore_check_constraints = ON;
                INSERT INTO covenant_schema_repair_intents (
                    OperationId, EffectDigest, InspectedCatalogDigest, RepairActionCode, TargetTierCode,
                    CapturedDatasetGeneration, AuthorityEpoch, PhaseCode, Revision, LastDurableErrorCode,
                    CreatedAtUtc, UpdatedAtUtc)
                VALUES ('private malformed identifier', X'01', zeroblob(32), 99, 'tier', NULL,
                    -1, 'phase', -1, NULL, 'now', 'now');
                """;

            _ = await command.ExecuteNonQueryAsync();

        }

        using MaintenanceLockScope heldLock = fixture.AcquireLock();

        StubSchemaRepairExecutor executor = new();

        Result<CovenantSchemaRepairStartupRecoveryPreparation> prepared = await fixture
            .Recovery(executor)
            .PrepareBeforeEffectsAsync(
                heldLock.Lock,
                heldLock.Directory,
                fixture.Connection,
                Token);

        Assert.True(prepared.IsFailure);

        Assert.DoesNotContain("private malformed identifier", prepared.Error.Message, StringComparison.Ordinal);

        Assert.Equal(0, executor.InspectCalls);

    }

    [Fact]
    public async Task Journal_read_preserves_caller_cancellation()
    {

        await using RepairFixture fixture = await RepairFixture.CreateAsync();

        using CancellationTokenSource cancelled = new();

        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CovenantSchemaRepairJournal.TryReadActiveAsync(
                fixture.Connection,
                cancelled.Token));

    }

    [Fact]
    public async Task Recovery_resumes_the_exact_journaled_owner_and_completes_a_repaired_catalog()
    {

        await using RepairFixture fixture = await RepairFixture.CreateAsync();

        CovenantSchemaRepairIntent prepared = await fixture.CommitAsync();

        using MaintenanceLockScope heldLock = fixture.AcquireLock();

        StubSchemaRepairExecutor executor = new()
        {
            Inspection = fixture.Inspection(prepared.InspectedCatalogDigest, canonicalValid: true),

            Mutated = true,
        };

        Result<CovenantSchemaRepairStartupRecoveryOutcome> recovered = await fixture
            .Recovery(executor)
            .RecoverBeforeReadinessAsync(heldLock.Lock, heldLock.Directory, fixture.Connection, Token);

        Assert.True(recovered.IsSuccess);

        Assert.Equal(CovenantSchemaRepairStartupRecoveryOutcome.RecoveredReady, recovered.Value);

        Assert.Equal(
            (long)CovenantSchemaRepairPhase.Completed,
            await fixture.ScalarAsync("SELECT PhaseCode FROM covenant_schema_repair_intents;"));

    }

    [Fact]
    public async Task Inspection_does_not_mistake_a_core_covenant_table_for_the_canonical_family()
    {

        await using RepairFixture fixture = await RepairFixture.CreateAsync();

        // The fixture installs Core-tier objects only, and covenant_schema_repair_intents is one of them:
        // Core owns two tables that happen to start with 'covenant_'. Answering "the canonical family is
        // present" from a name prefix therefore answers yes on every installation ever created, including
        // one whose canonical family is genuinely absent — the single catalog InstallAbsentCanonicalFamily
        // exists to repair, which it would then refuse as ManualRecoveryRequired (§10.17).
        Result<CovenantSchemaRepairInspection> inspected = await RepairFixture
            .RealExecutor()
            .InspectAsync(fixture.Connection, Token);

        Assert.True(inspected.IsSuccess);

        Assert.False(inspected.Value.CanonicalObjectsPresent);

    }

    [Fact]
    public async Task Recovery_health_checks_the_repaired_catalog_not_the_snapshot_that_justified_the_repair()
    {

        await using RepairFixture fixture = await RepairFixture.CreateAsync();

        CovenantSchemaRepairIntent prepared = await fixture.CommitAsync();

        using MaintenanceLockScope heldLock = fixture.AcquireLock();

        // The pre-state an active repair intent actually implies: the canonical tier was invalid, which is
        // the whole reason the repair exists. The resumed repair then succeeds, so the CatalogCommitted
        // health gate has to read the catalog the repair produced — re-reading the snapshot that justified
        // the repair can only ever say "still invalid" and keep admission shut for the rest of the process
        // on a repair that in fact completed (§10.17).
        StubSchemaRepairExecutor executor = new()
        {
            Inspection = fixture.Inspection(prepared.InspectedCatalogDigest, canonicalValid: false),

            PostRepairInspection = fixture.Inspection(prepared.InspectedCatalogDigest, canonicalValid: true),

            Mutated = true,
        };

        Result<CovenantSchemaRepairStartupRecoveryOutcome> recovered = await fixture
            .Recovery(executor)
            .RecoverBeforeReadinessAsync(heldLock.Lock, heldLock.Directory, fixture.Connection, Token);

        Assert.True(recovered.IsSuccess);

        Assert.Equal(CovenantSchemaRepairStartupRecoveryOutcome.RecoveredReady, recovered.Value);

        Assert.Equal(2, executor.InspectCalls);

        Assert.Equal(
            (long)CovenantSchemaRepairPhase.Completed,
            await fixture.ScalarAsync("SELECT PhaseCode FROM covenant_schema_repair_intents;"));

    }

    [Fact]
    public async Task Recovery_keeps_a_repair_whose_catalog_is_still_invalid_afterwards_closed()
    {

        await using RepairFixture fixture = await RepairFixture.CreateAsync();

        CovenantSchemaRepairIntent prepared = await fixture.CommitAsync();

        using MaintenanceLockScope heldLock = fixture.AcquireLock();

        // The other half of the same gate: a repair that changed the catalog and left it invalid is
        // exactly what KeptClosed exists to report, and re-inspecting must not soften that into a reopen.
        StubSchemaRepairExecutor executor = new()
        {
            Inspection = fixture.Inspection(prepared.InspectedCatalogDigest, canonicalValid: false),

            PostRepairInspection = fixture.Inspection(prepared.InspectedCatalogDigest, canonicalValid: false),

            Mutated = true,
        };

        Result<CovenantSchemaRepairStartupRecoveryOutcome> recovered = await fixture
            .Recovery(executor)
            .RecoverBeforeReadinessAsync(heldLock.Lock, heldLock.Directory, fixture.Connection, Token);

        Assert.True(recovered.IsSuccess);

        Assert.Equal(CovenantSchemaRepairStartupRecoveryOutcome.KeptClosed, recovered.Value);

        Assert.Equal(
            (long)CovenantSchemaRepairPhase.CatalogCommitted,
            await fixture.ScalarAsync("SELECT PhaseCode FROM covenant_schema_repair_intents;"));

    }

    [Fact]
    public async Task Recovery_abandons_a_proven_no_mutation_repair_through_a_rollback()
    {

        await using RepairFixture fixture = await RepairFixture.CreateAsync();

        CovenantSchemaRepairIntent prepared = await fixture.CommitAsync();

        using MaintenanceLockScope heldLock = fixture.AcquireLock();

        StubSchemaRepairExecutor executor = new()
        {
            Inspection = fixture.Inspection(prepared.InspectedCatalogDigest, canonicalValid: true),

            Mutated = false,
        };

        Result<CovenantSchemaRepairStartupRecoveryOutcome> recovered = await fixture
            .Recovery(executor)
            .RecoverBeforeReadinessAsync(heldLock.Lock, heldLock.Directory, fixture.Connection, Token);

        Assert.True(recovered.IsSuccess);

        Assert.Equal(CovenantSchemaRepairStartupRecoveryOutcome.RecoveredReady, recovered.Value);

        Assert.Equal(
            (long)CovenantSchemaRepairPhase.Abandoned,
            await fixture.ScalarAsync("SELECT PhaseCode FROM covenant_schema_repair_intents;"));

    }

    [Fact]
    public async Task Recovery_rejects_a_changed_catalog_digest_and_keeps_admission_closed()
    {

        await using RepairFixture fixture = await RepairFixture.CreateAsync();

        _ = await fixture.CommitAsync();

        using MaintenanceLockScope heldLock = fixture.AcquireLock();

        StubSchemaRepairExecutor executor = new()
        {
            Inspection = fixture.Inspection(CovenantOperationGateFixture.Digest(99), canonicalValid: true),
        };

        Result<CovenantSchemaRepairStartupRecoveryOutcome> recovered = await fixture
            .Recovery(executor)
            .RecoverBeforeReadinessAsync(heldLock.Lock, heldLock.Directory, fixture.Connection, Token);

        Assert.True(recovered.IsSuccess);

        Assert.Equal(CovenantSchemaRepairStartupRecoveryOutcome.KeptClosed, recovered.Value);

        Assert.Equal(0, executor.RepairCalls);

        Assert.Equal(
            (long)CovenantSchemaRepairPhase.Prepared,
            await fixture.ScalarAsync("SELECT PhaseCode FROM covenant_schema_repair_intents;"));

    }

    [Fact]
    public async Task Recovery_keeps_a_failed_repair_closed_and_leaves_its_journal_active()
    {

        await using RepairFixture fixture = await RepairFixture.CreateAsync();

        CovenantSchemaRepairIntent prepared = await fixture.CommitAsync();

        using MaintenanceLockScope heldLock = fixture.AcquireLock();

        StubSchemaRepairExecutor executor = new()
        {
            Inspection = fixture.Inspection(prepared.InspectedCatalogDigest, canonicalValid: true),

            RepairFails = true,
        };

        Result<CovenantSchemaRepairStartupRecoveryOutcome> recovered = await fixture
            .Recovery(executor)
            .RecoverBeforeReadinessAsync(heldLock.Lock, heldLock.Directory, fixture.Connection, Token);

        Assert.True(recovered.IsSuccess);

        Assert.Equal(CovenantSchemaRepairStartupRecoveryOutcome.KeptClosed, recovered.Value);

        Assert.Equal(
            (long)CovenantSchemaRepairPhase.Prepared,
            await fixture.ScalarAsync("SELECT PhaseCode FROM covenant_schema_repair_intents;"));

    }

    private sealed class StubSchemaRepairExecutor : ICovenantSchemaRepairExecutor
    {

        internal CovenantSchemaRepairInspection? Inspection { get; set; }

        /// <summary>
        /// What the catalog looks like once the repair has run. Left unset, the stub answers the same
        /// snapshot every time and so cannot tell a pre-repair catalog from a post-repair one at all.
        /// </summary>
        internal CovenantSchemaRepairInspection? PostRepairInspection { get; set; }

        internal bool Mutated { get; set; }

        internal bool RepairFails { get; set; }

        internal int RepairCalls { get; private set; }

        internal int InspectCalls { get; private set; }

        public Task<Result<CovenantSchemaRepairInspection>> InspectAsync(
            SqliteConnection connection,
            CancellationToken cancellationToken)
        {

            InspectCalls++;

            CovenantSchemaRepairInspection? answer = RepairCalls > 0 && PostRepairInspection is { } repaired
                ? repaired
                : Inspection;

            return Task.FromResult(
                answer is { } inspection
                    ? Result<CovenantSchemaRepairInspection>.Success(inspection)
                    : Result<CovenantSchemaRepairInspection>.Failure(
                        new Error(ErrorCodes.Covenant.MaintenanceFailed, "no inspection")));

        }

        public Task<Result<bool>> RepairAsync(
            SqliteConnection connection,
            CovenantSchemaRepairAction action,
            CovenantSchemaRepairInspection inspected,
            CancellationToken cancellationToken)
        {

            RepairCalls++;

            return Task.FromResult(
                RepairFails
                    ? Result<bool>.Failure(
                        new Error(ErrorCodes.Covenant.ManualRecoveryRequired, "not repairable"))
                    : Result<bool>.Success(Mutated));

        }

    }

    private sealed class MaintenanceLockScope(ArcanumMaintenanceLock heldLock, string directory) : IDisposable
    {

        internal ArcanumMaintenanceLock Lock { get; } = heldLock;

        internal string Directory { get; } = directory;

        public void Dispose() => Lock.Dispose();

    }

    private sealed class RepairFixture : IAsyncDisposable
    {

        private readonly CovenantSchemaScratchDatabase _database;

        private readonly string _root;

        private RepairFixture(CovenantSchemaScratchDatabase database, string root)
        {

            _database = database;

            _root = root;

        }

        internal SqliteConnection Connection => _database.Connection;

        internal RetroDownfall.Arcanum.Infrastructure.Covenant.CovenantOperationGate Gate { get; } =
            CovenantOperationGateFixture.CreateGate();

        internal static async Task<RepairFixture> CreateAsync()
        {

            CovenantSchemaScratchDatabase database = await CovenantSchemaScratchDatabase.CreateAsync(Token);

            string root = Path.Combine(Path.GetTempPath(), $"covenant-repair-{Guid.NewGuid():N}");

            try
            {

                _ = Directory.CreateDirectory(root);

                // The guards are the contract under test: the closed edge list, the compare-and-swap,
                // and the family-maintenance authorization all live in the triggers rather than in C#.
                await database.InstallCoreObjectsAsync(
                    [
                        "covenant_schema_repair_intents",
                        "covenant_schema_repair_intents_guard_update",
                        "covenant_schema_repair_intents_guard_delete",
                    ],
                    Token);

                return new RepairFixture(database, root);

            }
            catch
            {

                await database.DisposeAsync();

                throw;

            }

        }

        internal MaintenanceLockScope AcquireLock() =>
            new(ArcanumMaintenanceLock.TryAcquire(_root)!, _root);

        /// <summary>
        /// The shipped executor over the shipped manifests — the only way to assert what an inspection
        /// reads out of a real catalog rather than out of a stub's answer.
        /// </summary>
        internal static CovenantSchemaRepairExecutor RealExecutor()
        {

            GrimoireSchemaManifestInspector inspector = new(GrimoireSchemaTierOwnershipRegistry.CreateDefault());

            return new CovenantSchemaRepairExecutor(
                inspector,
                new GrimoireSchemaInstaller(
                    inspector,
                    new GrimoireSchemaDataInitializers(
                    [
                        new CoreGrimoireSchemaDataInitializer(),
                        new CovenantCanonicalSchemaDataInitializer(),
                        new CovenantAcceleratorSchemaDataInitializer(),
                    ])),
                new GrimoireSchemaInitializationContext(
                    "installation",
                    AuthorityEpoch: 1,
                    MasterKeyVersion: 1,
                    MasterKeyFingerprint: [1, 2, 3, 4],
                    RecoveryEnvelopeEpoch: 1,
                    DateTimeOffset.UnixEpoch),
                embeddingDimensions: 64);

        }

        internal CovenantSchemaRepairStartupRecovery Recovery(ICovenantSchemaRepairExecutor executor) =>
            new(
                Gate,
                executor,
                CovenantSqliteConnectionInitializer.Instance,
                TimeProvider.System);

        internal CovenantSchemaRepairIntent Intent(CovenantSchemaRepairPhase phase) =>
            new(
                Guid.Parse("44444444-4444-4444-8444-444444444444"),
                CovenantOperationGateFixture.Digest(7),
                CovenantOperationGateFixture.Digest(21),
                CovenantSchemaRepairAction.RepairExistingFamily,
                GrimoireSchemaTransactionTier.CovenantCanonical,
                CovenantOperationGateFixture.DatasetGeneration,
                AuthorityEpoch: 1,
                phase,
                Revision: 0);

        internal CovenantSchemaRepairInspection Inspection(CovenantDigest catalogDigest, bool canonicalValid) =>
            new(catalogDigest, "fingerprint", true, canonicalValid, canonicalValid, [], null);

        internal async Task<CovenantSchemaRepairIntent> CommitAsync()
        {

            CovenantSchemaRepairIntent intent = Intent(CovenantSchemaRepairPhase.Prepared);

            Result committed = await CovenantSchemaRepairJournal.CommitPreparedAsync(
                Connection,
                CovenantSqliteConnectionInitializer.Instance,
                intent,
                DateTimeOffset.UnixEpoch,
                Token);

            Assert.True(committed.IsSuccess);

            return intent;

        }

        internal Task<Result<bool>> AdvanceAsync(
            CovenantSchemaRepairIntent intent,
            CovenantSchemaRepairPhase next) =>
            CovenantSchemaRepairJournal.TryAdvanceAsync(
                Connection,
                CovenantSqliteConnectionInitializer.Instance,
                intent,
                next,
                lastDurableErrorCode: null,
                DateTimeOffset.UnixEpoch,
                Token);

        internal async Task<long> ScalarAsync(string sql) =>
            Convert.ToInt64(await _database.ScalarLongAsync(sql, Token), CultureInfo.InvariantCulture);

        public async ValueTask DisposeAsync()
        {

            await _database.DisposeAsync();

            try
            {

                Directory.Delete(_root, recursive: true);

            }
            catch (IOException)
            {

                // Scratch directory under the OS temp root; removal failure is not a test outcome.
            }

        }

    }

}
