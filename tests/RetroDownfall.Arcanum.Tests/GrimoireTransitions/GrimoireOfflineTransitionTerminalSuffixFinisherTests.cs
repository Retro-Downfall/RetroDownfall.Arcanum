using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.GrimoireTransitions;

public sealed class GrimoireOfflineTransitionTerminalSuffixFinisherTests
{
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

    private static async Task<string> IdentityAsync(SqliteConnection connection)
    {
        await using SqliteCommand read = connection.CreateCommand();

        read.CommandText = "SELECT InstallationIdentity FROM covenant_authority_state WHERE StateKey = 1;";

        return (string)(await read.ExecuteScalarAsync())!;
    }

    private static CovenantDigest Digest(byte value) =>
        new([.. Enumerable.Repeat(value, 32)]);

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
