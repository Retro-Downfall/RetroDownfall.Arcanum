using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

public sealed partial class GrimoireSchemaTransitionCoordinatorTests
{
    [Fact]
    public async Task An_unhealthy_DDL_convergence_records_the_pending_tier_failure_without_progress()
    {
        TestCapturingLogger<GrimoireSchemaTransitionCoordinator> log = new();

        await using CoordinatorHarness harness = await CoordinatorHarness.StartCovenantDdlAsync(log);

        GrimoireSchemaTransitionJournalRow before = (await harness.ReadJournalAsync(GrimoireSchemaTransactionTier.CovenantCanonical))!;

        await harness.ExecuteAsync("ALTER TABLE covenant_entries ADD COLUMN private_catalog_drift TEXT;");

        var outcome = await harness.Coordinator.RunOnceAsync(CancellationToken.None);

        Assert.True(outcome.IsSuccess);

        Assert.False(outcome.Value.Advanced);

        Assert.True(outcome.Value.Deferred);

        Assert.Equal(0, outcome.Value.BatchesRun);

        Assert.Equal(0, outcome.Value.RowsProcessed);

        Assert.Equal(before, await harness.ReadJournalAsync(GrimoireSchemaTransactionTier.CovenantCanonical));

        Assert.Equal(4, await harness.RecordedVersionAsync(GrimoireSchemaTransactionTier.CovenantCanonical));

        Assert.Equal(ErrorCodes.Grimoire.Schema.FinalizationFailed,
            await harness.ReadErrorCodeAsync(GrimoireSchemaTransactionTier.CovenantCanonical));

        var warning = Assert.Single(log.Entries, entry => entry.Level == LogLevel.Warning);

        Assert.Contains("CovenantCanonical schema sweep DDL at version 5 toward 6", warning.Message, StringComparison.Ordinal);

        Assert.Contains(ErrorCodes.Grimoire.Schema.FinalizationFailed, warning.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("private_catalog_drift", warning.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("covenant_entries", warning.Message, StringComparison.Ordinal);

        Assert.Null(warning.Exception);

        await harness.ExecuteAsync("ALTER TABLE covenant_entries DROP COLUMN private_catalog_drift;");

        var recovered = await harness.Coordinator.RunOnceAsync(CancellationToken.None);

        Assert.True(recovered.Value.Advanced);

        Assert.False(recovered.Value.Deferred);

        Assert.Null(await harness.ReadJournalAsync(GrimoireSchemaTransactionTier.CovenantCanonical));

        Assert.Equal(6, await harness.RecordedVersionAsync(GrimoireSchemaTransactionTier.CovenantCanonical));
    }

    [Fact]
    public async Task A_healthy_DDL_convergence_finishes_without_a_failure_diagnostic()
    {
        TestCapturingLogger<GrimoireSchemaTransitionCoordinator> log = new();

        await using CoordinatorHarness harness = await CoordinatorHarness.StartCovenantDdlAsync(log);

        var outcome = await harness.Coordinator.RunOnceAsync(CancellationToken.None);

        Assert.True(outcome.Value.Advanced);

        Assert.False(outcome.Value.Deferred);

        Assert.Equal(0, outcome.Value.RunsPending);

        Assert.Null(await harness.ReadJournalAsync(GrimoireSchemaTransactionTier.CovenantCanonical));

        Assert.Equal(6, await harness.RecordedVersionAsync(GrimoireSchemaTransactionTier.CovenantCanonical));

        Assert.DoesNotContain(log.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task A_refusal_in_another_tier_does_not_blame_the_pending_DDL_run()
    {
        TestCapturingLogger<GrimoireSchemaTransitionCoordinator> log = new();

        await using CoordinatorHarness harness = await CoordinatorHarness.StartCovenantDdlAsync(log);

        GrimoireSchemaTransitionJournalRow before = (await harness.ReadJournalAsync(GrimoireSchemaTransactionTier.CovenantCanonical))!;

        await harness.ExecuteAsync("UPDATE grimoire_feature_schemas SET SourceDefinitionFingerprint = 'FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF' WHERE TransactionTierCode = 0;");

        GrimoireSchemaRefusedException refusal = await Assert.ThrowsAsync<GrimoireSchemaRefusedException>(
            () => harness.Coordinator.RunOnceAsync(CancellationToken.None));

        Assert.Equal(GrimoireSchemaTransactionTier.Core, refusal.Tier);

        Assert.Equal(before, await harness.ReadJournalAsync(GrimoireSchemaTransactionTier.CovenantCanonical));

        Assert.Null(await harness.ReadErrorCodeAsync(GrimoireSchemaTransactionTier.CovenantCanonical));

        Assert.DoesNotContain(log.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task A_throwing_DDL_convergence_records_its_safe_failure_without_progress()
    {
        TestCapturingLogger<GrimoireSchemaTransitionCoordinator> log = new();

        await using CoordinatorHarness harness = await CoordinatorHarness.StartAsync(3, logger: log, pendingDdl: true);

        GrimoireSchemaTransitionJournalRow before = (await harness.ReadJournalAsync())!;

        Assert.Equal(2, before.CompletedThroughVersion);

        Assert.Null(before.BackfillName);

        await harness.ExecuteAsync("CREATE TABLE evolution_final (Id INTEGER PRIMARY KEY);");

        var outcome = await harness.Coordinator.RunOnceAsync(CancellationToken.None);

        Assert.False(outcome.Value.Advanced);

        Assert.True(outcome.Value.Deferred);

        Assert.Equal(0, outcome.Value.RowsProcessed);

        Assert.Equal(0, outcome.Value.BatchesRun);

        Assert.Equal(before, await harness.ReadJournalAsync());

        Assert.Equal(1, await harness.RecordedVersionAsync());

        Assert.Equal(ErrorCodes.Grimoire.Schema.SqliteFailure, await harness.ReadErrorCodeAsync(GrimoireSchemaTransactionTier.Core));

        var warning = Assert.Single(log.Entries, entry => entry.Level == LogLevel.Warning);

        Assert.Contains("Core schema sweep DDL at version 2 toward 3", warning.Message, StringComparison.Ordinal);

        Assert.Contains(ErrorCodes.Grimoire.Schema.SqliteFailure, warning.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("evolution_final", warning.Message, StringComparison.Ordinal);

        Assert.Null(warning.Exception);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failure_at_a_stale_run_never_changes_or_warns_for_its_replacement(bool replaceIdentity)
    {
        TestCapturingLogger<GrimoireSchemaTransitionCoordinator> log = new();

        CallbackTimeProvider clock = new();

        await using CoordinatorHarness harness = await CoordinatorHarness.StartAsync(3, logger: log, timeProvider: clock);

        await harness.ExecuteAsync("DROP TABLE evolution_target;");

        int clockReads = 0;

        clock.OnRead = () =>
        {
            if (++clockReads == 2)
            {
                harness.ExecuteAsync(replaceIdentity
                    ? "UPDATE grimoire_schema_transitions SET TargetVersion = 3, TargetSourceDefinitionFingerprint = 'FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF';"
                    : "UPDATE grimoire_schema_transitions SET Revision = Revision + 1;").GetAwaiter().GetResult();
            }
        };

        var outcome = await harness.Coordinator.RunOnceAsync(CancellationToken.None);

        Assert.False(outcome.Value.Advanced);

        Assert.Null(await harness.ReadErrorCodeAsync(GrimoireSchemaTransactionTier.Core));

        Assert.DoesNotContain(log.Entries, entry => entry.Level == LogLevel.Warning);

        GrimoireSchemaTransitionJournalRow replacement = (await harness.ReadJournalAsync())!;

        Assert.Equal(replaceIdentity ? 3 : 2, replacement.TargetVersion);

        Assert.Equal(replaceIdentity ? 0 : 1, replacement.Revision);
    }

    [Fact]
    public async Task Cancellation_before_failure_recording_preserves_the_run_without_a_warning()
    {
        TestCapturingLogger<GrimoireSchemaTransitionCoordinator> log = new();

        CallbackTimeProvider clock = new();

        await using CoordinatorHarness harness = await CoordinatorHarness.StartAsync(3, logger: log, timeProvider: clock);

        GrimoireSchemaTransitionJournalRow before = (await harness.ReadJournalAsync())!;

        await harness.ExecuteAsync("DROP TABLE evolution_target;");

        using CancellationTokenSource cancellation = new();

        int clockReads = 0;

        clock.OnRead = () =>
        {
            if (++clockReads == 2)
            {
                cancellation.Cancel();
            }
        };

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.Coordinator.RunOnceAsync(cancellation.Token));

        Assert.Equal(before, await harness.ReadJournalAsync());

        Assert.Null(await harness.ReadErrorCodeAsync(GrimoireSchemaTransactionTier.Core));

        Assert.DoesNotContain(log.Entries, entry => entry.Level == LogLevel.Warning);
    }

    private sealed class CallbackTimeProvider : TimeProvider
    {
        internal Action? OnRead { get; set; }

        public override DateTimeOffset GetUtcNow()
        {
            OnRead?.Invoke();

            return new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);
        }
    }

    [Fact]
    public async Task A_failure_after_committed_batches_reports_only_their_progress()
    {
        TestCapturingLogger<GrimoireSchemaTransitionCoordinator> log = new();

        await using CoordinatorHarness harness = await CoordinatorHarness.StartAsync(40, 2, log, failOnBatch: 3);

        var result = await harness.Coordinator.RunOnceAsync(CancellationToken.None);

        Assert.True(result.Value.Advanced);

        Assert.Equal(2, result.Value.BatchesRun);

        Assert.Equal(4, result.Value.RowsProcessed);

        Assert.Equal(4, await harness.TargetRowCountAsync());

        var warning = Assert.Single(log.Entries, entry => entry.Level == LogLevel.Warning);

        Assert.Contains("committed rows 4", warning.Message, StringComparison.Ordinal);

        Assert.Null(warning.Exception);
    }

    [Fact]
    public async Task An_unresumable_non_core_run_does_not_republish_availability_or_claim_progress()
    {
        await using CoordinatorHarness harness = await CoordinatorHarness.StartAsync(3);

        _ = await harness.Coordinator.RunOnceAsync(CancellationToken.None);

        await harness.ExecuteAsync("""
            INSERT INTO grimoire_schema_transitions (
                FamilyCode, TransactionTierCode, FromVersion, TargetVersion, CompletedThroughVersion,
                TargetSourceDefinitionFingerprint, BackfillName, BackfillCursor, BackfillRowsProcessed,
                Revision, LastDurableErrorCode, StartedAtUtc, UpdatedAtUtc)
            SELECT FamilyCode, TransactionTierCode, SchemaVersion, 99, SchemaVersion,
                SourceDefinitionFingerprint, NULL, NULL, 0, 0, NULL,
                '2026-10-26T00:00:00.0000000Z', '2026-10-26T00:00:00.0000000Z'
            FROM grimoire_feature_schemas WHERE TransactionTierCode = 1;
            """);

        long generation = harness.Availability.Current.Generation;

        for (int pass = 0; pass < 2; pass++)
        {
            var result = await harness.Coordinator.RunOnceAsync(CancellationToken.None);

            Assert.True(result.IsSuccess);

            Assert.False(result.Value.Advanced);

            Assert.Equal(0, result.Value.BatchesRun);

            Assert.Equal(generation, harness.Availability.Current.Generation);
        }
    }

    [Theory]
    [InlineData("advanced", 500)]
    [InlineData("idle", 300000)]
    [InlineData("failed", 15000)]
    [InlineData("stalled", 15000)]
    public async Task Hosted_cadence_follows_durable_work_state(string state, int delayMilliseconds)
    {
        await using CoordinatorHarness harness = await CoordinatorHarness.StartAsync(40, 1);

        if (state == "idle")
        {
            while (await harness.ReadJournalAsync() is not null)
            {
                _ = await harness.Coordinator.RunOnceAsync(CancellationToken.None);
            }
        }
        else if (state == "failed")
        {
            await harness.ExecuteAsync("DROP TABLE evolution_target;");
        }
        else if (state == "stalled")
        {
            await harness.ExecuteAsync("UPDATE grimoire_schema_transitions SET TargetVersion = 99, BackfillName = NULL;");
        }

        ServiceCollection services = new();

        services.AddScoped(_ => harness.Coordinator);

        await using ServiceProvider provider = services.BuildServiceProvider();

        ManualWallClockTimeProvider clock = new();

        using GrimoireSchemaTransitionHostedService service = new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new GrimoireConnectionAdmissionGate(clock),
            clock,
            NullLogger<GrimoireSchemaTransitionHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);

        try
        {
            await clock.WhenArmedTimersAsync(count => count == 1, TimeSpan.FromSeconds(10));

            Assert.Equal(TimeSpan.FromMilliseconds(delayMilliseconds), clock.NextDueIn);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Admission_refusal_retries_promptly_without_creating_a_scope()
    {
        ManualWallClockTimeProvider clock = new();

        GrimoireConnectionAdmissionGate gate = new(clock);

        var closed = await PeriodicHostMaintenance.CloseAsync(gate);

        await using ServiceProvider provider = new ServiceCollection().BuildServiceProvider();

        CountingServiceScopeFactory scopes = new(provider.GetRequiredService<IServiceScopeFactory>());

        using GrimoireSchemaTransitionHostedService service = new(
            scopes, gate, clock, NullLogger<GrimoireSchemaTransitionHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);

        try
        {
            await clock.WhenArmedTimersAsync(count => count == 1, TimeSpan.FromSeconds(10));

            Assert.Equal(TimeSpan.FromSeconds(15), clock.NextDueIn);

            Assert.Equal(0, scopes.Created);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);

            await PeriodicHostMaintenance.ReopenAsync(closed);
        }
    }

    [Fact]
    public async Task A_pending_sweep_logs_its_identity_versions_and_durable_row_count()
    {
        TestCapturingLogger<GrimoireSchemaTransitionCoordinator> log = new();

        await using CoordinatorHarness harness = await CoordinatorHarness.StartAsync(40, 1, log);

        _ = await harness.Coordinator.RunOnceAsync(CancellationToken.None);

        string message = Assert.Single(log.Entries, entry => entry.Level == LogLevel.Information).Message;

        Assert.Contains("Core", message, StringComparison.Ordinal);

        Assert.Contains("fill-target", message, StringComparison.Ordinal);

        Assert.Contains("1", message, StringComparison.Ordinal);

        Assert.Contains("2", message, StringComparison.Ordinal);

        Assert.Contains("16", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unresumable_run_does_not_claim_progress_or_run_its_pending_DDL()
    {
        await using CoordinatorHarness harness = await CoordinatorHarness.StartAsync(3);

        await harness.ExecuteAsync("UPDATE grimoire_schema_transitions SET TargetVersion = 99, BackfillName = NULL;");

        var outcome = await harness.Coordinator.RunOnceAsync(CancellationToken.None);

        Assert.True(outcome.IsSuccess);

        Assert.False(outcome.Value.Advanced);

        Assert.Equal(0, await harness.TargetRowCountAsync());

        Assert.Equal(1, await harness.RecordedVersionAsync());
    }

    [Fact]
    public async Task A_sweep_with_a_foreign_target_fingerprint_never_writes_source_rows()
    {
        await using CoordinatorHarness harness = await CoordinatorHarness.StartAsync(3);

        await harness.ExecuteAsync("UPDATE grimoire_schema_transitions SET TargetSourceDefinitionFingerprint = 'FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF';");

        var outcome = await harness.Coordinator.RunOnceAsync(CancellationToken.None);

        Assert.False(outcome.Value.Advanced);

        Assert.Equal(0, await harness.TargetRowCountAsync());

        Assert.NotNull(await harness.ReadJournalAsync());
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(5, 4)]
    public async Task Finalization_rollback_never_counts_rolled_back_rows_or_batches(int sourceRows, long committedRows)
    {
        await using CoordinatorHarness harness = await CoordinatorHarness.StartAsync(sourceRows);

        await harness.ExecuteAsync("ALTER TABLE evolution_target ADD COLUMN unexpected TEXT;");

        var outcome = await harness.Coordinator.RunOnceAsync(CancellationToken.None);

        Assert.Equal(committedRows, outcome.Value.RowsProcessed);

        Assert.Equal(committedRows / 2, outcome.Value.BatchesRun);

        Assert.Equal(committedRows > 0, outcome.Value.Advanced);

        Assert.Equal(committedRows, await harness.TargetRowCountAsync());

        Assert.Equal(committedRows, (await harness.ReadJournalAsync())!.BackfillRowsProcessed);
    }

    [Fact]
    public async Task A_failed_sweep_names_safe_identity_and_failure_category_without_exception_content()
    {
        TestCapturingLogger<GrimoireSchemaTransitionCoordinator> log = new();

        await using CoordinatorHarness harness = await CoordinatorHarness.StartAsync(3, 2, log);

        await harness.ExecuteAsync("DROP TABLE evolution_target;");

        _ = await harness.Coordinator.RunOnceAsync(CancellationToken.None);

        var entry = Assert.Single(log.Entries, entry => entry.Level == LogLevel.Warning);

        Assert.Contains("fill-target", entry.Message, StringComparison.Ordinal);

        Assert.Contains("Grimoire.Schema.SqliteFailure", entry.Message, StringComparison.Ordinal);

        Assert.Null(entry.Exception);

        Assert.DoesNotContain("evolution_target", entry.Message, StringComparison.Ordinal);
    }
}
