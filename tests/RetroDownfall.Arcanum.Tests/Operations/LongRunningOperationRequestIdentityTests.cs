using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Operations;

/// <summary>
/// Issue #88 — the caller-requested durable identity a prepare/apply operation binds.
/// </summary>
/// <remarks>
/// The defect this prevents is a destructive operation started twice by one operator. An apply
/// request can be retried by a dropped connection, a CLI retry, or a restarted process, and without a
/// durable identity each retry starts a fresh operation: two family reinitializes, or a reinitialize
/// racing the operation it was meant to resume. The identity makes replay a lookup rather than a
/// promise, and it lives in the database rather than in an in-memory 202 response, because the
/// failure mode that matters most is the one where the process that issued the 202 is gone.
///
/// <para>The same identity with a different apply-request digest is a conflict rather than a second
/// operation: the caller changed what it was asking for while reusing the name it asked under.</para>
/// </remarks>
[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class LongRunningOperationRequestIdentityTests : IAsyncLifetime
{

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    public LongRunningOperationRequestIdentityTests(GrimoireFixture fixture) => _fixture = fixture;

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
    public async Task A_first_request_creates_the_operation_and_records_its_requested_identity()
    {

        RequireSqlCipher();

        LongRunningOperationStore store = new(_db!);

        LongRunningOperationRequestIdentity identity = Identity(Guid.NewGuid(), 1, 2);

        LongRunningOperationRequestIdentityResult result =
            await store.ResolveOrCreateAsync(Request(), identity);

        Assert.Equal(LongRunningOperationRequestIdentityOutcome.Created, result.Outcome);

        Assert.NotNull(result.Operation);

        Assert.Equal(LongRunningOperationKinds.DataRetentionPrune, result.Operation!.Kind);

    }

    [SkippableFact]
    public async Task The_same_identity_and_digest_resolves_to_the_operation_it_already_created()
    {

        RequireSqlCipher();

        LongRunningOperationStore store = new(_db!);

        LongRunningOperationRequestIdentity identity = Identity(Guid.NewGuid(), 1, 2);

        LongRunningOperationRequestIdentityResult first =
            await store.ResolveOrCreateAsync(Request(), identity);

        LongRunningOperationRequestIdentityResult replay =
            await store.ResolveOrCreateAsync(Request(), identity);

        Assert.Equal(LongRunningOperationRequestIdentityOutcome.Replayed, replay.Outcome);

        Assert.Equal(first.Operation!.Id, replay.Operation!.Id);

    }

    [SkippableFact]
    public async Task The_same_identity_with_a_different_apply_digest_conflicts_instead_of_starting_a_second_operation()
    {

        RequireSqlCipher();

        LongRunningOperationStore store = new(_db!);

        Guid requested = Guid.NewGuid();

        LongRunningOperationRequestIdentityResult first =
            await store.ResolveOrCreateAsync(Request(), Identity(requested, 1, 2));

        LongRunningOperationRequestIdentityResult conflict =
            await store.ResolveOrCreateAsync(Request(), Identity(requested, 9, 2));

        Assert.Equal(LongRunningOperationRequestIdentityOutcome.DigestConflict, conflict.Outcome);

        Assert.Null(conflict.Operation);

        // The conflict must not have created anything: exactly one operation still exists.
        IReadOnlyList<LongRunningOperation> operations = await store.ListAsync(
            new LongRunningOperationQuery(LongRunningOperationKinds.DataRetentionPrune));

        Assert.Single(operations);

        Assert.Equal(first.Operation!.Id, operations[0].Id);

    }

    [SkippableFact]
    public async Task Two_different_identities_are_two_operations()
    {

        RequireSqlCipher();

        LongRunningOperationStore store = new(_db!);

        _ = await store.ResolveOrCreateAsync(Request(), Identity(Guid.NewGuid(), 1, 2));

        _ = await store.ResolveOrCreateAsync(Request(), Identity(Guid.NewGuid(), 1, 2));

        IReadOnlyList<LongRunningOperation> operations = await store.ListAsync(
            new LongRunningOperationQuery(LongRunningOperationKinds.DataRetentionPrune));

        Assert.Equal(2, operations.Count);

    }

    [SkippableFact]
    public async Task An_uninitialized_identity_is_refused_rather_than_stored_as_a_blank_name()
    {

        RequireSqlCipher();

        LongRunningOperationStore store = new(_db!);

        _ = await Assert.ThrowsAsync<ArgumentException>(
            () => store.ResolveOrCreateAsync(
                Request(),
                new LongRunningOperationRequestIdentity(
                    Guid.Empty,
                    Digest(1),
                    Digest(2))));

    }

    private static LongRunningOperationCreateRequest Request() =>
        new(
            LongRunningOperationKinds.DataRetentionPrune,
            LongRunningOperationRecoveryPolicy.RestartIdempotently,
            "retention prune",
            DateTimeOffset.UnixEpoch);

    private static LongRunningOperationRequestIdentity Identity(Guid requested, byte apply, byte effect) =>
        new(requested, Digest(apply), Digest(effect));

    private static CovenantDigest Digest(byte fill)
    {

        byte[] bytes = new byte[32];

        Array.Fill(bytes, fill);

        return new CovenantDigest(bytes);

    }

    private static void RequireSqlCipher() =>
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, "SQLCipher native assets are unavailable on this host.");

}
