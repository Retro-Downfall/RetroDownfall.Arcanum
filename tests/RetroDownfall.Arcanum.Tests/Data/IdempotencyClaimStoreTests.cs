using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data;

[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class IdempotencyClaimStoreTests : IAsyncLifetime
{
    private readonly GrimoireFixture _fixture;
    private string _dbPath = string.Empty;
    private ArcanumDbContext? _db;

    public IdempotencyClaimStoreTests(GrimoireFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync()
    {
        _dbPath = _fixture.CopyDatabase();
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
    }

    [SkippableFact]
    public async Task TryAcquireAsync_WhenCompletedStreamIsTerminal_ReturnsReplayWithLinkedRun()
    {
        RequireSqlCipher();

        IdempotencyClaimStore store = new(_db!);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        IdempotencyClaim acquired = await AcquireAsync(
            store,
            "completed-terminal",
            "fingerprint",
            "owner-1",
            now.AddMinutes(5),
            now);
        Guid runId = Guid.NewGuid();

        await store.CompleteAsync(
            acquired.Id,
            "owner-1",
            statusCode: 201,
            contentType: "application/json",
            responseBody: """{"created":true}""",
            terminalStreamValid: true,
            runId);

        IdempotencyClaimAcquireResult replay = await store.TryAcquireAsync(
            new IdempotencyClaimAcquireRequest(
                "completed-terminal",
                "fingerprint",
                "owner-2",
                now.AddMinutes(10),
                now.AddMinutes(1)));

        Assert.False(replay.Conflict);
        Assert.False(replay.Acquired);
        Assert.Equal(acquired.Id, replay.Claim.Id);
        Assert.Equal(IdempotencyClaimState.Completed, replay.Claim.State);
        Assert.Equal("owner-1", replay.Claim.OwnerId);
        Assert.Equal(201, replay.Claim.StatusCode);
        Assert.Equal("application/json", replay.Claim.ContentType);
        Assert.Equal("""{"created":true}""", replay.Claim.ResponseBody);
        Assert.True(replay.Claim.TerminalStreamComplete);
        Assert.Equal(runId, replay.Claim.RunId);
    }

    [SkippableFact]
    public async Task TryAcquireAsync_WhenExistingFingerprintDiffers_ReturnsConflictWithoutChangingLease()
    {
        RequireSqlCipher();

        IdempotencyClaimStore store = new(_db!);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        IdempotencyClaim acquired = await AcquireAsync(
            store,
            "fingerprint-conflict",
            "fingerprint-1",
            "owner-1",
            now.AddMinutes(5),
            now);

        IdempotencyClaimAcquireResult conflict = await store.TryAcquireAsync(
            new IdempotencyClaimAcquireRequest(
                "fingerprint-conflict",
                "fingerprint-2",
                "owner-2",
                now.AddMinutes(10),
                now.AddMinutes(1)));

        Assert.True(conflict.Conflict);
        Assert.False(conflict.Acquired);
        Assert.Equal(acquired, conflict.Claim);

        IdempotencyClaim? persisted = await store.TryGetAsync("fingerprint-conflict");
        Assert.Equal(acquired, persisted);
    }

    [SkippableFact]
    public async Task CompleteAsync_WhenTerminalStreamIsInvalid_MarksClaimAbandonedWithoutReplayPayload()
    {
        RequireSqlCipher();

        IdempotencyClaimStore store = new(_db!);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        IdempotencyClaim acquired = await AcquireAsync(
            store,
            "invalid-terminal",
            "fingerprint",
            "owner-1",
            now.AddMinutes(5),
            now);

        await store.CompleteAsync(
            acquired.Id,
            "owner-1",
            statusCode: 200,
            contentType: "application/json",
            responseBody: """{"partial":true}""",
            terminalStreamValid: false,
            runId: Guid.NewGuid());

        IdempotencyClaim? abandoned = await store.TryGetAsync("invalid-terminal");
        Assert.NotNull(abandoned);
        Assert.Equal(IdempotencyClaimState.Abandoned, abandoned.State);
        Assert.Null(abandoned.StatusCode);
        Assert.Null(abandoned.ContentType);
        Assert.Null(abandoned.ResponseBody);
        Assert.Null(abandoned.RunId);
        Assert.False(abandoned.TerminalStreamComplete);
    }

    [SkippableFact]
    public async Task TryGetAsync_WhenConnectionIsClosed_ReopensAndReadsClaim()
    {
        RequireSqlCipher();

        IdempotencyClaimStore store = new(_db!);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        IdempotencyClaim acquired = await AcquireAsync(
            store,
            "closed-connection",
            "fingerprint",
            "owner",
            now.AddMinutes(5),
            now);

        await _db!.Database.CloseConnectionAsync();
        Assert.Equal(System.Data.ConnectionState.Closed, _db.Database.GetDbConnection().State);

        IdempotencyClaim? loaded = await store.TryGetAsync("closed-connection");

        Assert.Equal(acquired, loaded);
        Assert.Equal(System.Data.ConnectionState.Open, _db.Database.GetDbConnection().State);
    }

    [SkippableFact]
    public async Task TryAcquireAsync_WhenCompletedRowLacksTerminalMarker_DoesNotReplayOrReclaim()
    {
        RequireSqlCipher();

        IdempotencyClaimStore store = new(_db!);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        IdempotencyClaim acquired = await AcquireAsync(
            store,
            "completed-nonterminal",
            "fingerprint",
            "owner-1",
            now.AddMinutes(-1),
            now.AddMinutes(-5));
        await ExecuteNonQueryAsync(
            """
            UPDATE "IdempotencyClaims"
            SET "State" = @state, "TerminalStreamComplete" = 0
            WHERE "Id" = @id;
            """,
            ("@state", (int)IdempotencyClaimState.Completed),
            ("@id", acquired.Id.ToString("N")));

        IdempotencyClaimAcquireResult result = await store.TryAcquireAsync(
            new IdempotencyClaimAcquireRequest(
                "completed-nonterminal",
                "fingerprint",
                "owner-2",
                now.AddMinutes(5),
                now));

        Assert.False(result.Conflict);
        Assert.False(result.Acquired);
        Assert.Equal(acquired.Id, result.Claim.Id);
        Assert.Equal(IdempotencyClaimState.Completed, result.Claim.State);
        Assert.False(result.Claim.TerminalStreamComplete);
        Assert.Equal("owner-1", result.Claim.OwnerId);
    }

    [SkippableFact]
    public async Task TryAcquireAsync_WhenFailed_ReclaimsAndClearsStaleResponse()
    {
        RequireSqlCipher();

        IdempotencyClaimStore store = new(_db!);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        IdempotencyClaim acquired = await AcquireAsync(
            store,
            "failed-reclaim",
            "fingerprint",
            "owner-1",
            now.AddMinutes(5),
            now);
        Guid linkedRunId = Guid.NewGuid();

        await store.MarkFailedAsync(acquired.Id, "owner-1");
        await ExecuteNonQueryAsync(
            """
            UPDATE "IdempotencyClaims"
            SET "StatusCode" = 503,
                "ContentType" = 'application/problem+json',
                "ResponseBody" = '{"error":true}',
                "TerminalStreamComplete" = 1,
                "RunId" = @runId
            WHERE "Id" = @id;
            """,
            ("@runId", linkedRunId.ToString("N")),
            ("@id", acquired.Id.ToString("N")));

        IdempotencyClaimAcquireResult reclaimed = await store.TryAcquireAsync(
            new IdempotencyClaimAcquireRequest(
                "failed-reclaim",
                "fingerprint",
                "owner-2",
                now.AddMinutes(10),
                now.AddMinutes(1)));

        Assert.False(reclaimed.Conflict);
        Assert.True(reclaimed.Acquired);
        Assert.Equal(acquired.Id, reclaimed.Claim.Id);
        Assert.Equal(IdempotencyClaimState.Running, reclaimed.Claim.State);
        Assert.Equal("owner-2", reclaimed.Claim.OwnerId);
        Assert.Null(reclaimed.Claim.StatusCode);
        Assert.Null(reclaimed.Claim.ContentType);
        Assert.Null(reclaimed.Claim.ResponseBody);
        Assert.Equal(linkedRunId, reclaimed.Claim.RunId);
        Assert.False(reclaimed.Claim.TerminalStreamComplete);
    }

    [SkippableFact]
    public async Task TryAcquireAsync_WhenClaimedLeaseExpired_ReclaimsForNewOwner()
    {
        RequireSqlCipher();

        IdempotencyClaimStore store = new(_db!);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        IdempotencyClaim acquired = await AcquireAsync(
            store,
            "claimed-expired",
            "fingerprint",
            "owner-1",
            now.AddMinutes(-5),
            now.AddMinutes(-10));
        await ExecuteNonQueryAsync(
            """
            UPDATE "IdempotencyClaims"
            SET "State" = @state
            WHERE "Id" = @id;
            """,
            ("@state", (int)IdempotencyClaimState.Claimed),
            ("@id", acquired.Id.ToString("N")));

        IdempotencyClaimAcquireResult reclaimed = await store.TryAcquireAsync(
            new IdempotencyClaimAcquireRequest(
                "claimed-expired",
                "fingerprint",
                "owner-2",
                now.AddMinutes(5),
                now));

        Assert.False(reclaimed.Conflict);
        Assert.True(reclaimed.Acquired);
        Assert.Equal(IdempotencyClaimState.Running, reclaimed.Claim.State);
        Assert.Equal("owner-2", reclaimed.Claim.OwnerId);
        Assert.Equal(now.AddMinutes(5), reclaimed.Claim.LeaseExpiresAt);
    }

    [SkippableFact]
    public async Task TryAcquireAsync_WhenAnotherConnectionWinsExpiredReclaim_ReturnsLiveWinner()
    {
        RequireSqlCipher();

        const string claimKey = "expired-reclaim-race";
        DateTimeOffset now = DateTimeOffset.UtcNow;
        IdempotencyClaimStore losingStore = new(_db!);
        _ = await AcquireAsync(
            losingStore,
            claimKey,
            "fingerprint",
            "expired-owner",
            now.AddMinutes(-5),
            now.AddMinutes(-10));

        SqliteConnection losingConnection =
            (SqliteConnection)_db!.Database.GetDbConnection();
        losingConnection.CreateCollation(
            "IDEMPOTENCY_RECLAIM_RACE",
            static (left, right) => string.Compare(
                left,
                right,
                StringComparison.Ordinal));
        await RebuildClaimsWithRaceCollationAsync();

        await using ArcanumDbContext winningDb =
            _fixture.CreateContext(_dbPath);
        SqliteConnection winningConnection =
            (SqliteConnection)winningDb.Database.GetDbConnection();
        winningConnection.CreateCollation(
            "IDEMPOTENCY_RECLAIM_RACE",
            static (left, right) => string.Compare(
                left,
                right,
                StringComparison.Ordinal));
        IdempotencyClaimStore winningStore = new(winningDb);

        TaskCompletionSource losingReadEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource allowLosingRead =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        int interceptNextComparison = 1;

        losingConnection.CreateCollation(
            "IDEMPOTENCY_RECLAIM_RACE",
            (left, right) =>
            {
                if (Interlocked.Exchange(
                        ref interceptNextComparison,
                        0) == 1)
                {
                    losingReadEntered.TrySetResult();
                    allowLosingRead.Task.GetAwaiter().GetResult();
                }

                return string.Compare(
                    left,
                    right,
                    StringComparison.Ordinal);
            });

        Task<IdempotencyClaimAcquireResult> losingAcquire =
            Task.Run(
                () => losingStore.TryAcquireAsync(
                    new IdempotencyClaimAcquireRequest(
                        claimKey,
                        "fingerprint",
                        "losing-owner",
                        now.AddMinutes(5),
                        now)));

        try
        {
            // Generous orchestration budget: the collation callback can be delayed well past 5s
            // on coverage-instrumented CI runners under parallel load.
            await losingReadEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(30));

            IdempotencyClaimAcquireResult winner =
                await winningStore.TryAcquireAsync(
                    new IdempotencyClaimAcquireRequest(
                        claimKey,
                        "fingerprint",
                        "winning-owner",
                        now.AddMinutes(10),
                        now));

            Assert.True(winner.Acquired);
            Assert.Equal(
                "winning-owner",
                winner.Claim.OwnerId);
        }
        finally
        {
            allowLosingRead.TrySetResult();
        }

        IdempotencyClaimAcquireResult loser =
            await losingAcquire.WaitAsync(
                TimeSpan.FromSeconds(30));

        Assert.False(loser.Conflict);
        Assert.False(loser.Acquired);
        Assert.Equal(
            "winning-owner",
            loser.Claim.OwnerId);
        Assert.Equal(
            IdempotencyClaimState.Running,
            loser.Claim.State);
        Assert.Equal(
            now.AddMinutes(10),
            loser.Claim.LeaseExpiresAt);
        Assert.Equal(
            await winningStore.TryGetAsync(claimKey),
            loser.Claim);
    }

    [SkippableFact]
    public async Task TryAcquireAsync_WhenRequestClockRunsAhead_DoesNotStealLiveLease()
    {
        RequireSqlCipher();

        IdempotencyClaimStore store = new(_db!);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        IdempotencyClaim acquired = await AcquireAsync(
            store,
            "clock-skew",
            "fingerprint",
            "owner-1",
            now.AddMinutes(5),
            now);

        IdempotencyClaimAcquireResult result = await store.TryAcquireAsync(
            new IdempotencyClaimAcquireRequest(
                "clock-skew",
                "fingerprint",
                "owner-2",
                now.AddMinutes(20),
                now.AddMinutes(10)));

        Assert.False(result.Conflict);
        Assert.False(result.Acquired);
        Assert.Equal(acquired.Id, result.Claim.Id);
        Assert.Equal("owner-1", result.Claim.OwnerId);
        Assert.Equal(now.AddMinutes(5), result.Claim.LeaseExpiresAt);

        IdempotencyClaim? persisted = await store.TryGetAsync("clock-skew");
        Assert.NotNull(persisted);
        Assert.Equal("owner-1", persisted.OwnerId);
    }

    [SkippableFact]
    public async Task TryAcquireAsync_WhenReclaimedRowIsDeleted_PerformsOneBoundedCreateAttempt()
    {
        RequireSqlCipher();

        IdempotencyClaimStore store = new(_db!);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        IdempotencyClaim acquired = await AcquireAsync(
            store,
            "deleted-after-reclaim",
            "fingerprint",
            "owner-1",
            now.AddMinutes(-5),
            now.AddMinutes(-10));
        string id = acquired.Id.ToString("N");
        await ExecuteNonQueryAsync(
            $"""
            CREATE TRIGGER "DeleteAfterReclaim"
            AFTER UPDATE OF "OwnerId" ON "IdempotencyClaims"
            WHEN NEW."Id" = {SqlLiteral(id)}
            BEGIN
                DELETE FROM "IdempotencyClaims" WHERE "Id" = NEW."Id";
            END;
            """);

        IdempotencyClaimAcquireResult result = await store.TryAcquireAsync(
            new IdempotencyClaimAcquireRequest(
                "deleted-after-reclaim",
                "fingerprint",
                "owner-2",
                now.AddMinutes(5),
                now));

        Assert.False(result.Conflict);
        Assert.True(result.Acquired);
        Assert.NotEqual(acquired.Id, result.Claim.Id);
        Assert.Equal("owner-2", result.Claim.OwnerId);
        Assert.Equal(IdempotencyClaimState.Running, result.Claim.State);
        Assert.Equal(
            result.Claim,
            await store.TryGetAsync("deleted-after-reclaim"));
    }

    [SkippableFact]
    public async Task TryAcquireAsync_WhenInsertFailsWithoutWinner_RethrowsDatabaseError()
    {
        RequireSqlCipher();

        const string claimKey = "insert-failure-no-winner";
        await ExecuteNonQueryAsync(
            $"""
            CREATE TRIGGER "FailClaimInsert"
            BEFORE INSERT ON "IdempotencyClaims"
            WHEN NEW."ClaimKeyHash" = {SqlLiteral(claimKey)}
            BEGIN
                SELECT RAISE(FAIL, 'simulated insert failure');
            END;
            """);
        IdempotencyClaimStore store = new(_db!);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        SqliteException exception = await Assert.ThrowsAsync<SqliteException>(
            () => store.TryAcquireAsync(new IdempotencyClaimAcquireRequest(
                claimKey,
                "fingerprint",
                "owner",
                now.AddMinutes(5),
                now)));

        Assert.Equal(19, exception.SqliteErrorCode);
        Assert.Contains("simulated insert failure", exception.Message, StringComparison.Ordinal);
        Assert.Null(await store.TryGetAsync(claimKey));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TryAcquireAsync_WhenInsertRaceHasWinner_ReturnsWinner(bool fingerprintConflict)
    {
        RequireSqlCipher();

        string claimKey = fingerprintConflict ? "insert-race-conflict" : "insert-race-match";
        const string requestFingerprint = "request-fingerprint";
        string winnerFingerprint = fingerprintConflict ? "winner-fingerprint" : requestFingerprint;
        await CreateInsertRaceTriggerAsync(claimKey, winnerFingerprint);
        IdempotencyClaimStore store = new(_db!);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        IdempotencyClaimAcquireResult result = await store.TryAcquireAsync(
            new IdempotencyClaimAcquireRequest(
                claimKey,
                requestFingerprint,
                "request-owner",
                now.AddMinutes(5),
                now));

        Assert.Equal(fingerprintConflict, result.Conflict);
        Assert.False(result.Acquired);
        Assert.Equal(claimKey, result.Claim.ClaimKeyHash);
        Assert.Equal(winnerFingerprint, result.Claim.FingerprintHash);
        Assert.Equal("race-winner", result.Claim.OwnerId);
        Assert.Equal(IdempotencyClaimState.Running, result.Claim.State);

        IdempotencyClaim? persisted = await store.TryGetAsync(claimKey);
        Assert.NotNull(persisted);
        Assert.Equal(result.Claim, persisted);
    }

    [SkippableFact]
    public async Task HeartbeatAndLinkRun_UpdateOnlyCurrentRunningOwner()
    {
        RequireSqlCipher();

        IdempotencyClaimStore store = new(_db!);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        IdempotencyClaim acquired = await AcquireAsync(
            store,
            "heartbeat-link",
            "fingerprint",
            "owner-1",
            now.AddMinutes(5),
            now);
        DateTimeOffset wrongOwnerLease = now.AddMinutes(20);

        bool wrongOwnerRenewed = await store.HeartbeatAsync(acquired.Id, "wrong-owner", wrongOwnerLease);

        IdempotencyClaim? unchanged = await store.TryGetAsync("heartbeat-link");
        Assert.NotNull(unchanged);
        Assert.False(wrongOwnerRenewed);
        Assert.Equal(acquired.LeaseExpiresAt, unchanged.LeaseExpiresAt);
        Assert.Equal(acquired.HeartbeatAt, unchanged.HeartbeatAt);

        DateTimeOffset correctOwnerLease = now.AddMinutes(10);
        DateTimeOffset heartbeatStarted = DateTimeOffset.UtcNow;
        bool currentOwnerRenewed = await store.HeartbeatAsync(acquired.Id, "owner-1", correctOwnerLease);
        DateTimeOffset heartbeatFinished = DateTimeOffset.UtcNow;
        Guid runId = Guid.NewGuid();
        await store.LinkRunAsync(acquired.Id, runId);

        IdempotencyClaim? updated = await store.TryGetAsync("heartbeat-link");
        Assert.NotNull(updated);
        Assert.True(currentOwnerRenewed);
        Assert.Equal(correctOwnerLease, updated.LeaseExpiresAt);
        Assert.InRange(updated.HeartbeatAt, heartbeatStarted, heartbeatFinished);
        Assert.Equal(runId, updated.RunId);
        Assert.Equal("owner-1", updated.OwnerId);
        Assert.Equal(IdempotencyClaimState.Running, updated.State);
    }

    [Fact]
    public void HeartbeatContract_ReportsWhetherOwnershipWasRenewed()
    {
        System.Reflection.MethodInfo? heartbeat =
            typeof(IIdempotencyClaimStore).GetMethod(nameof(IIdempotencyClaimStore.HeartbeatAsync));

        Assert.NotNull(heartbeat);
        Assert.Equal(typeof(Task<bool>), heartbeat.ReturnType);
    }

    [SkippableFact]
    public async Task HeartbeatAsync_ExtendsOriginalLease_ButCannotRenewCompletedClaim()
    {
        RequireSqlCipher();

        IdempotencyClaimStore store = new(_db!);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset originalLease = now.AddMilliseconds(100);
        IdempotencyClaim acquired = await AcquireAsync(
            store,
            "heartbeat-terminal",
            "fingerprint",
            "owner-1",
            originalLease,
            now);
        DateTimeOffset renewedLease = now.AddMinutes(5);

        Assert.True(await store.HeartbeatAsync(acquired.Id, "owner-1", renewedLease));

        IdempotencyClaim? renewed = await store.TryGetAsync("heartbeat-terminal");
        Assert.NotNull(renewed);
        Assert.Equal(renewedLease, renewed.LeaseExpiresAt);
        Assert.True(renewed.LeaseExpiresAt > originalLease);

        await store.CompleteAsync(
            acquired.Id,
            "owner-1",
            statusCode: 200,
            contentType: "application/json",
            responseBody: """{"ok":true}""",
            terminalStreamValid: true,
            runId: null);
        Assert.False(await store.HeartbeatAsync(acquired.Id, "owner-1", now.AddMinutes(10)));

        IdempotencyClaim? completed = await store.TryGetAsync("heartbeat-terminal");
        Assert.NotNull(completed);
        Assert.Equal(IdempotencyClaimState.Completed, completed.State);
        Assert.Equal(renewedLease, completed.LeaseExpiresAt);
        Assert.Equal("""{"ok":true}""", completed.ResponseBody);
        Assert.True(completed.TerminalStreamComplete);
    }

    [SkippableFact]
    public async Task MarkFailedAsync_CurrentOwnerCanRetireClaimedState()
    {
        RequireSqlCipher();

        IdempotencyClaimStore store = new(_db!);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        IdempotencyClaim acquired = await AcquireAsync(
            store,
            "claimed-retirement",
            "fingerprint",
            "owner-1",
            now.AddMinutes(5),
            now);
        await ExecuteNonQueryAsync(
            """
            UPDATE "IdempotencyClaims"
            SET "State" = @state
            WHERE "Id" = @id;
            """,
            ("@state", (int)IdempotencyClaimState.Claimed),
            ("@id", acquired.Id.ToString("N")));

        Assert.False(await store.HeartbeatAsync(
            acquired.Id,
            "owner-1",
            now.AddMinutes(10)));

        await store.CompleteAsync(
            acquired.Id,
            "owner-1",
            statusCode: 204,
            contentType: null,
            responseBody: string.Empty,
            terminalStreamValid: true,
            runId: null);

        IdempotencyClaim? stillClaimed = await store.TryGetAsync("claimed-retirement");
        Assert.NotNull(stillClaimed);
        Assert.Equal(IdempotencyClaimState.Claimed, stillClaimed.State);

        await store.MarkFailedAsync(acquired.Id, "owner-1");

        IdempotencyClaim? failed = await store.TryGetAsync("claimed-retirement");
        Assert.NotNull(failed);
        Assert.Equal(IdempotencyClaimState.Failed, failed.State);
        Assert.False(failed.TerminalStreamComplete);
    }

    private static void RequireSqlCipher() =>
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

    private static async Task<IdempotencyClaim> AcquireAsync(
        IdempotencyClaimStore store,
        string claimKey,
        string fingerprint,
        string owner,
        DateTimeOffset leaseExpiresAt,
        DateTimeOffset createdAt)
    {
        IdempotencyClaimAcquireResult result = await store.TryAcquireAsync(
            new IdempotencyClaimAcquireRequest(
                claimKey,
                fingerprint,
                owner,
                leaseExpiresAt,
                createdAt));

        Assert.False(result.Conflict);
        Assert.True(result.Acquired);
        Assert.Equal(claimKey, result.Claim.ClaimKeyHash);
        Assert.Equal(fingerprint, result.Claim.FingerprintHash);
        Assert.Equal(owner, result.Claim.OwnerId);
        Assert.Equal(IdempotencyClaimState.Running, result.Claim.State);

        return result.Claim;
    }

    private Task RebuildClaimsWithRaceCollationAsync()
    {
        return ExecuteNonQueryAsync(
            """
            ALTER TABLE "IdempotencyClaims"
                RENAME TO "IdempotencyClaimsWithoutRaceCollation";

            CREATE TABLE "IdempotencyClaims" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_IdempotencyClaims" PRIMARY KEY,
                "ClaimKeyHash" TEXT COLLATE IDEMPOTENCY_RECLAIM_RACE NOT NULL,
                "FingerprintHash" TEXT NOT NULL,
                "State" INTEGER NOT NULL,
                "OwnerId" TEXT NOT NULL,
                "LeaseExpiresAt" TEXT NOT NULL,
                "HeartbeatAt" TEXT NOT NULL,
                "RunId" TEXT NULL,
                "StatusCode" INTEGER NULL,
                "ContentType" TEXT NULL,
                "ResponseBody" TEXT NULL,
                "TerminalStreamComplete" INTEGER NOT NULL DEFAULT 0,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );

            INSERT INTO "IdempotencyClaims"
                ("Id", "ClaimKeyHash", "FingerprintHash", "State", "OwnerId",
                 "LeaseExpiresAt", "HeartbeatAt", "RunId", "StatusCode", "ContentType",
                 "ResponseBody", "TerminalStreamComplete", "CreatedAt", "UpdatedAt")
            SELECT
                "Id", "ClaimKeyHash", "FingerprintHash", "State", "OwnerId",
                "LeaseExpiresAt", "HeartbeatAt", "RunId", "StatusCode", "ContentType",
                "ResponseBody", "TerminalStreamComplete", "CreatedAt", "UpdatedAt"
            FROM "IdempotencyClaimsWithoutRaceCollation";

            DROP TABLE "IdempotencyClaimsWithoutRaceCollation";

            CREATE UNIQUE INDEX "IX_IdempotencyClaims_ClaimKeyHash"
                ON "IdempotencyClaims" ("ClaimKeyHash");
            CREATE INDEX "IX_IdempotencyClaims_State_LeaseExpiresAt"
                ON "IdempotencyClaims" ("State", "LeaseExpiresAt");
            """);
    }

    private Task CreateInsertRaceTriggerAsync(string claimKey, string winnerFingerprint)
    {
        return ExecuteNonQueryAsync(
            $"""
            CREATE TRIGGER "SimulateClaimInsertRace"
            BEFORE INSERT ON "IdempotencyClaims"
            WHEN NEW."ClaimKeyHash" = {SqlLiteral(claimKey)}
                 AND NEW."OwnerId" <> 'race-winner'
            BEGIN
                INSERT INTO "IdempotencyClaims"
                    ("Id", "ClaimKeyHash", "FingerprintHash", "State", "OwnerId",
                     "LeaseExpiresAt", "HeartbeatAt", "RunId", "StatusCode", "ContentType",
                     "ResponseBody", "TerminalStreamComplete", "CreatedAt", "UpdatedAt")
                VALUES
                    (lower(hex(randomblob(16))), NEW."ClaimKeyHash", {SqlLiteral(winnerFingerprint)},
                     {(int)IdempotencyClaimState.Running}, 'race-winner',
                     NEW."LeaseExpiresAt", NEW."HeartbeatAt", NULL, NULL, NULL,
                     NULL, 0, NEW."CreatedAt", NEW."UpdatedAt");
                SELECT RAISE(FAIL, 'simulated unique race');
            END;
            """);
    }

    private async Task ExecuteNonQueryAsync(
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using DbCommand command = _db!.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;

        foreach ((string name, object value) in parameters)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        _ = await command.ExecuteNonQueryAsync();
    }

    private static string SqlLiteral(string value) =>
        $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
}
