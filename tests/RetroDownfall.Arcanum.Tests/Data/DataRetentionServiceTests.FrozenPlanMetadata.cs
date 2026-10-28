using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{
    [SkippableTheory]
    [InlineData("archive", false)]
    [InlineData("archive", true)]
    [InlineData("delete", false)]
    [InlineData("delete", true)]
    [InlineData("stricter", false)]
    [InlineData("stricter", true)]
    [InlineData("unchanged", false)]
    [InlineData("unchanged", true)]
    public async Task Rv240_Covenant_decoration_preserves_fresh_prune_authority_after_start(string change, bool hosted)
    {
        RequireSqlCipher();

        (Guid changed, _) = await SeedSessionAsync(pinned: false);

        (Guid clean, _) = await SeedSessionAsync(pinned: false);

        await ExecuteAsync("UPDATE Sessions SET Status = 'active'");

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.ActiveSessions = EnabledRule();

        if (change == "stricter")
        {
            settings.Retention.ArchivedSessions = EnabledRule(365);

            await ExecuteAsync("UPDATE Sessions SET UpdatedAt = @at WHERE Id = @id",
                ("@at", UtcInstantText.Format(DateTimeOffset.UtcNow.AddDays(-10))), ("@id", Canonical(changed)));
        }

        settings.Retention.AutomaticSweepsEnabled = hosted;

        RecordingCovenantOperationGate covenant = new();

        LongRunningOperationStore nativeStore = new(_db!, TestOrdinaryConnectionFactory.For(_db!));

        bool started = false;

        HeartbeatCountingOperationStore operations = new(nativeStore)
        {
            AfterStartAsync = async cancellationToken =>
            {
                started = true;

                if (change == "unchanged")
                {
                    return;
                }

                await using ArcanumDbContext independent = _fixture.CreateContext(_dbPath);

                await using SqliteCommand command = ((SqliteConnection)independent.Database.GetDbConnection()).CreateCommand();

                command.CommandText = change is "archive" or "stricter"
                    ? "UPDATE Sessions SET Status = 'archived' WHERE lower(replace(Id, '-', '')) = @id"
                    : "DELETE FROM Sessions WHERE lower(replace(Id, '-', '')) = @id";

                command.Parameters.AddWithValue("@id", changed.ToString("N"));

                using CovenantSqliteAuthorizationScope authorization = CovenantSqliteConnectionInitializer.Instance.Authorize(
                    (SqliteConnection)independent.Database.GetDbConnection(), CovenantSqliteAuthorizationKind.SessionRetention);

                Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken));
            },
        };

        DataRetentionService service = CreateService(settings, operationStore: operations,
            operationOwnership: new LongRunningOperationOwnership(), covenantGate: covenant);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan preview = await service.PlanAsync(request);

        Assert.NotNull(preview.Covenant);

        Assert.Equal(2, preview.CandidateIds.Length);

        if (hosted)
        {
            GrimoireConnectionAdmissionGate admission = new(TimeProvider.System);

            Assert.True(admission.TryAcquireWorkLease(GrimoireWorkKind.DataRetentionSweep, out IGrimoireWorkLease? acquired));

            await using IGrimoireWorkLease lease = acquired!;

            DataRetentionHostedSweepOutcome outcome = await service.ApplyOrResumeHostedPruneAsync(null, lease, CancellationToken.None);

            Assert.True(started);

            Assert.Equal(DataRetentionHostedSweepDisposition.Concluded, outcome.Disposition);

            Assert.True(outcome.Result is { IsSuccess: true }, outcome.Result?.Error.Message);
        }
        else
        {
            Result<DataRetentionApplyResult> applied = await service.ApplyAsync(new(request, preview.PlanId));

            Assert.True(started);

            Assert.True(applied.IsSuccess, applied.Error.Message);
        }

        Assert.Equal(change is "archive" or "stricter" ? 1 : 0, await CountAsync("Sessions", "Id", Canonical(changed)));

        Assert.Equal(0, await CountAsync("Sessions", "Id", Canonical(clean)));

        Assert.Equal(0, covenant.LiveLeases);

        Assert.Equal(1, covenant.PeakConcurrentLeases);

        LongRunningOperation operation = Assert.Single(await nativeStore.ListAsync(
            new LongRunningOperationQuery(Kind: LongRunningOperationKinds.DataRetentionPrune)));

        Assert.Equal(2, operation.CheckpointVersion);

        string[] lines = System.Text.Encoding.UTF8.GetString(Assert.IsType<byte[]>(operation.CheckpointPayload)).Split('\n');

        Assert.Equal("ARCADATA2", lines[0]);

        DateTimeOffset generatedAt = UtcInstantText.Parse(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(
            Assert.Single(lines, line => line.StartsWith("G:", StringComparison.Ordinal))[2..])));

        string[] candidateLines = lines.Where(line => line.StartsWith("C:", StringComparison.Ordinal)).ToArray();

        Assert.Equal(2, candidateLines.Length);

        Assert.All(candidateLines, line =>
        {
            string[] fields = line.Split(':');

            string candidate = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(fields[1]));

            Assert.Contains(candidate, new[] { "session:" + changed.ToString("D"), "session:" + clean.ToString("D") });

            DateTimeOffset cutoff = UtcInstantText.Parse(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(fields[2])));

            Assert.Equal(generatedAt.AddDays(-1), cutoff);
        });
    }

    [SkippableTheory]
    [InlineData(IdempotencyClaimState.Claimed, true)]
    [InlineData(IdempotencyClaimState.Running, true)]
    [InlineData(IdempotencyClaimState.Running, false)]
    public async Task Rv242_plan_distinguishes_expired_claims_without_replaying_or_pruning_them(IdempotencyClaimState state, bool expired)
    {
        RequireSqlCipher();

        Guid id = Guid.NewGuid();

        DateTimeOffset now = DateTimeOffset.UtcNow;

        DateTimeOffset lease = expired ? now.AddDays(-2) : now.AddDays(1);

        await ExecuteAsync("""
            INSERT INTO IdempotencyClaims
                (Id, ClaimKeyHash, FingerprintHash, State, OwnerId, LeaseExpiresAt, HeartbeatAt,
                 TerminalStreamComplete, CreatedAt, UpdatedAt)
            VALUES (@id, @key, 'frozen-plan-fingerprint', @state, 'stale-test-owner', @lease, @old, 0, @old, @old)
            """,
            ("@id", id.ToString("N")),
            ("@key", "expired-test-key-" + id.ToString("N")),
            ("@state", (int)state),
            ("@lease", UtcInstantText.Format(lease)),
            ("@old", OldTimestamp));

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.IdempotencyClaims = EnabledRule();

        DataRetentionService service = CreateService(settings);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(request);

        DataRetentionBlocker blocker = Assert.Single(plan.Blockers, item => item.DataClass == RetentionDataClass.IdempotencyClaims);

        Assert.Equal(expired ? "Data.IdempotencyLeaseExpired" : "Data.IdempotencyLeaseActive", blocker.ReasonCode);

        Assert.Contains(plan.Conflicts, conflict => conflict.Code == blocker.ReasonCode);

        Assert.DoesNotContain(plan.CandidateIds, candidate => candidate.Contains(id.ToString("N"), StringComparison.Ordinal));

        if (expired)
        {
            Assert.DoesNotContain("reconciliation", blocker.Message, StringComparison.OrdinalIgnoreCase);

            Assert.Contains("24", blocker.Message, StringComparison.Ordinal);
        }

        Assert.Equal(1, await CountAsync("IdempotencyClaims", "Id", id.ToString("N")));

        int removed = await new IdempotencyClaimStore(_db!).DeleteExpiredAsync(now.AddDays(-1));

        Assert.Equal(expired ? 1 : 0, removed);

        DataRetentionPlan after = await service.PlanAsync(request);

        Assert.Equal(expired ? 0 : 1, after.Blockers.Count(item => item.DataClass == RetentionDataClass.IdempotencyClaims));
    }

    [SkippableFact]
    public async Task Rv240_owned_decoration_carries_exact_weak_metadata_without_plan_id_authority()
    {
        RequireSqlCipher();

        _ = await SeedSessionAsync(pinned: false);

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.ArchivedSessions = EnabledRule();

        RecordingCovenantOperationGate gate = new();

        DataRetentionService service = CreateService(settings, covenantGate: gate);

        DataRetentionPlan plan = await service.PlanAsync(new(DataRetentionOperation.Prune));

        object authority = ReadPruneMetadata(service, plan, "_frozenPruneAuthorities")!;

        Assert.NotNull(authority);

        object snapshot = ReadPruneMetadata(service, plan, "_pruneSnapshots")!;

        Assert.NotNull(snapshot);

        var binder = typeof(DataRetentionService).GetMethod("BindCovenantErasurePlanIdentity",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static)!;

        DataRetentionPlan attached = Assert.IsType<DataRetentionPlan>(binder.Invoke(service, [plan, plan.Covenant!]));

        Assert.NotSame(plan, attached);

        Assert.Equal(plan.PlanId, attached.PlanId);

        Assert.Same(authority, ReadPruneMetadata(service, attached, "_frozenPruneAuthorities"));

        Assert.Same(snapshot, ReadPruneMetadata(service, attached, "_pruneSnapshots"));

        IReadOnlyDictionary<string, DateTimeOffset> cutoffs = Assert.IsAssignableFrom<IReadOnlyDictionary<string, DateTimeOffset>>(
            authority.GetType().GetProperty("Cutoffs")!.GetValue(authority));

        Assert.Equal(plan.GeneratedAt.AddDays(-1), Assert.Single(cutoffs).Value);

        DataRetentionPlan callerClone = plan with { };

        Assert.Null(ReadPruneMetadata(service, callerClone, "_frozenPruneAuthorities"));

        Assert.Null(ReadPruneMetadata(service, callerClone, "_pruneSnapshots"));

        DataRetentionPlan unowned = Assert.IsType<DataRetentionPlan>(binder.Invoke(service, [callerClone, plan.Covenant!]));

        Assert.Null(ReadPruneMetadata(service, unowned, "_frozenPruneAuthorities"));

        Assert.Null(ReadPruneMetadata(service, unowned, "_pruneSnapshots"));

        Assert.Equal(0, gate.LiveLeases);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rv240_selection_to_freeze_status_change_preserves_changed_candidate_and_keeps_other_authority(bool disappeared)
    {
        RequireSqlCipher();

        (Guid changed, _) = await SeedSessionAsync(pinned: false);

        (Guid clean, _) = await SeedSessionAsync(pinned: false);

        await ExecuteAsync("UPDATE Sessions SET Status = 'active'");

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.ActiveSessions = EnabledRule();

        DataRetentionService service = CreateService(settings, covenantGate: new RecordingCovenantOperationGate());

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        await using ArcanumDbContext independent = _fixture.CreateContext(_dbPath);

        bool interleaved = false;

        bool selectionRead = false;

        Exception? callbackFailure = null;

        SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.strdelegate_trace)((_, statement) =>
        {
            if (interleaved || statement.StartsWith("--", StringComparison.Ordinal)) return;

            if (statement.StartsWith("SELECT session.Id\nFROM Sessions session", StringComparison.Ordinal))
            {
                selectionRead = true;

                return;
            }

            if (!selectionRead) return;

            interleaved = true;

            try
            {
                SqliteConnection other = (SqliteConnection)independent.Database.GetDbConnection();

                using CovenantSqliteAuthorizationScope authorization = CovenantSqliteConnectionInitializer.Instance.Authorize(
                    other, CovenantSqliteAuthorizationKind.SessionRetention);

                using SqliteCommand command = other.CreateCommand();

                command.CommandText = disappeared
                    ? "DELETE FROM Sessions WHERE lower(replace(Id, '-', '')) = @id"
                    : "UPDATE Sessions SET Status = 'archived' WHERE lower(replace(Id, '-', '')) = @id";

                command.Parameters.AddWithValue("@id", changed.ToString("N"));

                if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("The native interleave did not affect its selected row.");
            }
            catch (Exception exception)
            {
                callbackFailure = exception;
            }
        }), null);

        DataRetentionPlan? plan = null;

        Exception? planningFailure;

        try
        {
            planningFailure = await Record.ExceptionAsync(async () => plan = await service.PlanAsync(new(DataRetentionOperation.Prune)));
        }
        finally
        {
            SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.strdelegate_trace)null!, null);
        }

        Assert.True(interleaved);

        Assert.Null(callbackFailure);

        Assert.Null(planningFailure);

        Assert.NotNull(plan);

        object frozen = ReadPruneMetadata(service, plan, "_frozenPruneAuthorities")!;

        IReadOnlyDictionary<string, DateTimeOffset> selectedCutoffs = Assert.IsAssignableFrom<IReadOnlyDictionary<string, DateTimeOffset>>(
            frozen.GetType().GetProperty("Cutoffs")!.GetValue(frozen));

        Assert.Equal(plan.GeneratedAt.AddDays(-1), selectedCutoffs["session:" + changed.ToString("D")]);

        Assert.Equal(plan.GeneratedAt.AddDays(-1), selectedCutoffs["session:" + clean.ToString("D")]);

        Assert.Contains(plan.CandidateIds, candidate => candidate.Contains(clean.ToString("D"), StringComparison.OrdinalIgnoreCase));

        DataRetentionPlan fresh = await service.PlanAsync(new(DataRetentionOperation.Prune));

        Result<DataRetentionApplyResult> applied = await service.ApplyAsync(new(new(DataRetentionOperation.Prune), fresh.PlanId));

        Assert.True(applied.IsSuccess, applied.Error.Message);

        Assert.Equal(disappeared ? 0 : 1, await CountAsync("Sessions", "Id", Canonical(changed)));

        Assert.Equal(0, await CountAsync("Sessions", "Id", Canonical(clean)));
    }

    [SkippableFact]
    public async Task Rv240_duplicate_selection_keeps_one_candidate_measurement_and_the_stricter_original_cutoff()
    {
        RequireSqlCipher();

        (Guid sessionId, _) = await SeedSessionAsync(pinned: false);

        await ExecuteAsync("UPDATE Sessions SET Status = 'active'");

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.ActiveSessions = EnabledRule();

        settings.Retention.ArchivedSessions = EnabledRule(7);

        DataRetentionService service = CreateService(settings, covenantGate: new RecordingCovenantOperationGate());

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        await using ArcanumDbContext independent = _fixture.CreateContext(_dbPath);

        int selections = 0;

        Exception? callbackFailure = null;

        SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.strdelegate_trace)((_, statement) =>
        {
            if (!statement.StartsWith("SELECT session.Id\nFROM Sessions session", StringComparison.Ordinal) || ++selections != 2) return;

            try
            {
                using SqliteCommand update = ((SqliteConnection)independent.Database.GetDbConnection()).CreateCommand();

                update.CommandText = "UPDATE Sessions SET Status = 'archived'";

                Assert.Equal(1, update.ExecuteNonQuery());
            }
            catch (Exception exception)
            {
                callbackFailure = exception;
            }
        }), null);

        DataRetentionPlan plan;

        try
        {
            plan = await service.PlanAsync(new(DataRetentionOperation.Prune));
        }
        finally
        {
            SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.strdelegate_trace)null!, null);
        }

        Assert.Equal(2, selections);

        Assert.Null(callbackFailure);

        Assert.Equal("session:" + sessionId.ToString("D"), Assert.Single(plan.CandidateIds));

        Assert.Equal(1, plan.Items.Where(item => item.DataClass is RetentionDataClass.ActiveSessions or RetentionDataClass.ArchivedSessions).Sum(item => item.Rows));

        object authority = ReadPruneMetadata(service, plan, "_frozenPruneAuthorities")!;

        IReadOnlyDictionary<string, DateTimeOffset> cutoffs = Assert.IsAssignableFrom<IReadOnlyDictionary<string, DateTimeOffset>>(
            authority.GetType().GetProperty("Cutoffs")!.GetValue(authority));

        Assert.Equal(plan.GeneratedAt.AddDays(-7), Assert.Single(cutoffs).Value);
    }

    [SkippableFact]
    public async Task Rv240_cancellation_after_selection_preserves_the_caller_token_without_publishing_work()
    {
        RequireSqlCipher();

        (Guid retained, _) = await SeedSessionAsync(pinned: false);

        await ExecuteAsync("UPDATE Sessions SET Status = 'active'");

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.ActiveSessions = EnabledRule();

        RecordingCovenantOperationGate gate = new();

        DataRetentionService service = CreateService(settings, covenantGate: gate);

        using CancellationTokenSource cancellation = new();

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        bool selected = false;

        SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.strdelegate_trace)((_, statement) =>
        {
            if (selected) cancellation.Cancel();

            if (statement.StartsWith("SELECT session.Id\nFROM Sessions session", StringComparison.Ordinal)) selected = true;
        }), null);

        try
        {
            OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.PlanAsync(new(DataRetentionOperation.Prune), cancellation.Token));

            Assert.Equal(cancellation.Token, failure.CancellationToken);
        }
        finally
        {
            SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.strdelegate_trace)null!, null);
        }

        Assert.True(selected);

        Assert.Equal(1, await CountAsync("Sessions", "Id", Canonical(retained)));

        Assert.Empty(await new LongRunningOperationStore(_db!, TestOrdinaryConnectionFactory.For(_db!)).ListAsync(new()));

        Assert.Equal(0, gate.LiveLeases);
    }

    [SkippableTheory]
    [InlineData(IdempotencyClaimState.Claimed, -864000000000L, -1L, true)]
    [InlineData(IdempotencyClaimState.Running, -864000000000L, -1L, true)]
    [InlineData(IdempotencyClaimState.Running, -864000000000L, 0L, false)]
    [InlineData(IdempotencyClaimState.Running, -864000000000L, 1L, false)]
    [InlineData(IdempotencyClaimState.Running, 0L, -1L, false)]
    [InlineData(IdempotencyClaimState.Running, 1L, -1L, false)]
    [InlineData(IdempotencyClaimState.Running, -864000000000L, 864000000000L, false)]
    [InlineData(IdempotencyClaimState.Running, -864000000000L, 864000000001L, false)]
    [InlineData((IdempotencyClaimState)99, -864000000000L, -1L, false)]
    [InlineData(IdempotencyClaimState.Completed, -1L, 1728000000000L, true)]
    [InlineData(IdempotencyClaimState.Failed, -1L, 1728000000000L, true)]
    [InlineData(IdempotencyClaimState.Abandoned, 0L, -1L, false)]
    public async Task Rv242_diagnostics_preserve_actual_creation_and_expiry_cutoffs_at_native_precision(
        IdempotencyClaimState state, long createdOffset, long leaseOffset, bool cleanupEligible)
    {
        RequireSqlCipher();

        FakeTimeProvider clock = new();

        clock.SetUtcNow(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));

        DateTimeOffset now = clock.GetUtcNow();

        DateTimeOffset cleanupCutoff = now.AddHours(-24);

        Guid id = Guid.NewGuid();

        DateTimeOffset lease = cleanupCutoff.AddTicks(leaseOffset);

        await ExecuteAsync("""
            INSERT INTO IdempotencyClaims
                (Id, ClaimKeyHash, FingerprintHash, State, OwnerId, LeaseExpiresAt, HeartbeatAt,
                 TerminalStreamComplete, CreatedAt, UpdatedAt)
            VALUES (@id, @key, 'boundary-fingerprint', @state, 'boundary-owner', @lease, @created, 0, @created, @created)
            """,
            ("@id", id.ToString("N")),
            ("@key", "boundary-key-" + id.ToString("N")),
            ("@state", (int)state),
            ("@lease", UtcInstantText.Format(lease)),
            ("@created", UtcInstantText.Format(cleanupCutoff.AddTicks(createdOffset))));

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.IdempotencyClaims = EnabledRule();

        DataRetentionService service = CreateService(settings, timeProvider: clock);

        DataRetentionPlan plan = await service.PlanAsync(new(DataRetentionOperation.Prune));

        if (state is IdempotencyClaimState.Claimed or IdempotencyClaimState.Running)
        {
            DataRetentionBlocker blocker = Assert.Single(plan.Blockers, item => item.ResourceId == id.ToString("D"));

            Assert.Equal(lease > now ? "Data.IdempotencyLeaseActive" : "Data.IdempotencyLeaseExpired", blocker.ReasonCode);

            Assert.DoesNotContain(plan.CandidateIds, item => item.Contains(id.ToString("N"), StringComparison.OrdinalIgnoreCase));
        }
        else Assert.DoesNotContain(plan.Blockers, item => item.ResourceId == id.ToString("D"));

        IdempotencyClaimStore store = new(_db!);

        IdempotencyClaim? before = await store.GetByIdAsync(id);

        Assert.NotNull(before);

        Assert.Equal(cleanupEligible ? 1 : 0, await store.DeleteExpiredAsync(cleanupCutoff));

        Assert.Equal(cleanupEligible ? null : before, await store.GetByIdAsync(id));

        DataRetentionPlan after = await service.PlanAsync(new(DataRetentionOperation.Prune));

        if (cleanupEligible) Assert.DoesNotContain(after.Blockers, item => item.ResourceId == id.ToString("D"));
        else Assert.Equal(plan.Blockers, after.Blockers);
    }

    [SkippableFact]
    public async Task Rv240_a_session_missing_before_selection_never_gains_frozen_authority()
    {
        RequireSqlCipher();

        (Guid missing, _) = await SeedSessionAsync(pinned: false);

        (Guid retained, _) = await SeedSessionAsync(pinned: false);

        await ExecuteAsync("UPDATE Sessions SET Status = 'active'");

        await ExecuteSessionRetentionAsync("DELETE FROM Sessions WHERE Id = @id", ("@id", Canonical(missing)));

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.ActiveSessions = EnabledRule();

        DataRetentionService service = CreateService(settings, covenantGate: new RecordingCovenantOperationGate());

        DataRetentionPlan plan = await service.PlanAsync(new(DataRetentionOperation.Prune));

        Assert.Equal("session:" + retained.ToString("D"), Assert.Single(plan.CandidateIds));

        object authority = ReadPruneMetadata(service, plan, "_frozenPruneAuthorities")!;

        IReadOnlyDictionary<string, DateTimeOffset> cutoffs = Assert.IsAssignableFrom<IReadOnlyDictionary<string, DateTimeOffset>>(
            authority.GetType().GetProperty("Cutoffs")!.GetValue(authority));

        Assert.Equal(plan.CandidateIds[0], Assert.Single(cutoffs).Key);

        Assert.Equal(plan.GeneratedAt.AddDays(-1), Assert.Single(cutoffs).Value);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rv242_current_independent_renewal_removes_stale_expiry_guidance_without_pruning(bool reclaim)
    {
        RequireSqlCipher();

        DateTimeOffset now = DateTimeOffset.UtcNow;

        string key = "renewed-claim-" + Guid.NewGuid().ToString("N");

        IdempotencyClaimStore store = new(_db!);

        IdempotencyClaimAcquireResult acquired = await store.TryAcquireAsync(
            new(key, "renewal-fingerprint", "original-owner", now.AddDays(-2), now.AddDays(-3)));

        Assert.True(acquired.Acquired);

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.IdempotencyClaims = EnabledRule();

        DataRetentionService service = CreateService(settings);

        DataRetentionPlan before = await service.PlanAsync(new(DataRetentionOperation.Prune));

        Assert.Contains(before.Blockers, blocker => blocker.ReasonCode == "Data.IdempotencyLeaseExpired");

        await using ArcanumDbContext other = _fixture.CreateContext(_dbPath);

        IdempotencyClaimStore owner = new(other);

        DateTimeOffset renewedUntil = now.AddMinutes(5);

        Assert.True(reclaim
            ? await owner.TryReclaimAsync(acquired.Claim.Id, "new-owner", renewedUntil)
            : await owner.HeartbeatAsync(acquired.Claim.Id, acquired.Claim.OwnerId, renewedUntil));

        IdempotencyClaim? renewed = await store.TryGetAsync(key);

        Assert.NotNull(renewed);

        Assert.Equal(renewedUntil, renewed.LeaseExpiresAt);

        Assert.Equal(reclaim ? "new-owner" : acquired.Claim.OwnerId, renewed.OwnerId);

        Assert.Equal(0, await store.DeleteExpiredAsync(now.AddHours(-24)));

        DataRetentionPlan after = await service.PlanAsync(new(DataRetentionOperation.Prune));

        Assert.DoesNotContain(after.Blockers, blocker => blocker.ReasonCode == "Data.IdempotencyLeaseExpired");

        Assert.DoesNotContain(after.CandidateIds, candidate => candidate.Contains(acquired.Claim.Id.ToString("N"), StringComparison.OrdinalIgnoreCase));

        Assert.Equal(renewed, await store.TryGetAsync(key));
    }

    private static object? ReadPruneMetadata(DataRetentionService service, DataRetentionPlan plan, string field)
    {
        object table = typeof(DataRetentionService).GetField(field,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(service)!;

        object?[] arguments = [plan, null];

        bool found = (bool)table.GetType().GetMethod("TryGetValue")!.Invoke(table, arguments)!;

        return found ? arguments[1] : null;
    }
}
