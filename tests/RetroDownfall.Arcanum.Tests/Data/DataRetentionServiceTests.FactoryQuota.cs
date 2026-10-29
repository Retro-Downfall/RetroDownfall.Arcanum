using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FactoryQuota_Reset_returns_live_and_historically_orphaned_capacity_and_replays(bool continuation)
    {
        RequireSqlCipher();

        await SeedQuotaOwnerAsync(Canonical(Guid.NewGuid()), claims: 2, consumed: 1);

        Guid historical = Guid.NewGuid();

        await SeedQuotaOwnerAsync(Canonical(historical), claims: 1, consumed: 1);

        // Reproduce the old production cascade, which removed its owner without returning totals.
        await ExecuteSessionRetentionAsync("DELETE FROM Sessions WHERE Id = @id;", ("@id", Canonical(historical)));

        Assert.Equal((3L, 1L, 2L), await ReadInstallationQuotaAsync());

        Assert.True(await RunFactoryQuotaAsync(continuation, replay: true));

        Assert.Equal(0, await CountAllAsync("Sessions"));

        Assert.Equal(0, await CountAllAsync("session_turn_quota_state"));

        Assert.Equal((0L, 0L, 0L), await ReadInstallationQuotaAsync());

        await AssertQuotaAuthorizationClosedAsync();
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FactoryQuota_Zero_totals_do_not_issue_a_forbidden_noop(bool continuation)
    {
        RequireSqlCipher();

        await SeedQuotaOwnerAsync(Canonical(Guid.NewGuid()), claims: 0, consumed: 0);

        await ExecuteAsync("""
            CREATE TRIGGER refuse_factory_quota_noop BEFORE UPDATE ON installation_turn_quota_state
            BEGIN SELECT RAISE(ABORT, 'zero factory counters must not be updated'); END;
            """);

        Assert.True(await RunFactoryQuotaAsync(continuation, replay: true));

        Assert.Equal((0L, 0L, 0L), await ReadInstallationQuotaAsync());
    }

    [SkippableTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FactoryQuota_Failed_or_ignored_parent_delete_preserves_all_counters(bool continuation, bool ignore)
    {
        RequireSqlCipher();

        await SeedQuotaOwnerAsync(Canonical(Guid.NewGuid()), claims: 2, consumed: 1);

        await ExecuteAsync(ignore
            ? "CREATE TRIGGER refuse_factory_session BEFORE DELETE ON Sessions BEGIN SELECT RAISE(IGNORE); END;"
            : "CREATE TRIGGER refuse_factory_session BEFORE DELETE ON Sessions BEGIN SELECT RAISE(ABORT, 'injected parent failure'); END;");

        Assert.False(await RunFactoryQuotaAsync(continuation));

        Assert.Equal(1, await CountAllAsync("Sessions"));

        Assert.Equal((2L, 1L, 1L), await ReadInstallationQuotaAsync());

        await AssertQuotaAuthorizationClosedAsync();
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FactoryQuota_Missing_singleton_or_ignored_transition_rolls_back_owner_deletion(bool missingSingleton)
    {
        RequireSqlCipher();

        await SeedQuotaOwnerAsync(Canonical(Guid.NewGuid()), claims: 2, consumed: 1);

        if (missingSingleton)
        {
            await ExecuteAsync("DELETE FROM installation_turn_quota_state;");
        }
        else
        {
            await ExecuteAsync("""
                CREATE TRIGGER ignore_factory_quota BEFORE UPDATE ON installation_turn_quota_state
                BEGIN SELECT RAISE(IGNORE); END;
                """);
        }

        Assert.False(await RunFactoryQuotaAsync(continuation: true));

        Assert.Equal(1, await CountAllAsync("Sessions"));

        Assert.Equal(1, await CountAllAsync("session_turn_quota_state"));

        if (!missingSingleton) Assert.Equal((2L, 1L, 1L), await ReadInstallationQuotaAsync());
    }

    [SkippableFact]
    public async Task FactoryQuota_Reinserted_parent_preserves_target_diagnostic_and_rolls_back_capacity()
    {
        RequireSqlCipher();

        await SeedQuotaOwnerAsync(Canonical(Guid.NewGuid()), claims: 2, consumed: 1);

        await ExecuteAsync("""
            CREATE TRIGGER retain_quota_session AFTER DELETE ON Sessions
            BEGIN
                INSERT INTO Sessions (Id, Status, CreatedAt, UpdatedAt)
                VALUES (OLD.Id, OLD.Status, OLD.CreatedAt, OLD.UpdatedAt);
            END;
            """);

        TestCapturingLogger<DataRetentionService> logger = new();

        var recovered = await ReconcileFactoryResetV0Async(CreateService(logger: logger), "factory-quota-reinsert");

        Assert.Equal(1, recovered.Summary.RequiresAttention);

        Assert.Equal(1, await CountAllAsync("Sessions"));

        Assert.Equal(1, await CountAllAsync("session_turn_quota_state"));

        Assert.Equal((2L, 1L, 1L), await ReadInstallationQuotaAsync());

        Assert.Contains(logger.Entries, entry => entry.Message.Contains("FactoryCapacityRefused", StringComparison.Ordinal)
            && entry.Message.Contains(recovered.Operation.Id.ToString("D"), StringComparison.Ordinal)
            && entry.Properties!.Any(property => property.Key == "Target" && Equals(property.Value, "Sessions")));

        await AssertQuotaAuthorizationClosedAsync();
    }

    [SkippableFact]
    public async Task FactoryQuota_Surviving_quota_owner_refuses_reset_and_restores_session()
    {
        RequireSqlCipher();

        await SeedQuotaOwnerAsync(Canonical(Guid.NewGuid()), claims: 2, consumed: 1);

        // Corruption control: a disabled FK cascade must never turn a surviving owner into capacity.
        await ExecuteAsync("PRAGMA foreign_keys = OFF;");

        Assert.False(await RunFactoryQuotaAsync(continuation: true));

        Assert.Equal(1, await CountAllAsync("Sessions"));

        Assert.Equal(1, await CountAllAsync("session_turn_quota_state"));

        Assert.Equal((2L, 1L, 1L), await ReadInstallationQuotaAsync());

        await AssertQuotaAuthorizationClosedAsync();
    }

    [SkippableFact]
    public async Task FactoryQuota_Cancellation_after_the_observed_transition_rolls_back_every_owner()
    {
        RequireSqlCipher();

        await SeedQuotaOwnerAsync(Canonical(Guid.NewGuid()), claims: 2, consumed: 1);

        using CancellationTokenSource cancellation = new();

        bool resetObserved = false;

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        connection.CreateFunction("cancel_factory_quota", () =>
        {
            resetObserved = true;

            cancellation.Cancel();

            return 1;
        });

        await ExecuteAsync("""
            CREATE TRIGGER cancel_factory_quota AFTER UPDATE ON installation_turn_quota_state
            WHEN NEW.ClaimCount < OLD.ClaimCount
            BEGIN SELECT cancel_factory_quota(); END;
            """);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RunFactoryQuotaAsync(continuation: true, cancellationToken: cancellation.Token));

        Assert.True(resetObserved);

        Assert.Equal(1, await CountAllAsync("Sessions"));

        Assert.Equal(1, await CountAllAsync("session_turn_quota_state"));

        Assert.Equal((2L, 1L, 1L), await ReadInstallationQuotaAsync());

        await AssertQuotaAuthorizationClosedAsync();
    }

    private async Task<bool> RunFactoryQuotaAsync(bool continuation, bool replay = false, CancellationToken cancellationToken = default)
    {
        DataRetentionService service = CreateService();

        if (!continuation)
        {
            var first = await ReconcileFactoryResetV0Async(service, "factory-quota-recovery");

            if (first.Summary.Completed != 1) return false;

            if (!replay) return true;

            var second = await ReconcileFactoryResetV0Async(service, "factory-quota-replay");

            return second.Summary.Completed == 1;
        }

        // This is the actual private ordinary continuation used by current V2 activation/recovery.
        // The surrounding V2 journal/closure remains covered by its owning coordinator tests.
        LongRunningOperationStore store = new(_db!, TestOrdinaryConnectionFactory.For(_db!));

        DateTimeOffset now = DateTimeOffset.UtcNow;

        LongRunningOperation operation = await store.CreateAsync(new(
            LongRunningOperationKinds.DataRetentionFactoryReset,
            LongRunningOperationRecoveryPolicy.RestartIdempotently,
            "Factory quota continuation fixture.", now), cancellationToken);

        LongRunningOperationLeaseResult lease = await store.TryAcquireLeaseAsync(
            operation.Id, "factory-quota-owner", now, now.AddMinutes(5), cancellationToken);

        Assert.True(lease.Acquired);

        MethodInfo method = typeof(DataRetentionService).GetMethod("ContinueFactoryResetAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        try
        {
            Result<DataRetentionApplyResult> result = await Assert.IsAssignableFrom<Task<Result<DataRetentionApplyResult>>>(
                method.Invoke(service, [operation.Id, lease.Operation.LeaseOwner!, cancellationToken]));

            if (result.IsFailure || !replay) return result.IsSuccess;

            result = await Assert.IsAssignableFrom<Task<Result<DataRetentionApplyResult>>>(
                method.Invoke(service, [operation.Id, lease.Operation.LeaseOwner!, cancellationToken]));

            return result.IsSuccess;
        }
        finally
        {
            // The real continuation closes its service handle before the next coordinator phase.
            // Reopen only for the fixture's independent postcondition reads.
            await _db!.Database.OpenConnectionAsync(CancellationToken.None);
        }
    }
}
