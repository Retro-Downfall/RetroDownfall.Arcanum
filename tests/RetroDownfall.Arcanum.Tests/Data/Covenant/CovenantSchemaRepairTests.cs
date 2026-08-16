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

        Result<CovenantSchemaRepairStartupRecoveryOutcome> recovered = await fixture
            .Recovery(new StubSchemaRepairExecutor())
            .RecoverBeforeReadinessAsync(heldLock.Lock, heldLock.Directory, fixture.Connection, Token);

        Assert.True(recovered.IsSuccess);

        Assert.Equal(CovenantSchemaRepairStartupRecoveryOutcome.NoActiveJournal, recovered.Value);

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

        internal bool Mutated { get; set; }

        internal bool RepairFails { get; set; }

        internal int RepairCalls { get; private set; }

        public Task<Result<CovenantSchemaRepairInspection>> InspectAsync(
            SqliteConnection connection,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                Inspection is { } inspection
                    ? Result<CovenantSchemaRepairInspection>.Success(inspection)
                    : Result<CovenantSchemaRepairInspection>.Failure(
                        new Error(ErrorCodes.Covenant.MaintenanceFailed, "no inspection")));

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
