using System.Globalization;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

public sealed partial class CovenantSchemaRepairTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Pending_repair_restart_preserves_its_durable_no_mutation_or_committed_origin(bool committed, bool canonicalValid)
    {
        await using RepairFixture fixture = await RepairFixture.CreateAsync();

        CovenantSchemaRepairIntent current = await fixture.CommitAsync();

        CovenantSchemaRepairPhase[] path = committed
            ? [CovenantSchemaRepairPhase.CatalogCommitted, CovenantSchemaRepairPhase.HealthVerified, CovenantSchemaRepairPhase.ReopenPending]
            : [CovenantSchemaRepairPhase.ReopenPending];

        foreach (CovenantSchemaRepairPhase phase in path)
        {
            Result<bool> advanced = await fixture.AdvanceAsync(current, phase);

            Assert.True(advanced.IsSuccess && advanced.Value);

            current = current with { Phase = phase, Revision = current.Revision + 1 };
        }

        Assert.Equal(committed ? 3L : 1L, current.Revision);

        using MaintenanceLockScope heldLock = fixture.AcquireLock();

        StubSchemaRepairExecutor executor = new()
        {
            Inspection = fixture.Inspection(current.InspectedCatalogDigest, canonicalValid),
        };

        Result<CovenantSchemaRepairStartupRecoveryOutcome> recovered = await fixture.Recovery(executor)
            .RecoverBeforeReadinessAsync(heldLock.Lock, heldLock.Directory, fixture.Connection, Token);

        Assert.True(recovered.IsSuccess);

        bool staysClosed = committed && !canonicalValid;

        Assert.Equal(staysClosed ? CovenantSchemaRepairStartupRecoveryOutcome.KeptClosed
            : CovenantSchemaRepairStartupRecoveryOutcome.RecoveredReady, recovered.Value);

        CovenantSchemaRepairPhase expected = staysClosed ? CovenantSchemaRepairPhase.ReopenPending
            : committed ? CovenantSchemaRepairPhase.Completed : CovenantSchemaRepairPhase.Abandoned;

        Assert.Equal((long)expected, await fixture.ScalarAsync("SELECT PhaseCode FROM covenant_schema_repair_intents;"));

        Assert.Equal(staysClosed ? current.Revision : current.Revision + 1,
            await fixture.ScalarAsync("SELECT Revision FROM covenant_schema_repair_intents;"));

        Assert.Equal(0, executor.RepairCalls);
    }

    [Fact]
    public async Task A_proven_non_mutating_real_repair_refusal_records_Abandoned_and_does_not_close_two_later_starts()
    {
        await using RepairFixture fixture = await RepairFixture.CreateAsync();

        FakeCovenantAvailability availability = new();

        CovenantSchemaRepairExecutor executor = RepairFixture.RealExecutor();

        CovenantMaintenanceService service = CreateMaintenanceService(
            new FixedConnectionSource(fixture.Connection), fixture.Gate, executor, availability);

        Result<CovenantSchemaRepairResultDto> result = await service.RepairSchemaAsync(
            new CovenantSchemaRepairRequest(CovenantSchemaRepairAction.RepairExistingFamily), Token);

        Assert.True(result.IsSuccess);

        long durablePhase = await fixture.ScalarAsync("SELECT PhaseCode FROM covenant_schema_repair_intents;");

        Assert.Equal((long)result.Value.Phase, durablePhase);

        Assert.Equal((long)CovenantSchemaRepairPhase.Abandoned, durablePhase);

        Assert.Equal(CovenantSchemaRepairDisposition.ManualRecoveryRequired, result.Value.Disposition);

        using MaintenanceLockScope heldLock = fixture.AcquireLock();

        for (int restart = 0; restart < 2; restart++)
        {
            CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

            CovenantSchemaRepairStartupRecovery recovery = new(
                gate, executor, CovenantSqliteConnectionInitializer.Instance, TimeProvider.System);

            Result<CovenantSchemaRepairStartupRecoveryOutcome> recovered = await recovery
                .RecoverBeforeReadinessAsync(heldLock.Lock, heldLock.Directory, fixture.Connection, Token);

            Assert.True(recovered.IsSuccess);

            Assert.Equal(CovenantSchemaRepairStartupRecoveryOutcome.NoActiveJournal, recovered.Value);

            await using CovenantInstallationReadLease read = (await gate.AcquireInstallationReadAsync(Token)).Value;
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("duplicate")]
    public async Task Repair_preparation_refuses_bad_journals_with_one_safe_actionable_diagnostic_before_effects(string damage)
    {
        await using RepairFixture fixture = await RepairFixture.CreateAsync();

        if (damage == "duplicate")
        {
            _ = await fixture.CommitAsync();

            CovenantSchemaRepairIntent second = fixture.Intent(CovenantSchemaRepairPhase.Prepared) with { OperationId = Guid.NewGuid() };

            Assert.True((await CovenantSchemaRepairJournal.CommitPreparedAsync(fixture.Connection,
                CovenantSqliteConnectionInitializer.Instance, second, DateTimeOffset.UnixEpoch, Token)).IsSuccess);
        }
        else
        {
            await using SqliteCommand corrupt = fixture.Connection.CreateCommand();

            corrupt.CommandText = damage == "missing" ? "DROP TABLE covenant_schema_repair_intents;" : """
                PRAGMA ignore_check_constraints = ON;
                INSERT INTO covenant_schema_repair_intents (
                    OperationId, EffectDigest, InspectedCatalogDigest, RepairActionCode, TargetTierCode,
                    CapturedDatasetGeneration, AuthorityEpoch, PhaseCode, Revision, LastDurableErrorCode,
                    CreatedAtUtc, UpdatedAtUtc)
                VALUES ('PRIVATE_JOURNAL_CANARY', X'01', zeroblob(32), 99, 'tier', NULL,
                    -1, 'phase', -1, NULL, 'now', 'now');
                """;

            _ = await corrupt.ExecuteNonQueryAsync(Token);
        }

        using MaintenanceLockScope heldLock = fixture.AcquireLock();

        StubSchemaRepairExecutor executor = new();

        CapturingSink sink = new();

        Serilog.ILogger previous = Serilog.Log.Logger;

        using Serilog.Core.Logger logger = new Serilog.LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();

        Serilog.Log.Logger = logger;

        Result<CovenantSchemaRepairStartupRecoveryPreparation> result;

        try
        {
            result = await fixture.Recovery(executor)
                .PrepareBeforeEffectsAsync(heldLock.Lock, heldLock.Directory, fixture.Connection, Token);
        }
        finally
        {
            Serilog.Log.Logger = previous;
        }

        Assert.True(result.IsFailure);

        Assert.Equal(0, executor.InspectCalls);

        Assert.Equal(0, executor.RepairCalls);

        Serilog.Events.LogEvent logged = Assert.Single(sink.Events,
            static item => item.Level >= Serilog.Events.LogEventLevel.Warning);

        Assert.Null(logged.Exception);

        string rendered = logged.RenderMessage(CultureInfo.InvariantCulture);

        Assert.Contains("journal", rendered, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("recovery", result.Error.Message, StringComparison.OrdinalIgnoreCase);

        foreach (string value in logged.Properties.Values.Select(static value => value.ToString()).Append(rendered).Append(result.Error.Message))
        {
            Assert.DoesNotContain("PRIVATE_JOURNAL_CANARY", value, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Unknown_repair_effects_with_an_equal_catalog_digest_cannot_be_abandoned_by_a_later_no_op()
    {
        await using RepairFixture fixture = await RepairFixture.CreateAsync();

        await using (SqliteCommand setup = fixture.Connection.CreateCommand())
        {
            setup.CommandText = "CREATE TABLE repair_data_canary(Value INTEGER NOT NULL); INSERT INTO repair_data_canary VALUES(0);";

            _ = await setup.ExecuteNonQueryAsync(Token);
        }

        CovenantSchemaRepairExecutor inspector = RepairFixture.RealExecutor();

        Result<CovenantSchemaRepairInspection> before = await inspector.InspectAsync(fixture.Connection, Token);

        Assert.True(before.IsSuccess);

        UnknownDataRepairExecutor failed = new(inspector, failAfterWrite: true);

        CovenantMaintenanceService service = CreateMaintenanceService(
            new FixedConnectionSource(fixture.Connection), fixture.Gate, failed, new FakeCovenantAvailability());

        Result<CovenantSchemaRepairResultDto> first = await service.RepairSchemaAsync(
            new CovenantSchemaRepairRequest(CovenantSchemaRepairAction.InstallAbsentCanonicalFamily), Token);

        Assert.True(first.IsSuccess);

        Assert.Equal(CovenantSchemaRepairDisposition.ManualRecoveryRequired, first.Value.Disposition);

        Assert.Equal((long)first.Value.Phase, await fixture.ScalarAsync("SELECT PhaseCode FROM covenant_schema_repair_intents;"));

        Assert.Equal(CovenantSchemaRepairPhase.Prepared, first.Value.Phase);

        Assert.Equal(1, await fixture.ScalarAsync("SELECT Value FROM repair_data_canary;"));

        Result<CovenantSchemaRepairInspection> after = await inspector.InspectAsync(fixture.Connection, Token);

        Assert.True(after.IsSuccess);

        Assert.Equal(before.Value.CatalogDigest, after.Value.CatalogDigest);

        using MaintenanceLockScope heldLock = fixture.AcquireLock();

        UnknownDataRepairExecutor nextProcess = new(inspector, failAfterWrite: false);

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        CovenantSchemaRepairStartupRecovery recovery = new(gate, nextProcess,
            CovenantSqliteConnectionInitializer.Instance, TimeProvider.System);

        Result<CovenantSchemaRepairStartupRecoveryOutcome> restarted = await recovery
            .RecoverBeforeReadinessAsync(heldLock.Lock, heldLock.Directory, fixture.Connection, Token);

        Assert.True(restarted.IsSuccess);

        Assert.Equal(CovenantSchemaRepairStartupRecoveryOutcome.KeptClosed, restarted.Value);

        Assert.Equal(0, nextProcess.RepairCalls);

        Assert.Equal(1, await fixture.ScalarAsync("SELECT Value FROM repair_data_canary;"));

        Assert.NotEqual((long)CovenantSchemaRepairPhase.Abandoned,
            await fixture.ScalarAsync("SELECT PhaseCode FROM covenant_schema_repair_intents;"));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 0)]
    [InlineData(3, 0)]
    [InlineData(4, 0)]
    [InlineData(4, 2)]
    [InlineData(4, 4)]
    public async Task Unreachable_journal_phase_revision_pairs_refuse_before_owner_adoption_or_effects(int phase, long revision)
    {
        await using RepairFixture fixture = await RepairFixture.CreateAsync();

        _ = await fixture.CommitAsync();

        await using (SqliteCommand corrupt = fixture.Connection.CreateCommand())
        {
            corrupt.CommandText = "DROP TRIGGER covenant_schema_repair_intents_guard_update; UPDATE covenant_schema_repair_intents SET PhaseCode = $phase, Revision = $revision;";

            _ = corrupt.Parameters.AddWithValue("$phase", phase);

            _ = corrupt.Parameters.AddWithValue("$revision", revision);

            _ = await corrupt.ExecuteNonQueryAsync(Token);
        }

        using MaintenanceLockScope heldLock = fixture.AcquireLock();

        StubSchemaRepairExecutor executor = new();

        Result<CovenantSchemaRepairStartupRecoveryPreparation> result = await fixture.Recovery(executor)
            .PrepareBeforeEffectsAsync(heldLock.Lock, heldLock.Directory, fixture.Connection, Token);

        Assert.True(result.IsFailure);

        Assert.Equal(0, executor.InspectCalls);

        Assert.Equal(0, executor.RepairCalls);

        Assert.Equal(phase, await fixture.ScalarAsync("SELECT PhaseCode FROM covenant_schema_repair_intents;"));

        Assert.Equal(revision, await fixture.ScalarAsync("SELECT Revision FROM covenant_schema_repair_intents;"));
    }

    [Fact]
    public async Task A_failed_Prepared_commit_never_dispatches_or_reopens_on_an_unproven_journal_outcome()
    {
        await using RepairFixture fixture = await RepairFixture.CreateAsync();

        await using (SqliteCommand fault = fixture.Connection.CreateCommand())
        {
            fault.CommandText = "CREATE TRIGGER refuse_prepare BEFORE INSERT ON covenant_schema_repair_intents BEGIN SELECT RAISE(ABORT, 'PRIVATE_PREPARE_CANARY'); END;";

            _ = await fault.ExecuteNonQueryAsync(Token);
        }

        StubSchemaRepairExecutor executor = new()
        {
            Inspection = fixture.Inspection(new CovenantDigest(new byte[32]), canonicalValid: true),
        };

        CapturingSink sink = new();

        Serilog.ILogger previous = Serilog.Log.Logger;

        using Serilog.Core.Logger logger = new Serilog.LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();

        Serilog.Log.Logger = logger;

        Result<CovenantSchemaRepairResultDto> result;

        try
        {
            CovenantMaintenanceService service = CreateMaintenanceService(new FixedConnectionSource(fixture.Connection),
                fixture.Gate, executor, new FakeCovenantAvailability());

            result = await service.RepairSchemaAsync(new(CovenantSchemaRepairAction.RepairExistingFamily), Token);
        }
        finally
        {
            Serilog.Log.Logger = previous;
        }

        Assert.True(result.IsFailure);

        Assert.Null(result.Error.ConnectionFailure);

        Assert.Equal(0, executor.RepairCalls);

        Result<CovenantInstallationReadLease> read = await fixture.Gate.AcquireInstallationReadAsync(Token);

        if (read.IsSuccess)
        {
            await read.Value.DisposeAsync();
        }

        Assert.True(read.IsFailure);

        Serilog.Events.LogEvent warning = Assert.Single(sink.Events, static item => item.Level >= Serilog.Events.LogEventLevel.Warning);

        Assert.Null(warning.Exception);

        Assert.Contains("not dispatched", Assert.IsType<string>(Assert.IsType<Serilog.Events.ScalarValue>(warning.Properties["RecoveryGuidance"]).Value), StringComparison.OrdinalIgnoreCase);

        foreach (string value in warning.Properties.Values.Select(static value => value.ToString())
            .Append(warning.RenderMessage(CultureInfo.InvariantCulture)).Append(result.Error.Message))
        {
            Assert.DoesNotContain("PRIVATE_PREPARE_CANARY", value, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_native_inspection_interrupt_is_closed_storage_diagnosis_or_request_cancellation(bool cancel)
    {
        await using RepairFixture fixture = await RepairFixture.CreateAsync();

        using CancellationTokenSource cancellation = new();

        CovenantSchemaRepairExecutor executor = RepairFixture.RealExecutor();

        CovenantMaintenanceService service = CreateMaintenanceService(new FixedConnectionSource(fixture.Connection),
            fixture.Gate, executor, new FakeCovenantAvailability());

        Result<CovenantSchemaRepairResultDto>? result = null;

        SQLitePCL.raw.sqlite3_progress_handler(fixture.Connection.Handle, 1, _ =>
        {
            if (cancel)
            {
                cancellation.Cancel();
            }

            return 1;
        }, null);

        Exception? failure;

        try
        {
            failure = await Record.ExceptionAsync(async () => result = await service.RepairSchemaAsync(
                new(CovenantSchemaRepairAction.RepairExistingFamily), cancellation.Token));
        }
        finally
        {
            SQLitePCL.raw.sqlite3_progress_handler(fixture.Connection.Handle, 0, null, null);
        }

        if (cancel)
        {
            Assert.IsAssignableFrom<OperationCanceledException>(failure);

            Assert.Null(result);
        }
        else
        {
            Assert.Null(failure);

            Assert.NotNull(result);

            Assert.True(result.IsFailure);

            Assert.Equal(ErrorCodes.Covenant.MaintenanceFailed, result.Error.Code);

            Assert.Contains("inspection", result.Error.Message, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(0, await fixture.ScalarAsync("SELECT COUNT(*) FROM covenant_schema_repair_intents;"));
    }

    [Fact]
    public async Task A_real_partial_index_repair_retains_unknown_effects_after_one_autocommit_succeeds()
    {
        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        await using (SqliteCommand corrupt = fixture.Connection.CreateCommand())
        {
            corrupt.CommandText = """
                DROP INDEX ux_covenant_entries_global_key;
                DROP INDEX ux_covenant_entries_campaign_key;
                CREATE TABLE ux_covenant_entries_campaign_key(Value INTEGER);
                """;

            _ = await corrupt.ExecuteNonQueryAsync(Token);
        }

        CovenantSchemaRepairExecutor executor = RepairFixture.RealExecutor();

        Result<CovenantSchemaRepairInspection> inspected = await executor.InspectAsync(fixture.Connection, Token);

        Assert.True(inspected.IsSuccess);

        Assert.Contains("ux_covenant_entries_global_key", inspected.Value.MissingOrdinaryIndexes);

        Assert.Contains("ux_covenant_entries_campaign_key", inspected.Value.MissingOrdinaryIndexes);

        CovenantSchemaRepairExecution repaired = await executor.RepairAsync(fixture.Connection,
            CovenantSchemaRepairAction.RepairOrdinaryIndex, inspected.Value, Token);

        Assert.False(repaired.IsSuccess);

        Assert.Equal(CovenantSchemaRepairEffects.Unknown, repaired.Effects);

        Assert.Equal(CovenantSchemaRepairFailure.Storage, repaired.Failure);

        Assert.Equal(1, repaired.SqliteCode);

        await using SqliteCommand proof = fixture.Connection.CreateCommand();

        proof.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ux_covenant_entries_global_key';";

        Assert.Equal(1L, await proof.ExecuteScalarAsync(Token));
    }

    [Theory]
    [InlineData(CovenantSchemaRepairAction.InstallAbsentCanonicalFamily, true)]
    [InlineData(CovenantSchemaRepairAction.RepairExistingFamily, false)]
    public async Task Prepared_recovery_may_abandon_only_a_matching_catalog_pure_dispatch_refusal(
        CovenantSchemaRepairAction action, bool present)
    {
        await using RepairFixture fixture = await RepairFixture.CreateAsync();

        CovenantSchemaRepairIntent intent = fixture.Intent(CovenantSchemaRepairPhase.Prepared) with
        {
            Action = action,
            CapturedDatasetGeneration = action == CovenantSchemaRepairAction.InstallAbsentCanonicalFamily ? null : Guid.NewGuid(),
        };

        Assert.True((await CovenantSchemaRepairJournal.CommitPreparedAsync(fixture.Connection,
            CovenantSqliteConnectionInitializer.Instance, intent, DateTimeOffset.UnixEpoch, Token)).IsSuccess);

        StubSchemaRepairExecutor executor = new()
        {
            Inspection = fixture.Inspection(intent.InspectedCatalogDigest, canonicalValid: false) with
            {
                CanonicalObjectsPresent = present,
            },
        };

        using MaintenanceLockScope heldLock = fixture.AcquireLock();

        Result<CovenantSchemaRepairStartupRecoveryOutcome> recovered = await fixture.Recovery(executor)
            .RecoverBeforeReadinessAsync(heldLock.Lock, heldLock.Directory, fixture.Connection, Token);

        Assert.True(recovered.IsSuccess);

        Assert.Equal(CovenantSchemaRepairStartupRecoveryOutcome.RecoveredReady, recovered.Value);

        Assert.Equal(0, executor.RepairCalls);

        Assert.Equal((long)CovenantSchemaRepairPhase.Abandoned,
            await fixture.ScalarAsync("SELECT PhaseCode FROM covenant_schema_repair_intents;"));

        Assert.Equal(2, await fixture.ScalarAsync("SELECT Revision FROM covenant_schema_repair_intents;"));
    }

    [Theory]
    [InlineData("read")]
    [InlineData("prepare")]
    [InlineData("advance")]
    public async Task Native_journal_interruption_preserves_request_cancellation_and_never_advances_durable_state(string operation)
    {
        await using RepairFixture fixture = await RepairFixture.CreateAsync();

        CovenantSchemaRepairIntent intent = operation == "prepare"
            ? fixture.Intent(CovenantSchemaRepairPhase.Prepared) : await fixture.CommitAsync();

        using CancellationTokenSource cancellation = new();

        SQLitePCL.raw.sqlite3_progress_handler(fixture.Connection.Handle, 1, _ =>
        {
            cancellation.Cancel();

            return 1;
        }, null);

        Exception? failure;

        try
        {
            failure = await Record.ExceptionAsync(async () =>
            {
                switch (operation)
                {
                    case "read":
                        _ = await CovenantSchemaRepairJournal.TryReadActiveAsync(fixture.Connection, cancellation.Token);

                        break;

                    case "prepare":
                        _ = await CovenantSchemaRepairJournal.CommitPreparedAsync(fixture.Connection,
                            CovenantSqliteConnectionInitializer.Instance, intent, DateTimeOffset.UnixEpoch, cancellation.Token);

                        break;

                    default:
                        _ = await CovenantSchemaRepairJournal.TryAdvanceAsync(fixture.Connection,
                            CovenantSqliteConnectionInitializer.Instance, intent, CovenantSchemaRepairPhase.ReopenPending,
                            null, DateTimeOffset.UnixEpoch, cancellation.Token);

                        break;
                }
            });
        }
        finally
        {
            SQLitePCL.raw.sqlite3_progress_handler(fixture.Connection.Handle, 0, null, null);
        }

        Assert.IsAssignableFrom<OperationCanceledException>(failure);

        Assert.Equal(operation == "prepare" ? 0 : 1,
            await fixture.ScalarAsync("SELECT COUNT(*) FROM covenant_schema_repair_intents;"));

        if (operation != "prepare")
        {
            Assert.Equal((long)CovenantSchemaRepairPhase.Prepared,
                await fixture.ScalarAsync("SELECT PhaseCode FROM covenant_schema_repair_intents;"));

            Assert.Equal(0, await fixture.ScalarAsync("SELECT Revision FROM covenant_schema_repair_intents;"));
        }
    }

    [Fact]
    public async Task A_schema_inspection_fault_does_not_claim_that_the_admitted_connection_failed_to_open()
    {
        await using RepairFixture fixture = await RepairFixture.CreateAsync();

        Assert.Equal(System.Data.ConnectionState.Open, fixture.Connection.State);

        CovenantSchemaRepairResult<CovenantSchemaRepairInspection> inspected;

        SQLitePCL.raw.sqlite3_progress_handler(fixture.Connection.Handle, 1, _ => 1, null);

        try
        {
            inspected = await RepairFixture.RealExecutor().InspectAsync(fixture.Connection, Token);
        }
        finally
        {
            SQLitePCL.raw.sqlite3_progress_handler(fixture.Connection.Handle, 0, null, null);
        }

        Assert.True(inspected.IsFailure);

        Assert.Equal(System.Data.ConnectionState.Open, fixture.Connection.State);

        Assert.Null(inspected.Error.ConnectionFailure);

        Assert.NotNull(inspected.Fault);

        Assert.Equal(GrimoireConnectionFailureKind.Sqlite, inspected.Fault.Value.Kind);

        Assert.Equal(9, inspected.Fault.Value.SqliteCode);
    }

    [Theory]
    [InlineData("before-dispatch")]
    [InlineData("proven-no-effect")]
    [InlineData("committed-suffix")]
    public async Task Repair_guidance_matches_no_dispatch_proof_and_the_recorded_recovery_suffix(string mode)
    {
        await using RepairFixture fixture = await RepairFixture.CreateAsync();

        CovenantSchemaRepairExecutor executor = RepairFixture.RealExecutor();

        using MaintenanceLockScope heldLock = fixture.AcquireLock();

        CovenantSchemaRepairStartupRecovery recovery = fixture.Recovery(executor);

        CovenantSchemaRepairStartupRecoveryPreparation? preparation = null;

        if (mode == "committed-suffix")
        {
            CovenantSchemaRepairIntent intent = await fixture.CommitAsync();

            Assert.True((await fixture.AdvanceAsync(intent, CovenantSchemaRepairPhase.CatalogCommitted)).Value);

            Result<CovenantSchemaRepairStartupRecoveryPreparation> prepared = await recovery.PrepareBeforeEffectsAsync(
                heldLock.Lock, heldLock.Directory, fixture.Connection, Token);

            Assert.True(prepared.IsSuccess);

            preparation = prepared.Value;
        }

        CapturingSink sink = new();

        Serilog.ILogger previous = Serilog.Log.Logger;

        using Serilog.Core.Logger logger = new Serilog.LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();

        Serilog.Log.Logger = logger;

        Result<CovenantSchemaRepairResultDto>? result = null;

        Result<CovenantSchemaRepairStartupRecoveryOutcome>? restarted = null;

        try
        {
            if (mode != "proven-no-effect")
            {
                SQLitePCL.raw.sqlite3_progress_handler(fixture.Connection.Handle, 1, _ => 1, null);
            }

            if (preparation is not null)
            {
                restarted = await recovery.RecoverPreparedAsync(heldLock.Lock, heldLock.Directory,
                    fixture.Connection, preparation, Token);
            }
            else
            {
                CovenantMaintenanceService service = CreateMaintenanceService(new FixedConnectionSource(fixture.Connection),
                    fixture.Gate, executor, new FakeCovenantAvailability());

                result = await service.RepairSchemaAsync(new(CovenantSchemaRepairAction.RepairExistingFamily), Token);
            }
        }
        finally
        {
            SQLitePCL.raw.sqlite3_progress_handler(fixture.Connection.Handle, 0, null, null);

            Serilog.Log.Logger = previous;
        }

        Serilog.Events.LogEvent warning = Assert.Single(sink.Events, static item => item.Level >= Serilog.Events.LogEventLevel.Warning);

        string guidance = Assert.IsType<string>(Assert.IsType<Serilog.Events.ScalarValue>(warning.Properties["RecoveryGuidance"]).Value);

        Assert.DoesNotContain("unknown", guidance, StringComparison.OrdinalIgnoreCase);

        if (mode == "committed-suffix")
        {
            Assert.NotNull(restarted);

            Assert.True(restarted.IsSuccess);

            Assert.Equal(CovenantSchemaRepairStartupRecoveryOutcome.KeptClosed, restarted.Value);

            Assert.Contains("health", guidance, StringComparison.OrdinalIgnoreCase);

            Assert.Contains("finalization", guidance, StringComparison.OrdinalIgnoreCase);

            Assert.Contains("restart", guidance, StringComparison.OrdinalIgnoreCase);

            Assert.Equal((long)CovenantSchemaRepairPhase.CatalogCommitted,
                await fixture.ScalarAsync("SELECT PhaseCode FROM covenant_schema_repair_intents;"));
        }
        else if (mode == "before-dispatch")
        {
            Assert.NotNull(result);

            Assert.True(result.IsFailure);

            Assert.Contains("not dispatched", guidance, StringComparison.OrdinalIgnoreCase);

            Assert.Equal(0, await fixture.ScalarAsync("SELECT COUNT(*) FROM covenant_schema_repair_intents;"));
        }
        else
        {
            Assert.NotNull(result);

            Assert.True(result.IsSuccess);

            Assert.Equal(CovenantSchemaRepairPhase.Abandoned, result.Value.Phase);

            Assert.Contains("No schema repair mutation", guidance, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_repair_connection_acquisition_fault_is_safe_actionable_and_proven_before_dispatch(bool denied)
    {
        await using RepairFixture fixture = await RepairFixture.CreateAsync();

        Exception cause = denied ? new UnauthorizedAccessException("PRIVATE_ACQUISITION_CANARY /private/profile")
            : new IOException("PRIVATE_ACQUISITION_CANARY /private/profile");

        StubSchemaRepairExecutor executor = new();

        CapturingSink sink = new();

        Serilog.ILogger previous = Serilog.Log.Logger;

        using Serilog.Core.Logger logger = new Serilog.LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();

        Serilog.Log.Logger = logger;

        Result<CovenantSchemaRepairResultDto>? result = null;

        Exception? failure;

        try
        {
            CovenantMaintenanceService service = CreateMaintenanceService(new RepairFaultSource(cause),
                fixture.Gate, executor, new FakeCovenantAvailability());

            failure = await Record.ExceptionAsync(async () => result = await service.RepairSchemaAsync(
                new(CovenantSchemaRepairAction.RepairExistingFamily), Token));
        }
        finally
        {
            Serilog.Log.Logger = previous;
        }

        Assert.Null(failure);

        Assert.NotNull(result);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.MaintenanceFailed, result.Error.Code);

        Assert.Equal(0, executor.InspectCalls);

        Assert.Equal(0, executor.RepairCalls);

        Assert.Equal(0, await fixture.ScalarAsync("SELECT COUNT(*) FROM covenant_schema_repair_intents;"));

        Result<CovenantInstallationReadLease> read = await fixture.Gate.AcquireInstallationReadAsync(Token);

        Assert.True(read.IsSuccess);

        await read.Value.DisposeAsync();

        Serilog.Events.LogEvent warning = Assert.Single(sink.Events, static item => item.Level >= Serilog.Events.LogEventLevel.Warning);

        Assert.Null(warning.Exception);

        Assert.Contains("not dispatched", warning.RenderMessage(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);

        foreach (string value in warning.Properties.Values.Select(static value => value.ToString())
            .Append(warning.RenderMessage(CultureInfo.InvariantCulture)).Append(result.Error.Message))
        {
            Assert.DoesNotContain("PRIVATE_", value, StringComparison.Ordinal);

            Assert.DoesNotContain("/private/profile", value, StringComparison.Ordinal);
        }
    }

    private sealed class RepairFaultSource(Exception failure) : ICovenantConnectionSource
    {
        public ValueTask<SqliteConnection> GetOpenConnectionAsync(CancellationToken cancellationToken) => throw failure;

        public ValueTask<SqliteConnection> GetOpenCoreConnectionAsync(CancellationToken cancellationToken) => throw failure;
    }

    private sealed class UnknownDataRepairExecutor(CovenantSchemaRepairExecutor inspector, bool failAfterWrite)
        : ICovenantSchemaRepairExecutor
    {
        internal int RepairCalls { get; private set; }

        public Task<CovenantSchemaRepairResult<CovenantSchemaRepairInspection>> InspectAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
            inspector.InspectAsync(connection, cancellationToken);

        public async Task<CovenantSchemaRepairExecution> RepairAsync(SqliteConnection connection, CovenantSchemaRepairAction action,
            CovenantSchemaRepairInspection inspected, CancellationToken cancellationToken)
        {
            RepairCalls++;

            if (!failAfterWrite)
            {
                return CovenantSchemaRepairExecution.NoMutation();
            }

            await using SqliteCommand write = connection.CreateCommand();

            write.CommandText = "UPDATE repair_data_canary SET Value = 1;";

            Assert.Equal(1, await write.ExecuteNonQueryAsync(cancellationToken));

            return CovenantSchemaRepairExecution.Unknown(CovenantSchemaRepairFailure.Storage);
        }
    }
}
