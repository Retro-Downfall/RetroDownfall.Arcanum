using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Tests.Data;

/// <summary>
/// The two-phase key protocol every write chokepoint follows.
/// </summary>
/// <remarks>
/// <para>Before its transaction a write asks whether its store holds any evidence, and only then
/// resolves the key. Inside the transaction it asks again, because a fingerprint can commit in
/// between, and a write that finds evidence with no key in hand goes back out for the key rather than
/// reading the keychain under its lock.</para>
///
/// <para>The key is created by one keyring and read by a second, fresh one, so the guard's latch
/// starts <see cref="MemoryErasureKeyState.Unresolved"/> as a new process's does. No case reaches the
/// real keychain.</para>
/// </remarks>
public sealed class MemoryErasureGuardTests : IClassFixture<GrimoireFixture>, IAsyncLifetime
{
    private const string Content = "The operator prefers dark mode.";

    private static readonly MemoryErasureIdentity Identity =
        MemoryErasureIdentity.ForSaga(SagaMemoryScopeKind.Global, null, Content);

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    static MemoryErasureGuardTests() => SqliteNativeRuntime.Instance.Initialize();

    public MemoryErasureGuardTests(GrimoireFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Token => CancellationToken.None;

    private SqliteConnection Connection => (SqliteConnection)_db!.Database.GetDbConnection();

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
            await _db.DisposeAsync();
        }

        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [SkippableFact]
    public async Task No_evidence_means_allowed_with_no_keychain_io()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        CountingOsCredentialStore credentials = new(new InMemoryOsCredentialStore());

        using MemoryErasureKeyring keys = MemoryErasureTestKeys.Isolated(credentials);

        using MemoryErasureGuardContext context = await PrepareAsync(Connection, MemoryReviewStore.Saga, keys);

        await using SqliteTransaction transaction = Connection.BeginTransaction(deferred: false);

        Assert.Equal(
            MemoryErasureGuardVerdict.Allowed,
            await MemoryErasureGuard.CheckAsync(Connection, transaction, context, Identity, Token));

        Assert.False(context.EvidencePresent);

        Assert.Equal(0, credentials.Calls);
    }

    /// <summary>
    /// A key an earlier call already resolved rides along from an empty probe, so the first fingerprint
    /// committing between the phases is checked at once, with no retry and no keychain read.
    /// </summary>
    [SkippableFact]
    public async Task A_latched_key_is_carried_from_an_empty_probe_so_a_first_fingerprint_needs_no_retry()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore inner = new();

        using MemoryErasureKey key = MemoryErasureTestKeys.CreateKey(inner);

        CountingOsCredentialStore credentials = new(inner);

        using MemoryErasureKeyring keys = MemoryErasureTestKeys.Isolated(credentials);

        // An earlier call in this process, an erase prepare say, already resolved the key.
        keys.OpenExisting(MemoryErasureKeyProbe.UseLatched).Key?.Dispose();

        Assert.Equal(MemoryErasureKeyState.Present, keys.Latch.State);

        int readsBefore = credentials.Calls;

        using MemoryErasureGuardContext context = await PrepareAsync(Connection, MemoryReviewStore.Saga, keys);

        Assert.False(context.EvidencePresent);

        Assert.NotNull(context.Key);

        await MemoryErasureTestKeys.SeedFingerprintAsync(Connection, key, Identity, Token);

        await using SqliteTransaction transaction = Connection.BeginTransaction(deferred: false);

        Assert.Equal(
            MemoryErasureGuardVerdict.Withheld,
            await MemoryErasureGuard.CheckAsync(Connection, transaction, context, Identity, Token));

        Assert.Equal(readsBefore, credentials.Calls);
    }

    /// <summary>
    /// Phase one may read the credential store, which never happens inside a SQLite transaction, so
    /// it refuses to run inside one: a pending transaction object or a raw <c>BEGIN</c> alike.
    /// </summary>
    [SkippableTheory]
    [InlineData("sqlite-transaction")]
    [InlineData("raw-begin-immediate")]
    public async Task Phase_one_refuses_to_run_inside_a_transaction(string transactionKind)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore inner = new();

        using (MemoryErasureKey created = MemoryErasureTestKeys.CreateKey(inner))
        {
            await MemoryErasureTestKeys.SeedFingerprintAsync(Connection, created, Identity, Token);
        }

        CountingOsCredentialStore credentials = new(inner);

        using MemoryErasureKeyring keys = MemoryErasureTestKeys.Isolated(credentials);

        SqliteTransaction? transaction = null;

        if (transactionKind is "sqlite-transaction")
        {
            transaction = Connection.BeginTransaction(deferred: false);
        }
        else
        {
            Assert.Equal("raw-begin-immediate", transactionKind);

            await ExecuteAsync(Connection, "BEGIN IMMEDIATE;");
        }

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MemoryErasureGuard.PrepareAsync(Connection, MemoryReviewStore.Saga, keys, Token));

        Assert.Contains("credential store", refused.Message, StringComparison.Ordinal);

        // Refused before the evidence probe could send it to the credential store.
        Assert.Equal(0, credentials.Calls);

        if (transaction is not null)
        {
            await transaction.RollbackAsync(Token);

            await transaction.DisposeAsync();
        }
        else
        {
            await ExecuteAsync(Connection, "ROLLBACK;");
        }

        // Outside a transaction the same preparation runs, and resolves the key once.
        using MemoryErasureGuardContext context = await PrepareAsync(Connection, MemoryReviewStore.Saga, keys);

        Assert.True(context.EvidencePresent);

        Assert.Equal(1, credentials.Calls);
    }

    [SkippableFact]
    public async Task Evidence_that_appears_after_the_probe_asks_for_one_retry_with_the_key()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore credentials = new();

        using MemoryErasureKey key = MemoryErasureTestKeys.CreateKey(credentials);

        using MemoryErasureKeyring keys = MemoryErasureTestKeys.Isolated(credentials);

        MemoryErasureGuardVerdict first;

        using (MemoryErasureGuardContext context = await PrepareAsync(Connection, MemoryReviewStore.Saga, keys))
        {
            Assert.False(context.EvidencePresent);

            Assert.Null(context.Key);

            // The first fingerprint commits after the probe and before the write's transaction opens.
            await MemoryErasureTestKeys.SeedFingerprintAsync(Connection, key, Identity, Token);

            await using SqliteTransaction transaction = Connection.BeginTransaction(deferred: false);

            first = await MemoryErasureGuard.CheckAsync(Connection, transaction, context, Identity, Token);

            await transaction.RollbackAsync(Token);
        }

        Assert.Equal(MemoryErasureGuardVerdict.RetryWithKey, first);

        using MemoryErasureGuardContext again = await PrepareAsync(Connection, MemoryReviewStore.Saga, keys);

        Assert.True(again.EvidencePresent);

        Assert.NotNull(again.Key);

        await using SqliteTransaction retry = Connection.BeginTransaction(deferred: false);

        Assert.Equal(
            MemoryErasureGuardVerdict.Withheld,
            await MemoryErasureGuard.CheckAsync(Connection, retry, again, Identity, Token));
    }

    /// <summary>
    /// Two test homes share one keychain account: this process latched K2, and the other home, still
    /// holding K1, records a fingerprint under it.
    /// </summary>
    [SkippableFact]
    public async Task Rows_written_under_an_overwritten_key_fail_closed_as_key_lost()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore credentials = new();

        using MemoryErasureKey overwritten = MemoryErasureTestKeys.CreateKey(credentials);

        _ = credentials.Delete(ArcanumCredentialIdentity.Service, ArcanumCredentialIdentity.MemoryErasureFingerprintKeyAccount);

        using MemoryErasureKey current = MemoryErasureTestKeys.CreateKey(credentials);

        Assert.False(current.HasKeyId(overwritten.KeyId));

        using MemoryErasureKeyring keys = MemoryErasureTestKeys.Isolated(credentials);

        // This home's own evidence, so the context below is prepared with the key in hand.
        await MemoryErasureTestKeys.SeedFingerprintAsync(Connection, current, Saga("Other content."), Token);

        using MemoryErasureGuardContext context = await PrepareAsync(Connection, MemoryReviewStore.Saga, keys);

        Assert.NotNull(context.Key);

        await MemoryErasureTestKeys.SeedFingerprintAsync(Connection, overwritten, Identity, Token);

        await using (SqliteTransaction transaction = Connection.BeginTransaction(deferred: false))
        {
            Assert.Equal(
                MemoryErasureGuardVerdict.KeyLost,
                await MemoryErasureGuard.CheckAsync(Connection, transaction, context, Identity, Token));
        }

        Result<MemoryErasureGuardContext> prepared =
            await MemoryErasureGuard.PrepareAsync(Connection, MemoryReviewStore.Saga, keys, Token);

        Assert.True(prepared.IsFailure);

        Assert.Equal(ErrorCodes.MemoryErasure.KeyLost, prepared.Error.Code);
    }

    [SkippableTheory]
    [InlineData("deleted", ErrorCodes.MemoryErasure.KeyLost)]
    [InlineData("unavailable", ErrorCodes.MemoryErasure.KeyUnavailable)]
    [InlineData("malformed", ErrorCodes.MemoryErasure.KeyUnavailable)]
    public async Task Evidence_without_a_key_is_key_lost_and_an_unreadable_key_is_unavailable(string key, string code)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore inner = new();

        using (MemoryErasureKey created = MemoryErasureTestKeys.CreateKey(inner))
        {
            await MemoryErasureTestKeys.SeedFingerprintAsync(Connection, created, Identity, Token);
        }

        CountingOsCredentialStore credentials = new(inner);

        switch (key)
        {
            case "deleted":
                _ = inner.Delete(ArcanumCredentialIdentity.Service, ArcanumCredentialIdentity.MemoryErasureFingerprintKeyAccount);

                break;

            case "unavailable":
                credentials.FailWith = OsCredentialStoreStatus.Unavailable;

                break;

            case "malformed":
                _ = inner.Set(ArcanumCredentialIdentity.Service, ArcanumCredentialIdentity.MemoryErasureFingerprintKeyAccount, "not-a-key");

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown key case.");
        }

        using MemoryErasureKeyring keys = MemoryErasureTestKeys.Isolated(credentials);

        Result<MemoryErasureGuardContext> prepared =
            await MemoryErasureGuard.PrepareAsync(Connection, MemoryReviewStore.Saga, keys, Token);

        Assert.True(prepared.IsFailure);

        Assert.Equal(code, prepared.Error.Code);

        // A write that cannot be guarded is refused before it runs.
        int writes = 0;

        MemoryErasureGuardException refused = await Assert.ThrowsAsync<MemoryErasureGuardException>(() =>
            MemoryErasureGuard.RunWithRetryAsync(
                Connection,
                MemoryReviewStore.Saga,
                keys,
                _ =>
                {
                    writes++;

                    return Task.FromResult(0);
                },
                Token));

        Assert.Equal(code, refused.Error.Code);

        Assert.Equal(0, writes);
    }

    [Theory]
    [InlineData(MemoryErasureKeyState.Absent, ErrorCodes.MemoryErasure.KeyLost)]
    [InlineData(MemoryErasureKeyState.Unresolved, ErrorCodes.MemoryErasure.KeyUnavailable)]
    [InlineData(MemoryErasureKeyState.Unavailable, ErrorCodes.MemoryErasure.KeyUnavailable)]
    [InlineData(MemoryErasureKeyState.Malformed, ErrorCodes.MemoryErasure.KeyUnavailable)]
    public void Every_key_state_short_of_present_maps_to_one_refusal(MemoryErasureKeyState state, string code) =>
        Assert.Equal(code, MemoryErasureGuard.RefusalFor(state).Code);

    [Fact]
    public void A_present_key_has_no_refusal() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => MemoryErasureGuard.RefusalFor(MemoryErasureKeyState.Present));

    [SkippableFact]
    public async Task A_matching_fingerprint_is_withheld_and_nothing_else_is()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore credentials = new();

        using MemoryErasureKey key = MemoryErasureTestKeys.CreateKey(credentials);

        using MemoryErasureKeyring keys = MemoryErasureTestKeys.Isolated(credentials);

        await MemoryErasureTestKeys.SeedFingerprintAsync(Connection, key, Identity, Token);

        using MemoryErasureGuardContext saga = await PrepareAsync(Connection, MemoryReviewStore.Saga, keys);

        using MemoryErasureGuardContext lexicon = await PrepareAsync(Connection, MemoryReviewStore.Lexicon, keys);

        await using SqliteTransaction transaction = Connection.BeginTransaction(deferred: false);

        Assert.Equal(
            MemoryErasureGuardVerdict.Withheld,
            await MemoryErasureGuard.CheckAsync(Connection, transaction, saga, Identity, Token));

        Assert.Equal(
            MemoryErasureGuardVerdict.Allowed,
            await MemoryErasureGuard.CheckAsync(Connection, transaction, saga, Saga("The operator prefers light mode."), Token));

        Assert.Equal(
            MemoryErasureGuardVerdict.Allowed,
            await MemoryErasureGuard.CheckAsync(
                Connection,
                transaction,
                saga,
                MemoryErasureIdentity.ForSaga(
                    SagaMemoryScopeKind.Campaign,
                    Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7"),
                    Content),
                Token));

        Assert.Equal(
            MemoryErasureGuardVerdict.Allowed,
            await MemoryErasureGuard.CheckAsync(
                Connection,
                transaction,
                lexicon,
                MemoryErasureIdentity.ForLexicon(null, Content),
                Token));

        // A guard answers only for the store it was prepared for.
        await Assert.ThrowsAsync<ArgumentException>(() => MemoryErasureGuard.CheckAsync(
            Connection,
            transaction,
            saga,
            MemoryErasureIdentity.ForLexicon(null, Content),
            Token));
    }

    [SkippableFact]
    public async Task An_identity_is_computed_only_when_evidence_exists()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore credentials = new();

        using MemoryErasureKey key = MemoryErasureTestKeys.CreateKey(credentials);

        using MemoryErasureKeyring keys = MemoryErasureTestKeys.Isolated(credentials);

        using (MemoryErasureGuardContext empty = await PrepareAsync(Connection, MemoryReviewStore.Saga, keys))
        {
            await using SqliteTransaction transaction = Connection.BeginTransaction(deferred: false);

            Assert.Equal(
                MemoryErasureGuardVerdict.Allowed,
                await MemoryErasureGuard.CheckAsync(Connection, transaction, empty, () => throw new FormatException(), Token));
        }

        await MemoryErasureTestKeys.SeedFingerprintAsync(Connection, key, Identity, Token);

        using MemoryErasureGuardContext context = await PrepareAsync(Connection, MemoryReviewStore.Saga, keys);

        Assert.NotNull(context.Key);

        await using SqliteTransaction guarded = Connection.BeginTransaction(deferred: false);

        await Assert.ThrowsAsync<FormatException>(() =>
            MemoryErasureGuard.CheckAsync(Connection, guarded, context, () => throw new FormatException(), Token));
    }

    [SkippableFact]
    public async Task RunWithRetryAsync_prepares_again_when_the_write_meets_newer_evidence()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore credentials = new();

        using MemoryErasureKey key = MemoryErasureTestKeys.CreateKey(credentials);

        using MemoryErasureKeyring keys = MemoryErasureTestKeys.Isolated(credentials);

        int invocations = 0;

        MemoryErasureGuardVerdict result = await MemoryErasureGuard.RunWithRetryAsync(
            Connection,
            MemoryReviewStore.Saga,
            keys,
            async context =>
            {
                invocations++;

                if (invocations == 1)
                {
                    await MemoryErasureTestKeys.SeedFingerprintAsync(Connection, key, Identity, Token);
                }

                await using SqliteTransaction transaction = Connection.BeginTransaction(deferred: false);

                MemoryErasureGuardVerdict verdict =
                    await MemoryErasureGuard.CheckAsync(Connection, transaction, context, Identity, Token);

                await transaction.RollbackAsync(Token);

                return verdict is MemoryErasureGuardVerdict.RetryWithKey
                    ? throw new MemoryErasureRetryException()
                    : verdict;
            },
            Token);

        Assert.Equal(2, invocations);

        Assert.Equal(MemoryErasureGuardVerdict.Withheld, result);
    }

    [SkippableFact]
    public async Task RunWithRetryAsync_refuses_a_second_retry_as_unavailable()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using MemoryErasureKeyring keys = MemoryErasureTestKeys.Isolated();

        int invocations = 0;

        MemoryErasureGuardException refused = await Assert.ThrowsAsync<MemoryErasureGuardException>(() =>
            MemoryErasureGuard.RunWithRetryAsync<int>(
                Connection,
                MemoryReviewStore.Saga,
                keys,
                _ =>
                {
                    invocations++;

                    throw new MemoryErasureRetryException();
                },
                Token));

        Assert.Equal(ErrorCodes.MemoryErasure.Unavailable, refused.Error.Code);

        Assert.Equal(2, invocations);
    }

    /// <summary>
    /// Evidence can only be committed at Core version 13, so a catalog recorded below it has none,
    /// whatever its tables hold, and nothing reads the key for it.
    /// </summary>
    [SkippableTheory]
    [InlineData("fixture-v12")]
    [InlineData("v13-recorded-as-12")]
    public async Task A_catalog_below_version_thirteen_has_no_evidence_and_touches_no_key(string catalog)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore inner = new();

        using MemoryErasureKey key = MemoryErasureTestKeys.CreateKey(inner);

        byte[] fingerprint = key.Fingerprint(Identity);

        using EvolutionScratchDatabase? scratch = catalog is "fixture-v12" ? EvolutionScratchDatabase.Create() : null;

        SqliteConnection? owned = null;

        try
        {
            SqliteConnection connection;

            if (scratch is not null)
            {
                owned = await scratch.OpenAsync(Token);

                GrimoireSchemaInstallResult installed = await GrimoireSchemaTestInstaller.InstallAsync(
                    owned,
                    CoreSchemaVersionTwelveFixture.ChainSet(),
                    1536,
                    Token);

                Assert.Equal(12, installed.Core.SchemaVersion);

                Assert.Equal("fixture-v12", catalog);

                connection = owned;
            }
            else
            {
                Assert.Equal("v13-recorded-as-12", catalog);

                connection = Connection;

                await MemoryErasureTestKeys.SeedFingerprintAsync(connection, key, Identity, Token);

                await using SqliteCommand downgrade = connection.CreateCommand();

                downgrade.CommandText =
                    "UPDATE grimoire_feature_schemas SET SchemaVersion = 12 WHERE FamilyCode = 0 AND TransactionTierCode = 0;";

                _ = await downgrade.ExecuteNonQueryAsync(Token);
            }

            CountingOsCredentialStore credentials = new(inner);

            using MemoryErasureKeyring keys = MemoryErasureTestKeys.Isolated(credentials);

            Assert.False(await MemoryErasureEvidence.IsInstalledAsync(connection, null, Token));

            foreach (MemoryReviewStore store in (MemoryReviewStore[])
                [MemoryReviewStore.Covenant, MemoryReviewStore.Saga, MemoryReviewStore.Lexicon])
            {
                Assert.False(await MemoryErasureEvidence.AnyAsync(connection, null, store, Token));
            }

            Assert.False(await MemoryErasureEvidence.ContainsAsync(connection, null, fingerprint, Token));

            foreach (byte[]? keyId in (byte[]?[])[null, key.KeyId.ToArray()])
            {
                MemoryErasureEvidenceCounts counts = await MemoryErasureEvidence.CountAsync(connection, null, keyId, Token);

                Assert.Equal<MemoryErasureStoreCountsDto>(
                    [
                        new(MemoryReviewStore.Covenant, 0, 0, 0),
                        new(MemoryReviewStore.Saga, 0, 0, 0),
                        new(MemoryReviewStore.Lexicon, 0, 0, 0),
                    ],
                    counts.Stores);

                Assert.Equal(0L, counts.UnverifiableReceipts);

                Assert.Equal(0L, counts.PendingScrubReceipts);
            }

            using (MemoryErasureGuardContext context = await PrepareAsync(connection, MemoryReviewStore.Saga, keys))
            {
                await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

                Assert.Equal(
                    MemoryErasureGuardVerdict.Allowed,
                    await MemoryErasureGuard.CheckAsync(connection, transaction, context, Identity, Token));
            }

            await MemoryErasureKeyWarmup.RunAsync(connection, keys, Token);

            Assert.Equal(MemoryErasureKeyState.Unresolved, keys.Latch.State);

            Assert.Equal(0, credentials.Calls);

            await Assert.ThrowsAsync<InvalidOperationException>(() => MemoryErasureEvidence.InsertFingerprintAsync(
                connection,
                null,
                fingerprint,
                MemoryReviewStore.Saga,
                key.KeyId.ToArray(),
                Token));
        }
        finally
        {
            if (owned is not null)
            {
                await owned.DisposeAsync();
            }
        }
    }

    private static MemoryErasureIdentity Saga(string content) =>
        MemoryErasureIdentity.ForSaga(SagaMemoryScopeKind.Global, null, content);

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync(Token);
    }

    private static async Task<MemoryErasureGuardContext> PrepareAsync(
        SqliteConnection connection,
        MemoryReviewStore store,
        IMemoryErasureKeyProvider keys)
    {
        Result<MemoryErasureGuardContext> prepared = await MemoryErasureGuard.PrepareAsync(connection, store, keys, Token);

        Assert.True(prepared.IsSuccess, prepared.Error.Message);

        return prepared.Value;
    }
}
