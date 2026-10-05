using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.GrimoireTransitions;

public sealed class GrimoireOfflineTransitionTerminalSuffixFinisherTests
{
    private static readonly Guid SnapshotOperationId =
        Guid.Parse("6b111111-2222-4333-8444-555555555555");

    [Theory]
    [InlineData("missing")]
    [InlineData("claimed")]
    [InlineData("foreign-winner")]
    [InlineData("wrong-binding")]
    public async Task An_already_satisfied_bound_parent_requires_the_exact_completed_winner(
        string mutation)
    {
        CovenantDigest binding = Digest(0x31);

        CovenantDigest winner = Digest(0x51);

        RecordingParentReceipt? parent = mutation is "missing"
            ? null
            : new RecordingParentReceipt(
                binding,
                mutation switch
                {
                    "claimed" => Result<CovenantDigest>.Failure(new Error(
                        ErrorCodes.Covenant.ManualRecoveryRequired,
                        "The parent receipt remains claimed.")),
                    "foreign-winner" => Result<CovenantDigest>.Failure(new Error(
                        ErrorCodes.Covenant.ManualRecoveryRequired,
                        "The parent receipt names another winner.")),
                    _ => Result<CovenantDigest>.Success(Digest(0x71)),
                });

        Result result = await GrimoireOfflineTransitionTerminalSuffixFinisher
            .VerifyRecordedParentAsync(
                binding,
                GrimoireOfflineTransitionReconciliationStep.ParentReceiptSatisfied,
                parent,
                winner,
                CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(0, parent?.PublishCalls ?? 0);

        Assert.Equal(parent is null ? 0 : 1, parent?.VerifyCalls ?? 0);
    }

    [Fact]
    public async Task An_already_satisfied_bound_parent_is_verified_without_republication()
    {
        CovenantDigest binding = Digest(0x31);

        RecordingParentReceipt parent = new(
            binding,
            Result<CovenantDigest>.Success(binding));

        Result result = await GrimoireOfflineTransitionTerminalSuffixFinisher
            .VerifyRecordedParentAsync(
                binding,
                GrimoireOfflineTransitionReconciliationStep.CovenantDispositionVerified,
                parent,
                Digest(0x51),
                CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(1, parent.VerifyCalls);

        Assert.Equal(0, parent.PublishCalls);
    }

    /// <summary>
    /// The terminal arm's operation-row read on a catalog another connection holds exclusively meets
    /// SQLITE_BUSY: that is an outage the start can retry, not a row that failed to verify.
    /// </summary>
    [Fact]
    public async Task A_busy_catalog_during_the_operation_row_read_is_an_outage_not_manual_recovery()
    {
        await using ExclusivelyLockedCatalog locked = await ExclusivelyLockedCatalog.CreateAsync(
            Guid.NewGuid(),
            CancellationToken.None);

        Result<GrimoireOfflineTransitionTerminalSuffixFinisher.TerminalOperationSnapshot> row =
            await GrimoireOfflineTransitionTerminalSuffixFinisher.TerminalOperationSnapshot.ReadAsync(
                locked.RecoveryConnection,
                Guid.NewGuid(),
                CancellationToken.None);

        Assert.True(row.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.Unavailable, row.Error.Code);
    }

    /// <summary>
    /// The terminal arm's installation-identity read: a busy catalog is the outage, while an identity
    /// that names another installation is durable disagreement and stays the manual-recovery refusal.
    /// </summary>
    [Fact]
    public async Task A_busy_identity_read_is_an_outage_and_a_foreign_identity_is_a_refusal()
    {
        Guid installationId = Guid.NewGuid();

        await using (ExclusivelyLockedCatalog locked = await ExclusivelyLockedCatalog.CreateAsync(
                         installationId,
                         CancellationToken.None))
        {
            Result busy = await GrimoireOfflineTransitionTerminalSuffixFinisher
                .VerifyInstallationIdentityAsync(
                    locked.RecoveryConnection,
                    installationId,
                    CancellationToken.None);

            Assert.True(busy.IsFailure);

            Assert.Equal(ErrorCodes.Covenant.Unavailable, busy.Error.Code);
        }

        SqliteNativeRuntime.Instance.Initialize();

        string directory = Directory.CreateTempSubdirectory("arcanum-finisher-identity-").FullName;

        try
        {
            await using SqliteConnection connection = new(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(directory, "identity.db"),
                Pooling = false,
            }.ToString());

            await connection.OpenAsync();

            await using (SqliteCommand create = connection.CreateCommand())
            {
                create.CommandText = """
                    CREATE TABLE covenant_authority_state(
                        StateKey INTEGER PRIMARY KEY,
                        InstallationIdentity TEXT NOT NULL);
                    INSERT INTO covenant_authority_state(StateKey, InstallationIdentity)
                    VALUES (1, $identity);
                    """;

                create.Parameters.AddWithValue(
                    "$identity",
                    Guid.NewGuid().ToString("D").ToUpperInvariant());

                await create.ExecuteNonQueryAsync();
            }

            Result foreign = await GrimoireOfflineTransitionTerminalSuffixFinisher
                .VerifyInstallationIdentityAsync(connection, installationId, CancellationToken.None);

            Assert.True(foreign.IsFailure);

            Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, foreign.Error.Code);

            Assert.True((await GrimoireOfflineTransitionTerminalSuffixFinisher
                .VerifyInstallationIdentityAsync(
                    connection,
                    Guid.Parse(await IdentityAsync(connection)),
                    CancellationToken.None)).IsSuccess);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// The terminal arm's canonical-state read is the same read the recovery-authority load makes on
    /// the same recovery connection, so a busy catalog there is the same retryable outage rather than
    /// an exception that escapes the pass.
    /// </summary>
    [Fact]
    public async Task A_busy_catalog_during_the_canonical_state_read_is_an_outage_not_manual_recovery()
    {
        await using ExclusivelyLockedCatalog locked = await ExclusivelyLockedCatalog.CreateAsync(
            Guid.NewGuid(),
            CancellationToken.None);

        Result<CovenantOfflineTransitionSourceState> observed =
            await GrimoireOfflineTransitionTerminalSuffixFinisher.ReadCanonicalStateAsync(
                locked.RecoveryConnection,
                CancellationToken.None);

        Assert.True(observed.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.Unavailable, observed.Error.Code);
    }

    /// <summary>
    /// A catalog the canonical-state read cannot use for any reason other than an outage is the
    /// manual-recovery refusal, whether the read throws or finds no canonical row.
    /// </summary>
    [Fact]
    public async Task A_canonical_state_read_that_fails_without_an_outage_is_the_refusal()
    {
        await using ScratchCatalog missing = await ScratchCatalog.CreateAsync(schema: null);

        Result<CovenantOfflineTransitionSourceState> noTable =
            await GrimoireOfflineTransitionTerminalSuffixFinisher.ReadCanonicalStateAsync(
                missing.Connection,
                CancellationToken.None);

        Assert.True(noTable.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, noTable.Error.Code);

        await using ScratchCatalog empty = await ScratchCatalog.CreateAsync(
            """
            CREATE TABLE covenant_state(
                StateKey INTEGER PRIMARY KEY,
                DatasetGeneration BLOB,
                AcceleratorEpoch INTEGER,
                KeyReclamationEpoch INTEGER,
                EnvelopeKeyEpoch INTEGER);
            """);

        Result<CovenantOfflineTransitionSourceState> noRow =
            await GrimoireOfflineTransitionTerminalSuffixFinisher.ReadCanonicalStateAsync(
                empty.Connection,
                CancellationToken.None);

        Assert.True(noRow.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, noRow.Error.Code);
    }

    /// <summary>
    /// The operation row read again just before retirement: a busy catalog is the outage a later
    /// start resumes from, because the journal edges already written are monotonic and the next start
    /// reaches this same reread from the retirement-pending revision.
    /// </summary>
    [Fact]
    public async Task A_busy_catalog_during_the_operation_row_reread_is_an_outage_not_manual_recovery()
    {
        await using ExclusivelyLockedCatalog locked = await ExclusivelyLockedCatalog.CreateAsync(
            Guid.NewGuid(),
            CancellationToken.None);

        Result unchanged = await GrimoireOfflineTransitionTerminalSuffixFinisher
            .RereadUnchangedAsync(
                locked.RecoveryConnection,
                Snapshot(revision: 4),
                CancellationToken.None);

        Assert.True(unchanged.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.Unavailable, unchanged.Error.Code);
    }

    /// <summary>
    /// The reread admits only the exact row the pass verified: the same row is success, and a row
    /// that moved, or a catalog with no row at all, is the refusal.
    /// </summary>
    [Fact]
    public async Task The_operation_row_reread_admits_only_the_exact_verified_row()
    {
        GrimoireOfflineTransitionTerminalSuffixFinisher.TerminalOperationSnapshot verified =
            Snapshot(revision: 4);

        await using ScratchCatalog catalog = await ScratchCatalog.CreateAsync(
            """
            CREATE TABLE LongRunningOperations(
                Id, Kind, State, RecoveryPolicy, RootOperationId, ParentOperationId, SessionId, RunId,
                InferenceRunId, BudgetReservationId, IdempotencyClaimId, CreatedAt, StartedAt,
                HeartbeatAt, CompletedAt, LeaseOwner, LeaseExpiresAt, AttemptCount, CheckpointVersion,
                CheckpointPayload, CheckpointReference, PublicSummary, TerminalErrorCode, Revision);
            """);

        await InsertAsync(catalog.Connection, verified.Operation);

        Assert.True((await GrimoireOfflineTransitionTerminalSuffixFinisher.RereadUnchangedAsync(
            catalog.Connection,
            verified,
            CancellationToken.None)).IsSuccess);

        Result moved = await GrimoireOfflineTransitionTerminalSuffixFinisher.RereadUnchangedAsync(
            catalog.Connection,
            Snapshot(revision: 5),
            CancellationToken.None);

        Assert.True(moved.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, moved.Error.Code);

        await using ScratchCatalog missing = await ScratchCatalog.CreateAsync(schema: null);

        Result gone = await GrimoireOfflineTransitionTerminalSuffixFinisher.RereadUnchangedAsync(
            missing.Connection,
            verified,
            CancellationToken.None);

        Assert.True(gone.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, gone.Error.Code);
    }

    private static GrimoireOfflineTransitionTerminalSuffixFinisher.TerminalOperationSnapshot Snapshot(
        long revision)
    {
        DateTimeOffset created = UtcInstantText.Parse("2026-10-05T10:00:00.0000000Z");

        return new(new LongRunningOperation(
            SnapshotOperationId,
            "covenant.reset",
            LongRunningOperationState.Completed,
            LongRunningOperationRecoveryPolicy.ResumeFromCheckpoint,
            RootOperationId: null,
            ParentOperationId: null,
            SessionId: null,
            RunId: null,
            InferenceRunId: null,
            BudgetReservationId: null,
            IdempotencyClaimId: null,
            created,
            created.AddSeconds(1),
            created.AddSeconds(2),
            created.AddSeconds(3),
            LeaseOwner: null,
            LeaseExpiresAt: null,
            AttemptCount: 1,
            CheckpointVersion: 1,
            CheckpointPayload: [0x01, 0x02, 0x03],
            CheckpointReference: "checkpoint",
            PublicSummary: "summary",
            TerminalErrorCode: null,
            revision));
    }

    private static async Task InsertAsync(SqliteConnection connection, LongRunningOperation operation)
    {
        await using SqliteCommand insert = connection.CreateCommand();

        insert.CommandText = """
            INSERT INTO LongRunningOperations VALUES (
                $id, $kind, $state, $policy, NULL, NULL, NULL, NULL, NULL, NULL, NULL, $created,
                $started, $heartbeat, $completed, NULL, NULL, $attempts, $version, $payload,
                $reference, $summary, NULL, $revision);
            """;

        insert.Parameters.AddWithValue("$id", operation.Id.ToString("N"));

        insert.Parameters.AddWithValue("$kind", operation.Kind);

        insert.Parameters.AddWithValue("$state", (long)operation.State);

        insert.Parameters.AddWithValue("$policy", (long)operation.RecoveryPolicy);

        insert.Parameters.AddWithValue("$created", UtcInstantText.Format(operation.CreatedAt));

        insert.Parameters.AddWithValue("$started", UtcInstantText.Format(operation.StartedAt!.Value));

        insert.Parameters.AddWithValue("$heartbeat", UtcInstantText.Format(operation.HeartbeatAt!.Value));

        insert.Parameters.AddWithValue("$completed", UtcInstantText.Format(operation.CompletedAt!.Value));

        insert.Parameters.AddWithValue("$attempts", (long)operation.AttemptCount);

        insert.Parameters.AddWithValue("$version", (long)operation.CheckpointVersion);

        insert.Parameters.AddWithValue("$payload", operation.CheckpointPayload!);

        insert.Parameters.AddWithValue("$reference", operation.CheckpointReference!);

        insert.Parameters.AddWithValue("$summary", operation.PublicSummary);

        insert.Parameters.AddWithValue("$revision", operation.Revision);

        await insert.ExecuteNonQueryAsync();
    }

    private static async Task<string> IdentityAsync(SqliteConnection connection)
    {
        await using SqliteCommand read = connection.CreateCommand();

        read.CommandText = "SELECT InstallationIdentity FROM covenant_authority_state WHERE StateKey = 1;";

        return (string)(await read.ExecuteScalarAsync())!;
    }

    private static CovenantDigest Digest(byte value) =>
        new([.. Enumerable.Repeat(value, 32)]);

    /// <summary>A plain unpooled SQLite file the test owns, with an optional schema.</summary>
    private sealed class ScratchCatalog : IAsyncDisposable
    {
        private readonly string _directory;

        private ScratchCatalog(string directory, SqliteConnection connection)
        {
            _directory = directory;

            Connection = connection;
        }

        internal SqliteConnection Connection { get; }

        internal static async Task<ScratchCatalog> CreateAsync(string? schema)
        {
            SqliteNativeRuntime.Instance.Initialize();

            string directory = Directory.CreateTempSubdirectory("arcanum-finisher-catalog-").FullName;

            SqliteConnection connection = new(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(directory, "catalog.db"),
                Pooling = false,
            }.ToString());

            await connection.OpenAsync();

            if (schema is not null)
            {
                await using SqliteCommand create = connection.CreateCommand();

                create.CommandText = schema;

                await create.ExecuteNonQueryAsync();
            }

            return new ScratchCatalog(directory, connection);
        }

        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync();

            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class RecordingParentReceipt(
        CovenantDigest binding,
        Result<CovenantDigest> verification) : IGrimoireOfflineTransitionParentReceiptSink
    {
        internal int PublishCalls { get; private set; }

        internal int VerifyCalls { get; private set; }

        public CovenantDigest BindingDigest => binding;

        public Task<Result<CovenantDigest>> PublishAndRereadAsync(
            CovenantDigest terminalWinnerDigest,
            CancellationToken cancellationToken)
        {
            PublishCalls++;

            return Task.FromResult(Result<CovenantDigest>.Success(binding));
        }

        public Task<Result<CovenantDigest>> VerifyCompletedAsync(
            CovenantDigest terminalWinnerDigest,
            CancellationToken cancellationToken)
        {
            VerifyCalls++;

            return Task.FromResult(verification);
        }
    }
}
