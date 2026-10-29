using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionQuota_Deletion_returns_only_the_deleted_owners_capacity(bool prune)
    {
        RequireSqlCipher();

        Guid target = Guid.NewGuid();

        Guid survivor = Guid.NewGuid();

        string targetKey = Canonical(target);

        await SeedQuotaOwnerAsync(targetKey, claims: 2, consumed: 1);

        await SeedQuotaOwnerAsync(Canonical(survivor), claims: 1, consumed: 1);

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.ArchivedSessions = EnabledRule();

        settings.Retention.ProtectedSessionIds = [survivor];

        IDataRetentionService service = CreateService(settings);

        DataRetentionRequest request = new(prune ? DataRetentionOperation.Prune : DataRetentionOperation.DeleteSession,
            TargetId: prune ? null : target);

        DataRetentionPlan plan = await service.PlanAsync(request, CancellationToken.None);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(new(request, plan.PlanId), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(0, await CountAsync("Sessions", "Id", targetKey));

        Assert.Equal(0, await CountAsync("session_turn_quota_state", "SessionId", targetKey));

        Assert.Equal((1L, 0L, 1L), await ReadInstallationQuotaAsync());

        // A new plan over the now-absent owner cannot give its capacity back a second time.
        plan = await service.PlanAsync(request, CancellationToken.None);

        result = await service.ApplyAsync(new(request, plan.PlanId), CancellationToken.None);

        if (prune)
        {
            Assert.True(result.IsSuccess, result.Error.Message);
        }
        else
        {
            Assert.True(result.IsFailure);

            Assert.Equal(ErrorCodes.Data.NotFound, result.Error.Code);
        }

        Assert.Equal((1L, 0L, 1L), await ReadInstallationQuotaAsync());

        await AssertQuotaAuthorizationClosedAsync();
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionQuota_Failure_after_release_rolls_back_owner_and_counters(bool cancel)
    {
        RequireSqlCipher();

        Guid target = Guid.NewGuid();

        await SeedQuotaOwnerAsync(Canonical(target), claims: 2, consumed: 1);

        using CancellationTokenSource cancellation = new();

        bool releaseObserved = false;

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        connection.CreateFunction("observe_quota_release", () =>
        {
            releaseObserved = true;

            if (cancel) cancellation.Cancel();

            return 1;
        });

        await ExecuteAsync("""
            CREATE TRIGGER observe_quota_release AFTER UPDATE ON installation_turn_quota_state
            WHEN NEW.ClaimCount < OLD.ClaimCount
            BEGIN SELECT observe_quota_release(); END;
            """);

        if (!cancel)
        {
            await ExecuteAsync("""
                CREATE TRIGGER refuse_quota_owner_delete BEFORE DELETE ON Sessions
                BEGIN SELECT RAISE(ABORT, 'injected Session delete failure'); END;
                """);
        }

        IDataRetentionService service = CreateService();

        DataRetentionRequest request = new(DataRetentionOperation.DeleteSession, TargetId: target);

        DataRetentionPlan plan = await service.PlanAsync(request, CancellationToken.None);

        if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.ApplyAsync(new(request, plan.PlanId), cancellation.Token));
        }
        else
        {
            Result<DataRetentionApplyResult> result = await service.ApplyAsync(new(request, plan.PlanId), CancellationToken.None);

            Assert.True(result.IsFailure);
        }

        Assert.True(releaseObserved);

        Assert.Equal(1, await CountAsync("Sessions", "Id", Canonical(target)));

        Assert.Equal((2L, 1L, 1L), await ReadInstallationQuotaAsync());

        await AssertQuotaAuthorizationClosedAsync();
    }

    [SkippableFact]
    public async Task SessionQuota_Zero_owner_deletes_without_a_forbidden_counter_noop()
    {
        RequireSqlCipher();

        Guid target = Guid.NewGuid();

        await SeedQuotaOwnerAsync(Canonical(target), claims: 0, consumed: 0);

        await ExecuteAsync("""
            CREATE TRIGGER refuse_counter_noop BEFORE UPDATE ON installation_turn_quota_state
            BEGIN SELECT RAISE(ABORT, 'zero owner must not update counters'); END;
            """);

        IDataRetentionService service = CreateService();

        DataRetentionRequest request = new(DataRetentionOperation.DeleteSession, TargetId: target);

        DataRetentionPlan plan = await service.PlanAsync(request, CancellationToken.None);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(new(request, plan.PlanId), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(0, await CountAsync("Sessions", "Id", Canonical(target)));

        Assert.Equal((0L, 0L, 0L), await ReadInstallationQuotaAsync());
    }

    [SkippableFact]
    public async Task SessionQuota_Inconsistent_installation_totals_refuse_without_clamping_or_deleting()
    {
        RequireSqlCipher();

        Guid target = Guid.NewGuid();

        await SeedQuotaOwnerAsync(Canonical(target), claims: 2, consumed: 1);

        using (CovenantSqliteAuthorizationScope authorization = CovenantSqliteConnectionInitializer.Instance.Authorize(
            (SqliteConnection)_db!.Database.GetDbConnection(), CovenantSqliteAuthorizationKind.TurnCapacityMutation))
        {
            await ExecuteAsync("UPDATE installation_turn_quota_state SET ClaimCount = 0, ReservedFinalizationCount = 0, ConsumedFinalizationCount = 0;");
        }

        IDataRetentionService service = CreateService();

        DataRetentionRequest request = new(DataRetentionOperation.DeleteSession, TargetId: target);

        DataRetentionPlan plan = await service.PlanAsync(request, CancellationToken.None);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(new(request, plan.PlanId), CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(1, await CountAsync("Sessions", "Id", Canonical(target)));

        Assert.Equal((0L, 0L, 0L), await ReadInstallationQuotaAsync());

        await AssertQuotaAuthorizationClosedAsync();
    }

    [SkippableFact]
    public async Task SessionQuota_Ignored_parent_delete_rolls_back_the_release()
    {
        RequireSqlCipher();

        Guid target = Guid.NewGuid();

        await SeedQuotaOwnerAsync(Canonical(target), claims: 2, consumed: 1);

        await ExecuteAsync("""
            CREATE TRIGGER ignore_quota_owner_delete BEFORE DELETE ON Sessions
            BEGIN SELECT RAISE(IGNORE); END;
            """);

        IDataRetentionService service = CreateService();

        DataRetentionRequest request = new(DataRetentionOperation.DeleteSession, TargetId: target);

        DataRetentionPlan plan = await service.PlanAsync(request, CancellationToken.None);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(new(request, plan.PlanId), CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(1, await CountAsync("Sessions", "Id", Canonical(target)));

        Assert.Equal((2L, 1L, 1L), await ReadInstallationQuotaAsync());
    }

    [SkippableFact]
    public async Task SessionQuota_Noncanonical_owner_identity_is_still_rejected()
    {
        RequireSqlCipher();

        SqliteException failure = await Assert.ThrowsAsync<SqliteException>(() =>
            SeedQuotaOwnerAsync(Guid.NewGuid().ToString("N"), claims: 0, consumed: 0));

        Assert.Contains("uppercase dashed 36-character identity", failure.Message, StringComparison.Ordinal);

        Assert.Equal((0L, 0L, 0L), await ReadInstallationQuotaAsync());
    }

    [SkippableFact]
    public async Task SessionQuota_Transaction_adapter_rejects_another_connection_and_a_completed_transaction()
    {
        RequireSqlCipher();

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        await using SqliteConnection other = new("Data Source=:memory:");

        await other.OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        _ = Assert.Throws<ArgumentException>(() => new CovenantMutationTransaction(other, transaction));

        await transaction.RollbackAsync();

        _ = Assert.Throws<ArgumentException>(() => new CovenantMutationTransaction(connection, transaction));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionQuota_Missing_owner_or_ignored_counter_update_refuses_deletion(bool missingOwner)
    {
        RequireSqlCipher();

        Guid target = Guid.NewGuid();

        await SeedQuotaOwnerAsync(Canonical(target), claims: 2, consumed: 1);

        if (missingOwner)
        {
            using CovenantSqliteAuthorizationScope authorization = CovenantSqliteConnectionInitializer.Instance.Authorize(
                (SqliteConnection)_db!.Database.GetDbConnection(), CovenantSqliteAuthorizationKind.TurnCapacityMutation);

            await ExecuteAsync("DELETE FROM session_turn_quota_state WHERE SessionId = @id;", ("@id", Canonical(target)));
        }
        else
        {
            await ExecuteAsync("""
                CREATE TRIGGER ignore_quota_release BEFORE UPDATE ON installation_turn_quota_state
                WHEN NEW.ClaimCount < OLD.ClaimCount
                BEGIN SELECT RAISE(IGNORE); END;
                """);
        }

        IDataRetentionService service = CreateService();

        DataRetentionRequest request = new(DataRetentionOperation.DeleteSession, TargetId: target);

        DataRetentionPlan plan = await service.PlanAsync(request, CancellationToken.None);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(new(request, plan.PlanId), CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(1, await CountAsync("Sessions", "Id", Canonical(target)));

        Assert.Equal((2L, 1L, 1L), await ReadInstallationQuotaAsync());

        await AssertQuotaAuthorizationClosedAsync();
    }

    private async Task SeedQuotaOwnerAsync(string sessionKey, int claims, int consumed)
    {
        await ExecuteAsync("""
            INSERT INTO Sessions(Id, Status, CreatedAt, UpdatedAt)
            VALUES(@id, 'archived', @created, @created);
            """, ("@id", sessionKey), ("@created", OldTimestamp));

        using CovenantSqliteAuthorizationScope authorization = CovenantSqliteConnectionInitializer.Instance.Authorize(
            (SqliteConnection)_db!.Database.GetDbConnection(), CovenantSqliteAuthorizationKind.TurnCapacityMutation);

        for (int index = 0; index < claims; index++)
        {
            await ExecuteAsync("""
                UPDATE session_turn_quota_state SET ClaimCount = ClaimCount + 1, ReservedFinalizationCount = ReservedFinalizationCount + 1 WHERE SessionId = @id;
                UPDATE installation_turn_quota_state SET ClaimCount = ClaimCount + 1, ReservedFinalizationCount = ReservedFinalizationCount + 1;
                """, ("@id", sessionKey));
        }

        for (int index = 0; index < consumed; index++)
        {
            await ExecuteAsync("""
                UPDATE session_turn_quota_state SET ReservedFinalizationCount = ReservedFinalizationCount - 1, ConsumedFinalizationCount = ConsumedFinalizationCount + 1 WHERE SessionId = @id;
                UPDATE installation_turn_quota_state SET ReservedFinalizationCount = ReservedFinalizationCount - 1, ConsumedFinalizationCount = ConsumedFinalizationCount + 1;
                """, ("@id", sessionKey));
        }
    }

    private async Task<(long Claims, long Reserved, long Consumed)> ReadInstallationQuotaAsync()
    {
        await using SqliteCommand command = ((SqliteConnection)_db!.Database.GetDbConnection()).CreateCommand();

        command.CommandText = "SELECT ClaimCount, ReservedFinalizationCount, ConsumedFinalizationCount FROM installation_turn_quota_state WHERE StateKey = 1;";

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());

        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private async Task AssertQuotaAuthorizationClosedAsync()
    {
        SqliteException failure = await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            "UPDATE installation_turn_quota_state SET ConsumedFinalizationCount = ConsumedFinalizationCount + 1;"));

        Assert.Contains("authorized turn capacity mutation scope", failure.Message, StringComparison.Ordinal);
    }
}
