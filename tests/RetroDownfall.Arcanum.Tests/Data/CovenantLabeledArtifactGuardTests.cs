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

    [SkippableTheory]
    [InlineData("single", "lower")]
    [InlineData("single", "dashless")]
    [InlineData("single", "different")]
    [InlineData("batch", "lower")]
    [InlineData("batch", "dashless")]
    [InlineData("batch", "different")]
    public async Task Transaction_admission_honors_exact_Guid_label_aliases_without_widening_to_other_identities(string arm, string spelling)
    {
        RequireSqlCipher();

        Guid selected = Guid.NewGuid();

        Guid labelled = spelling == "different" ? Guid.NewGuid() : selected;

        string persisted = labelled.ToString(spelling == "dashless" ? "N" : "D").ToLowerInvariant();

        ICovenantLabeledArtifactTransactionGuard guard = CreateGuard();

        await using SqliteTransaction transaction = await BeginAsync();

        await SeedLabelAsync(SensitiveArtifactKind.CampaignRollup, labelled, CancellationToken.None,
            transaction, storedIdentity: persisted);

        Result admitted = arm == "single"
            ? await guard.EnsureUnlabeledAsync(SensitiveArtifactKind.CampaignRollup, selected,
                transaction.Connection!, transaction, CancellationToken.None)
            : await guard.EnsureAllUnlabeledAsync(SensitiveArtifactKind.CampaignRollup, [selected],
                transaction.Connection!, transaction, CancellationToken.None);

        Assert.Equal(spelling == "different", admitted.IsSuccess);

        if (spelling != "different")
        {
            Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, admitted.Error.Code);
        }

        await using SqliteCommand retained = transaction.Connection!.CreateCommand();

        retained.Transaction = transaction;

        retained.CommandText = "SELECT COUNT(*) FROM artifact_sensitivity WHERE ArtifactKindCode = $kind AND ArtifactId = $identity;";

        _ = retained.Parameters.AddWithValue("$kind", (long)SensitiveArtifactKind.CampaignRollup);

        _ = retained.Parameters.AddWithValue("$identity", persisted);

        Assert.Equal(1L, await retained.ExecuteScalarAsync());
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
            transaction.Connection!,
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
            (await guard.EnsureNoneLabeledAsync(SensitiveArtifactKind.Saga, transaction.Connection!, transaction, CancellationToken.None))
                .IsSuccess);

        await SeedLabelAsync(SensitiveArtifactKind.Saga, Guid.NewGuid(), CancellationToken.None, transaction);

        Result refused = await guard.EnsureNoneLabeledAsync(
            SensitiveArtifactKind.Saga,
            transaction.Connection!,
            transaction,
            CancellationToken.None);

        Assert.True(refused.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, refused.Error.Code);

        // A different kind is unaffected: the bulk arm is per kind, not per installation, so labelling a
        // Saga fact must not block a Lexicon reset that has nothing protected in it.
        Assert.True(
            (await guard.EnsureNoneLabeledAsync(SensitiveArtifactKind.Lexicon, transaction.Connection!, transaction, CancellationToken.None))
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

            "single-in-transaction" => await guard.EnsureUnlabeledAsync(SensitiveArtifactKind.Saga, artifactId, transaction!.Connection!, transaction!, CancellationToken.None),

            _ => await guard.EnsureNoneLabeledAsync(SensitiveArtifactKind.Saga, transaction!.Connection!, transaction!, CancellationToken.None),

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
            (await guard.EnsureUnlabeledAsync(SensitiveArtifactKind.Saga, artifactId, transaction.Connection!, transaction, CancellationToken.None)).IsSuccess);

        Assert.True(
            (await guard.EnsureNoneLabeledAsync(SensitiveArtifactKind.Saga, transaction.Connection!, transaction, CancellationToken.None)).IsSuccess);

        await SeedLabelAsync(SensitiveArtifactKind.Saga, artifactId, CancellationToken.None, transaction);

        Result single = await guard.EnsureUnlabeledAsync(SensitiveArtifactKind.Saga, artifactId, transaction.Connection!, transaction, CancellationToken.None);

        Assert.True(single.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, single.Error.Code);

        Assert.True(
            (await guard.EnsureNoneLabeledAsync(SensitiveArtifactKind.Saga, transaction.Connection!, transaction, CancellationToken.None)).IsFailure);

        // The bulk arm is per kind, inside a transaction as outside one.
        Assert.True(
            (await guard.EnsureNoneLabeledAsync(SensitiveArtifactKind.Lexicon, transaction.Connection!, transaction, CancellationToken.None)).IsSuccess);

        await transaction.RollbackAsync();

        // The rolled-back label is gone for a transaction that begins afterwards.
        await using SqliteTransaction after = connection.BeginTransaction(deferred: false);

        Assert.True(
            (await guard.EnsureNoneLabeledAsync(SensitiveArtifactKind.Saga, after.Connection!, after, CancellationToken.None)).IsSuccess);

    }

    /// <summary>
    /// A set of artifacts none of which is labelled passes the batched check, across more than one chunk.
    /// </summary>
    /// <remarks>
    /// Six hundred identities is two full chunks and a remainder, so a check that stopped after the first
    /// chunk, or that bound too many parameters for one statement, shows up here rather than on a Session
    /// of a few Entries.
    /// </remarks>
    [SkippableFact]

    public async Task A_set_with_no_labeled_member_passes_the_batched_check_across_chunks()
    {

        RequireSqlCipher();

        ICovenantLabeledArtifactTransactionGuard guard = CreateGuard();

        Guid[] ids = [.. Enumerable.Range(0, 600).Select(static _ => Guid.NewGuid())];

        await using SqliteTransaction transaction = await BeginAsync();

        Result answer = await guard.EnsureAllUnlabeledAsync(
            SensitiveArtifactKind.AssistantEntry,
            ids,
            transaction.Connection!,
            transaction,
            CancellationToken.None);

        Assert.True(answer.IsSuccess);

    }

    /// <summary>
    /// One labelled member refuses the batched check wherever it sits in the set, including either side of
    /// a chunk boundary.
    /// </summary>
    /// <remarks>
    /// The positions are the first member, the last of the first chunk, the first of the second, and the
    /// last of the set. A check that read only some chunks, or that dropped a member when it split the
    /// set, would pass the one position it never asked about.
    /// </remarks>
    [SkippableTheory]

    [InlineData(0)]

    [InlineData(255)]

    [InlineData(256)]

    [InlineData(599)]

    public async Task A_labeled_member_anywhere_in_the_set_refuses_the_batched_check(int position)
    {

        RequireSqlCipher();

        ICovenantLabeledArtifactTransactionGuard guard = CreateGuard();

        Guid[] ids = [.. Enumerable.Range(0, 600).Select(static _ => Guid.NewGuid())];

        await SeedLabelAsync(SensitiveArtifactKind.AssistantEntry, ids[position], CancellationToken.None);

        await using SqliteTransaction transaction = await BeginAsync();

        Result refused = await guard.EnsureAllUnlabeledAsync(
            SensitiveArtifactKind.AssistantEntry,
            ids,
            transaction.Connection!,
            transaction,
            CancellationToken.None);

        Assert.True(refused.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, refused.Error.Code);

        // The refusal names the boundary and never the artifact, as the per-artifact form's does.
        Assert.Contains("purge boundary", refused.Error.Message, StringComparison.Ordinal);

        Assert.DoesNotContain(ids[position].ToString("D"), refused.Error.Message, StringComparison.OrdinalIgnoreCase);

    }

    /// <summary>
    /// The batched check is per kind and per identity: a label of another kind on one of the identities,
    /// or a label on an identity that is not in the set, does not refuse it.
    /// </summary>
    [SkippableFact]

    public async Task The_batched_check_ignores_another_kind_and_an_identity_outside_the_set()
    {

        RequireSqlCipher();

        ICovenantLabeledArtifactTransactionGuard guard = CreateGuard();

        Guid[] ids = [.. Enumerable.Range(0, 3).Select(static _ => Guid.NewGuid())];

        await SeedLabelAsync(SensitiveArtifactKind.Saga, ids[1], CancellationToken.None);

        await SeedLabelAsync(SensitiveArtifactKind.AssistantEntry, Guid.NewGuid(), CancellationToken.None);

        await using SqliteTransaction transaction = await BeginAsync();

        Result answer = await guard.EnsureAllUnlabeledAsync(
            SensitiveArtifactKind.AssistantEntry,
            ids,
            transaction.Connection!,
            transaction,
            CancellationToken.None);

        Assert.True(answer.IsSuccess);

        Result other = await guard.EnsureAllUnlabeledAsync(
            SensitiveArtifactKind.Saga,
            ids,
            transaction.Connection!,
            transaction,
            CancellationToken.None);

        Assert.True(other.IsFailure);

    }

    /// <summary>
    /// A label the caller's own transaction wrote and has not committed is seen by the batched check made
    /// in it.
    /// </summary>
    [SkippableFact]

    public async Task A_label_written_in_the_callers_transaction_is_seen_by_the_batched_check()
    {

        RequireSqlCipher();

        ICovenantLabeledArtifactTransactionGuard guard = CreateGuard();

        Guid[] ids = [.. Enumerable.Range(0, 5).Select(static _ => Guid.NewGuid())];

        await using SqliteTransaction transaction = await BeginAsync();

        Assert.True(
            (await guard.EnsureAllUnlabeledAsync(SensitiveArtifactKind.AssistantEntry, ids, transaction.Connection!, transaction, CancellationToken.None)).IsSuccess);

        await SeedLabelAsync(SensitiveArtifactKind.AssistantEntry, ids[3], CancellationToken.None, transaction);

        Assert.True(
            (await guard.EnsureAllUnlabeledAsync(SensitiveArtifactKind.AssistantEntry, ids, transaction.Connection!, transaction, CancellationToken.None)).IsFailure);

    }

    /// <summary>
    /// A label table that cannot be read refuses the batched check with <c>Covenant.Unavailable</c>, and an
    /// empty set passes without reading it at all.
    /// </summary>
    /// <remarks>
    /// The same real failure the other arms are driven with: a temporary table of the same name shadows the
    /// label table, so the read fails on a column it does not have. The empty set asks nothing, so it has
    /// nothing to be unable to read.
    /// </remarks>
    [SkippableFact]

    public async Task An_unreadable_label_table_refuses_the_batched_check_and_an_empty_set_passes()
    {

        RequireSqlCipher();

        ICovenantLabeledArtifactTransactionGuard guard = CreateGuard();

        Guid artifactId = Guid.NewGuid();

        await UnreadableLabelTableAsync();

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        Result refused = await guard.EnsureAllUnlabeledAsync(
            SensitiveArtifactKind.AssistantEntry,
            [artifactId],
            transaction.Connection!,
            transaction,
            CancellationToken.None);

        Assert.True(refused.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.Unavailable, refused.Error.Code);

        Assert.DoesNotContain(artifactId.ToString("D"), refused.Error.Message, StringComparison.OrdinalIgnoreCase);

        Result empty = await guard.EnsureAllUnlabeledAsync(
            SensitiveArtifactKind.AssistantEntry,
            [],
            transaction.Connection!,
            transaction,
            CancellationToken.None);

        Assert.True(empty.IsSuccess);

    }

    /// <summary>
    /// A transaction that is not on the connection handed alongside it is a caller's mistake, refused
    /// before anything is read.
    /// </summary>
    /// <remarks>
    /// The two travel together so the read is on the transaction's own connection. Answering from one
    /// connection while the delete runs in a transaction on another would be the check-then-delete shape
    /// the transaction forms exist to remove, with nothing to say so.
    /// </remarks>
    [SkippableFact]

    public async Task A_transaction_that_is_not_on_the_connection_handed_with_it_is_refused_before_anything_is_read()
    {

        RequireSqlCipher();

        ICovenantLabeledArtifactTransactionGuard guard = CreateGuard();

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        await using SqliteConnection other = new("Data Source=:memory:");

        await other.OpenAsync();

        await using SqliteTransaction foreign = other.BeginTransaction();

        _ = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await guard.EnsureUnlabeledAsync(SensitiveArtifactKind.Saga, Guid.NewGuid(), connection, foreign, CancellationToken.None));

        _ = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await guard.EnsureAllUnlabeledAsync(SensitiveArtifactKind.Saga, [Guid.NewGuid()], connection, foreign, CancellationToken.None));

        _ = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await guard.EnsureNoneLabeledAsync(SensitiveArtifactKind.Saga, connection, foreign, CancellationToken.None));

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
        SqliteTransaction? transaction = null,
        string? storedIdentity = null)
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

        _ = command.Parameters.AddWithValue("$artifact", storedIdentity ?? artifactId.ToString("D").ToUpperInvariant());

        _ = command.Parameters.AddWithValue("$generations", Enumerable.Repeat((byte)7, 16).ToArray());

        _ = command.Parameters.AddWithValue("$now", "2026-01-01T00:00:00.0000000Z");

        _ = await command.ExecuteNonQueryAsync(cancellationToken);

    }

    private static void RequireSqlCipher() =>
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

}
