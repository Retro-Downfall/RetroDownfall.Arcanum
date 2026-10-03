using Microsoft.Data.Sqlite;

using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data;

/// <summary>
/// Issue #117 — the check a legacy raw delete makes before it removes a row that might be labelled.
/// </summary>
/// <remarks>
/// The six routes already dispatch through the purge boundary, so in normal operation this guard never
/// fires. It exists for the caller that does not: a repository method is reachable from anywhere in the
/// process, and "every caller remembers to ask the purger first" is a convention rather than a property.
///
/// <para>The two arms answer different questions on purpose. A single delete can name the artifact it is
/// about; a set-based <c>DELETE FROM</c> examines no identity at all, so the only honest question there
/// is whether the kind has any protected member left (§10.20.2). The bulk arm exists only inside a
/// transaction, because a bulk delete always owns one.</para>
/// </remarks>
[Collection("Grimoire")]

[Trait("Category", "Integration")]

public sealed class CovenantLabeledArtifactGuardTests : IAsyncLifetime
{

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    public CovenantLabeledArtifactGuardTests(GrimoireFixture fixture) => _fixture = fixture;

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

    public async Task An_unlabeled_artifact_passes_the_guard_untouched()
    {

        RequireSqlCipher();

        ICovenantLabeledArtifactTransactionGuard guard = CreateGuard();

        Result unlabeled = await guard.EnsureUnlabeledAsync(
            SensitiveArtifactKind.Saga,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.True(unlabeled.IsSuccess);

        await using SqliteTransaction transaction = await BeginAsync();

        Result none = await guard.EnsureNoneLabeledAsync(
            SensitiveArtifactKind.Saga,
            transaction,
            CancellationToken.None);

        Assert.True(none.IsSuccess);

    }

    [SkippableTheory]

    [InlineData(SensitiveArtifactKind.Saga)]

    [InlineData(SensitiveArtifactKind.Lexicon)]

    [InlineData(SensitiveArtifactKind.AssistantEntry)]

    public async Task A_labeled_artifact_is_refused_outside_the_purge_boundary(SensitiveArtifactKind kind)
    {

        RequireSqlCipher();

        Guid artifactId = Guid.NewGuid();

        await SeedLabelAsync(kind, artifactId, CancellationToken.None);

        ICovenantLabeledArtifactTransactionGuard guard = CreateGuard();

        Result refused = await guard.EnsureUnlabeledAsync(
            kind,
            artifactId,
            CancellationToken.None);

        Assert.True(refused.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, refused.Error.Code);

        // The refusal names the boundary rather than the artifact: the message reaches operator surfaces
        // and has no business carrying an identity from the label table.
        Assert.Contains("purge boundary", refused.Error.Message, StringComparison.Ordinal);

        Assert.DoesNotContain(artifactId.ToString("D"), refused.Error.Message, StringComparison.OrdinalIgnoreCase);

    }

    /// <summary>
    /// One labelled member is enough to refuse a whole-kind delete.
    /// </summary>
    /// <remarks>
    /// This is the arm that closes the bulk hole. `DELETE FROM saga_memories` would remove every row
    /// including labelled ones, and no per-artifact check can see rows it never enumerated.
    /// </remarks>
    [SkippableFact]

    public async Task One_labeled_member_refuses_the_whole_kind_bulk_delete()
    {

        RequireSqlCipher();

        ICovenantLabeledArtifactTransactionGuard guard = CreateGuard();

        await using SqliteTransaction transaction = await BeginAsync();

        Assert.True(
            (await guard.EnsureNoneLabeledAsync(SensitiveArtifactKind.Saga, transaction, CancellationToken.None))
                .IsSuccess);

        await SeedLabelAsync(SensitiveArtifactKind.Saga, Guid.NewGuid(), CancellationToken.None, transaction);

        Result refused = await guard.EnsureNoneLabeledAsync(
            SensitiveArtifactKind.Saga,
            transaction,
            CancellationToken.None);

        Assert.True(refused.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, refused.Error.Code);

        // A different kind is unaffected: the bulk arm is per kind, not per installation, so labelling a
        // Saga fact must not block a Lexicon reset that has nothing protected in it.
        Assert.True(
            (await guard.EnsureNoneLabeledAsync(SensitiveArtifactKind.Lexicon, transaction, CancellationToken.None))
                .IsSuccess);

    }

    /// <summary>
    /// A label table that cannot be read refuses the delete, on every arm, rather than passing it.
    /// </summary>
    /// <remarks>
    /// The label table is a Core object at every schema version, so "it could not be read" is never
    /// "nothing is protected here": it is a Grimoire whose protection cannot be checked. The failure is a
    /// real one - a temporary table of the same name shadows the label table on this connection, so the
    /// read fails on a column it does not have - and the refusal names no artifact.
    /// </remarks>
    [SkippableTheory]

    [InlineData("single")]

    [InlineData("single-in-transaction")]

    [InlineData("bulk-in-transaction")]

    public async Task An_unreadable_label_table_refuses_the_delete_instead_of_passing_it(string arm)
    {

        RequireSqlCipher();

        ICovenantLabeledArtifactTransactionGuard guard = CreateGuard();

        Guid artifactId = Guid.NewGuid();

        await UnreadableLabelTableAsync();

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        await using SqliteTransaction? transaction = arm == "single"
            ? null
            : connection.BeginTransaction(deferred: false);

        Result refused = arm switch
        {

            "single" => await guard.EnsureUnlabeledAsync(SensitiveArtifactKind.Saga, artifactId, CancellationToken.None),

            "single-in-transaction" => await guard.EnsureUnlabeledAsync(SensitiveArtifactKind.Saga, artifactId, transaction!, CancellationToken.None),

            _ => await guard.EnsureNoneLabeledAsync(SensitiveArtifactKind.Saga, transaction!, CancellationToken.None),

        };

        Assert.True(refused.IsFailure);

        // Unavailable, not ForbiddenAuthority: nothing was found to be protected, so there is no
        // authority to lack. The protection could not be checked, which is a 503 the operator can act on
        // by repairing the Grimoire, where ForbiddenAuthority says a labelled artifact must leave through
        // the purge boundary.
        Assert.Equal(ErrorCodes.Covenant.Unavailable, refused.Error.Code);

        Assert.DoesNotContain(artifactId.ToString("D"), refused.Error.Message, StringComparison.OrdinalIgnoreCase);

    }

    /// <summary>
    /// A label the caller's own transaction has written and not committed is seen by the check made in
    /// it, which is what makes the check and the delete one moment.
    /// </summary>
    /// <remarks>
    /// Another connection could not see an uncommitted label at all, and a check that reads on its own
    /// connection or outside the transaction would answer "unlabelled" here.
    /// </remarks>
    [SkippableFact]

    public async Task A_label_written_in_the_callers_transaction_is_seen_by_the_check_made_in_it()
    {

        RequireSqlCipher();

        ICovenantLabeledArtifactTransactionGuard guard = CreateGuard();

        Guid artifactId = Guid.NewGuid();

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        await using SqliteTransaction transaction = await BeginAsync();

        Assert.True(
            (await guard.EnsureUnlabeledAsync(SensitiveArtifactKind.Saga, artifactId, transaction, CancellationToken.None)).IsSuccess);

        Assert.True(
            (await guard.EnsureNoneLabeledAsync(SensitiveArtifactKind.Saga, transaction, CancellationToken.None)).IsSuccess);

        await SeedLabelAsync(SensitiveArtifactKind.Saga, artifactId, CancellationToken.None, transaction);

        Result single = await guard.EnsureUnlabeledAsync(SensitiveArtifactKind.Saga, artifactId, transaction, CancellationToken.None);

        Assert.True(single.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, single.Error.Code);

        Assert.True(
            (await guard.EnsureNoneLabeledAsync(SensitiveArtifactKind.Saga, transaction, CancellationToken.None)).IsFailure);

        // The bulk arm is per kind, inside a transaction as outside one.
        Assert.True(
            (await guard.EnsureNoneLabeledAsync(SensitiveArtifactKind.Lexicon, transaction, CancellationToken.None)).IsSuccess);

        await transaction.RollbackAsync();

        // The rolled-back label is gone for a transaction that begins afterwards.
        await using SqliteTransaction after = connection.BeginTransaction(deferred: false);

        Assert.True(
            (await guard.EnsureNoneLabeledAsync(SensitiveArtifactKind.Saga, after, CancellationToken.None)).IsSuccess);

    }

    private ICovenantLabeledArtifactTransactionGuard CreateGuard()
    {

        ICovenantConnectionSource connections = new CovenantConnectionSource(
            _db!,
            new RecordingScopedOrdinaryConnectionFactory());

        return new CovenantLabeledArtifactGuard(
            new ArtifactSensitivityLedger(connections),
            NullLogger<CovenantLabeledArtifactGuard>.Instance);

    }

    /// <summary>Opens the connection if it is not open and begins a write transaction on it.</summary>
    private async Task<SqliteTransaction> BeginAsync()
    {

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        if (connection.State is not System.Data.ConnectionState.Open)
        {

            await connection.OpenAsync(CancellationToken.None);

        }

        return connection.BeginTransaction(deferred: false);

    }

    /// <summary>
    /// Makes the label table unreadable on this connection by shadowing it with a temporary table of the
    /// same name that has none of its columns.
    /// </summary>
    private async Task UnreadableLabelTableAsync()
    {

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        if (connection.State is not System.Data.ConnectionState.Open)
        {

            await connection.OpenAsync(CancellationToken.None);

        }

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "CREATE TEMP TABLE artifact_sensitivity (Unreadable INTEGER);";

        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);

    }

    private async Task SeedLabelAsync(
        SensitiveArtifactKind kind,
        Guid artifactId,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        if (connection.State is not System.Data.ConnectionState.Open)
        {

            await connection.OpenAsync(cancellationToken);

        }

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            INSERT INTO artifact_sensitivity (
                LabelId, ArtifactKindCode, ArtifactId, SensitivityCode, ProvenanceModeCode,
                ExactGenerationIds, GenerationBloom, SessionId, CampaignId, TurnId,
                ArtifactRevision, ArtifactContentDigest, SensitivityDigest, ProducingPlanDigest,
                ProducingAdmissionDigest, ProducingMaintenanceReceiptDigest, ArtifactLabelDigest,
                CreatedAtUtc)
            VALUES ($label, $kind, $artifact, 1, 1, $generations, NULL, NULL, NULL, NULL,
                    1, zeroblob(32), zeroblob(32), NULL, NULL, NULL, zeroblob(32), $now);
            """;

        _ = command.Parameters.AddWithValue("$label", Guid.NewGuid().ToString("D").ToUpperInvariant());

        _ = command.Parameters.AddWithValue("$kind", (int)kind);

        _ = command.Parameters.AddWithValue("$artifact", artifactId.ToString("D").ToUpperInvariant());

        _ = command.Parameters.AddWithValue("$generations", Enumerable.Repeat((byte)7, 16).ToArray());

        _ = command.Parameters.AddWithValue("$now", "2026-01-01T00:00:00.0000000Z");

        _ = await command.ExecuteNonQueryAsync(cancellationToken);

    }

    private static void RequireSqlCipher() =>
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

}
