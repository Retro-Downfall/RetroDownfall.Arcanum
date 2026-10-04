using System.Security.Cryptography;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Tests.Data;

/// <summary>
/// The erasure evidence store: fingerprints, receipts and their subjects, read and written only
/// through <see cref="MemoryErasureEvidence"/>.
/// </summary>
/// <remarks>
/// Every case runs on a copy of the head catalog through the context's own connection, the way the
/// chokepoints and erase services reach it. The receipt table's own checks and its update guard stay
/// in force here, so a write this store makes that the schema would refuse fails the case.
/// </remarks>
public sealed class MemoryErasureEvidenceTests : IClassFixture<GrimoireFixture>, IAsyncLifetime
{
    private static readonly MemoryErasureIdentity Identity =
        MemoryErasureIdentity.ForSaga(SagaMemoryScopeKind.Global, null, "The operator prefers dark mode.");

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    public MemoryErasureEvidenceTests(GrimoireFixture fixture)
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

    /// <summary>
    /// A probe repeated on a catalog nothing has changed runs one statement, not the table check and the
    /// Core version read again; and a write between probes is seen.
    /// </summary>
    /// <remarks>
    /// Every guard probe used to ask both questions each time, which is two statements before the one
    /// that answers. The positive answer is reused only while the catalog's change stamp is unchanged,
    /// so a Core version recorded below 13 on this same connection after a positive probe still answers
    /// "no evidence".
    /// </remarks>
    [SkippableFact]
    public async Task A_repeated_probe_on_an_unchanged_catalog_runs_one_statement()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await Connection.OpenAsync(Token);

        Assert.True(await MemoryErasureEvidence.IsInstalledAsync(Connection, null, Token));

        Assert.Equal(1, await CountStatementsAsync(
            async () => Assert.True(await MemoryErasureEvidence.IsInstalledAsync(Connection, null, Token))));

        await ExecuteAsync(
            Connection,
            "UPDATE grimoire_feature_schemas SET SchemaVersion = 12 WHERE FamilyCode = 0 AND TransactionTierCode = 0;");

        Assert.False(await MemoryErasureEvidence.IsInstalledAsync(Connection, null, Token));
    }

    /// <summary>
    /// The top-level statements a probe runs. SQLite also traces the nested statement behind a
    /// table-valued pragma, as a <c>--</c> comment line, and those are not separate round trips.
    /// </summary>
    private async Task<int> CountStatementsAsync(Func<Task> probe)
    {
        int statements = 0;

        SQLitePCL.raw.sqlite3_trace(
            Connection.Handle,
            (SQLitePCL.strdelegate_trace)((_, statement) =>
            {
                if (!statement.StartsWith("--", StringComparison.Ordinal))
                {
                    statements++;
                }
            }),
            null);

        try
        {
            await probe();
        }
        finally
        {
            SQLitePCL.raw.sqlite3_trace(Connection.Handle, (SQLitePCL.delegate_trace?)null, null);
        }

        return statements;
    }

    [SkippableFact]
    public async Task Fingerprint_rows_round_trip_by_primary_key_and_store()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using MemoryErasureKey key = MemoryErasureTestKeys.CreateKey(new InMemoryOsCredentialStore());

        byte[] fingerprint = key.Fingerprint(Identity);

        byte[] own = key.KeyId.ToArray();

        byte[] other = [.. Enumerable.Repeat((byte)0x5A, 16)];

        Assert.True(await MemoryErasureEvidence.IsInstalledAsync(Connection, null, Token));

        Assert.True(await MemoryErasureEvidence.InsertFingerprintAsync(Connection, null, fingerprint, MemoryReviewStore.Saga, own, Token));

        // The fingerprint is the primary key, so a second record of the same erasure is a no-op.
        Assert.False(await MemoryErasureEvidence.InsertFingerprintAsync(Connection, null, fingerprint, MemoryReviewStore.Saga, own, Token));

        Assert.True(await MemoryErasureEvidence.AnyAsync(Connection, null, MemoryReviewStore.Saga, Token));

        Assert.False(await MemoryErasureEvidence.AnyAsync(Connection, null, MemoryReviewStore.Lexicon, Token));

        Assert.True(await MemoryErasureEvidence.ContainsAsync(Connection, null, fingerprint, Token));

        Assert.False(await MemoryErasureEvidence.AnyForeignAsync(Connection, null, MemoryReviewStore.Saga, own, Token));

        Assert.True(await MemoryErasureEvidence.AnyForeignAsync(Connection, null, MemoryReviewStore.Saga, other, Token));

        Assert.Equal(1, await MemoryErasureEvidence.DeleteFingerprintAsync(Connection, null, fingerprint, Token));

        Assert.Equal(0, await MemoryErasureEvidence.DeleteFingerprintAsync(Connection, null, fingerprint, Token));

        Assert.False(await MemoryErasureEvidence.ContainsAsync(Connection, null, fingerprint, Token));

        Assert.False(await MemoryErasureEvidence.AnyAsync(Connection, null, MemoryReviewStore.Saga, Token));
    }

    [SkippableFact]
    public async Task A_receipt_and_its_subjects_round_trip_and_answer_by_subject_digest()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using MemoryErasureKey key = MemoryErasureTestKeys.CreateKey(new InMemoryOsCredentialStore());

        Guid mutationId = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

        byte[] requestDigest = [.. Enumerable.Range(0, 32).Select(static value => (byte)value)];

        byte[] effectDigest = [.. Enumerable.Range(0, 32).Select(static value => (byte)(0xF0 ^ value))];

        MemoryErasureReceiptRow row = new(
            mutationId,
            MemoryReviewStore.Lexicon,
            key.KeyId.ToArray(),
            requestDigest,
            effectDigest,
            ErasedItemCount: 1,
            RemovedRowCount: 7,
            RemovedLabelCount: 2,
            RemovedRetirementSuppressionCount: 1,
            Authorship: MemoryExternalEvidence.Known,
            Context: MemoryExternalEvidence.ReceiptWindow,
            Embedding: MemoryExternalEvidence.NotRecorded,
            Backup: MemoryExternalEvidence.NotApplicable,
            OtherExternal: MemoryExternalEvidence.Known,
            RetainedCopiesMask: 0b1011,
            ScrubStateCode: 1,
            ScrubPendingReasonMask: 3);

        byte[][] subjects =
        [
            key.Subject(MemoryReviewStore.Lexicon, "6F9619FF-8B86-D011-B42D-00C04FC964FF"),
            key.Subject(MemoryReviewStore.Lexicon, "7C9E6679-7425-40DE-944B-E07FC1F90AE7"),
        ];

        await using (SqliteTransaction transaction = Connection.BeginTransaction(deferred: false))
        {
            await MemoryErasureEvidence.InsertReceiptAsync(Connection, transaction, row, subjects, Token);

            await transaction.CommitAsync(Token);
        }

        MemoryErasureReceiptRow? read = await MemoryErasureEvidence.ReadReceiptAsync(Connection, null, mutationId, Token);

        Assert.NotNull(read);

        Assert.Equal(mutationId, read.MutationId);

        Assert.Equal(MemoryReviewStore.Lexicon, read.Store);

        Assert.Equal<byte>(key.KeyId.ToArray(), read.KeyId);

        Assert.Equal<byte>(requestDigest, read.RequestDigest);

        Assert.Equal<byte>(effectDigest, read.EffectDigest);

        Assert.Equal(1, read.ErasedItemCount);

        Assert.Equal(7L, read.RemovedRowCount);

        Assert.Equal(2, read.RemovedLabelCount);

        Assert.Equal(1, read.RemovedRetirementSuppressionCount);

        Assert.Equal(MemoryExternalEvidence.Known, read.Authorship);

        Assert.Equal(MemoryExternalEvidence.ReceiptWindow, read.Context);

        Assert.Equal(MemoryExternalEvidence.NotRecorded, read.Embedding);

        Assert.Equal(MemoryExternalEvidence.NotApplicable, read.Backup);

        Assert.Equal(MemoryExternalEvidence.Known, read.OtherExternal);

        Assert.Equal(0b1011, read.RetainedCopiesMask);

        Assert.Equal(1, read.ScrubStateCode);

        Assert.Equal(3, read.ScrubPendingReasonMask);

        Assert.Equal(
            "0F8FAD5B-D9CB-469F-A165-70867728950E",
            await ScalarAsync("SELECT MutationId FROM memory_erasure_receipts;"));

        foreach (byte[] subject in subjects)
        {
            Assert.True(await MemoryErasureEvidence.SubjectErasedAsync(Connection, null, subject, Token));
        }

        Assert.False(await MemoryErasureEvidence.SubjectErasedAsync(
            Connection,
            null,
            key.Subject(MemoryReviewStore.Lexicon, "A0EEBC99-9C0B-4EF8-BB6D-6BB9BD380A11"),
            Token));

        Assert.Null(await MemoryErasureEvidence.ReadReceiptAsync(Connection, null, Guid.NewGuid(), Token));
    }

    [SkippableFact]
    public async Task Clearing_the_wal_reason_verifies_only_receipts_that_had_no_other_reason()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using MemoryErasureKey key = MemoryErasureTestKeys.CreateKey(new InMemoryOsCredentialStore());

        Guid walOnly = Guid.NewGuid();

        Guid walAndFullText = Guid.NewGuid();

        await InsertPendingReceiptAsync(key, walOnly, MemoryReviewStore.Saga, key.KeyId.ToArray(), mask: 1);

        await InsertPendingReceiptAsync(key, walAndFullText, MemoryReviewStore.Saga, key.KeyId.ToArray(), mask: 1 | 2);

        Assert.Equal(0L, await MemoryErasureEvidence.ClearWalPendingAsync(Connection, null, Guid.NewGuid(), Token));

        Assert.Equal(1L, await MemoryErasureEvidence.ClearWalPendingAsync(Connection, null, null, Token));

        Assert.Equal((2, 0), await ScrubAsync(walOnly));

        // Full-text residue is not upgradable, so this receipt stays pending for that reason alone.
        Assert.Equal((1, 2), await ScrubAsync(walAndFullText));

        long writesBefore = await TotalChangesAsync();

        Assert.Equal(0L, await MemoryErasureEvidence.ClearWalPendingAsync(Connection, null, null, Token));

        // No receipt without the WAL reason is rewritten, so a scrub retry writes nothing it need not.
        Assert.Equal(writesBefore, await TotalChangesAsync());
    }

    [SkippableFact]
    public async Task Counts_and_unverifiable_deletion_split_rows_by_key_id()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using MemoryErasureKey current = MemoryErasureTestKeys.CreateKey(new InMemoryOsCredentialStore());

        using MemoryErasureKey previous = MemoryErasureTestKeys.CreateKey(new InMemoryOsCredentialStore());

        byte[] currentKeyId = current.KeyId.ToArray();

        await MemoryErasureTestKeys.SeedFingerprintAsync(Connection, current, Saga("first"), Token);

        await MemoryErasureTestKeys.SeedFingerprintAsync(Connection, current, Saga("second"), Token);

        await MemoryErasureTestKeys.SeedFingerprintAsync(Connection, previous, Saga("third"), Token);

        await MemoryErasureTestKeys.SeedFingerprintAsync(
            Connection,
            previous,
            MemoryErasureIdentity.ForLexicon(null, "Entity"),
            Token);

        Guid sagaReceipt = Guid.NewGuid();

        Guid lexiconReceipt = Guid.NewGuid();

        byte[] sagaSubject = await InsertPendingReceiptAsync(current, sagaReceipt, MemoryReviewStore.Saga, currentKeyId, mask: 1);

        byte[] lexiconSubject = await InsertPendingReceiptAsync(
            previous,
            lexiconReceipt,
            MemoryReviewStore.Lexicon,
            previous.KeyId.ToArray(),
            mask: 1);

        MemoryErasureEvidenceCounts counts = await MemoryErasureEvidence.CountAsync(Connection, null, currentKeyId, Token);

        Assert.Equal<MemoryErasureStoreCountsDto>(
            [
                new(MemoryReviewStore.Covenant, 0, 0, 0),
                new(MemoryReviewStore.Saga, 3, 1, 1),
                new(MemoryReviewStore.Lexicon, 1, 1, 1),
            ],
            counts.Stores);

        Assert.Equal(1L, counts.UnverifiableReceipts);

        // The unverifiable receipts are split by store too, in the order of the store counts.
        Assert.Equal<long>([0, 0, 1], counts.UnverifiableStoreReceipts);

        Assert.Equal(2L, counts.PendingScrubReceipts);

        // With no key, nothing can be verified.
        MemoryErasureEvidenceCounts keyless = await MemoryErasureEvidence.CountAsync(Connection, null, null, Token);

        Assert.Equal(3L, keyless.Stores[1].Unverifiable);

        Assert.Equal(1L, keyless.Stores[2].Unverifiable);

        Assert.Equal(2L, keyless.UnverifiableReceipts);

        Assert.Equal<long>([0, 1, 1], keyless.UnverifiableStoreReceipts);

        Assert.Equal((2L, 1L), await MemoryErasureEvidence.DeleteUnverifiableAsync(Connection, null, currentKeyId, Token));

        // The discarded receipt's subjects went with it, and the kept receipt's stayed.
        Assert.False(await MemoryErasureEvidence.SubjectErasedAsync(Connection, null, lexiconSubject, Token));

        Assert.Equal(0L, await ScalarAsync(
            $"SELECT count(*) FROM memory_erasure_receipt_subjects WHERE MutationId = '{lexiconReceipt.ToString("D").ToUpperInvariant()}';"));

        Assert.True(await MemoryErasureEvidence.SubjectErasedAsync(Connection, null, sagaSubject, Token));

        MemoryErasureEvidenceCounts after = await MemoryErasureEvidence.CountAsync(Connection, null, currentKeyId, Token);

        Assert.Equal(new MemoryErasureStoreCountsDto(MemoryReviewStore.Saga, 2, 0, 1), after.Stores[1]);

        Assert.Equal(new MemoryErasureStoreCountsDto(MemoryReviewStore.Lexicon, 0, 0, 0), after.Stores[2]);

        Assert.Equal(0L, after.UnverifiableReceipts);

        Assert.Equal<long>([0, 0, 0], after.UnverifiableStoreReceipts);
    }

    /// <summary>
    /// The scrub's snapshot: every receipt still pending on the write-ahead log, by mutation id, and
    /// nothing once the reason is cleared or on a catalog that cannot hold evidence.
    /// </summary>
    [SkippableFact]
    public async Task Wal_pending_ids_are_read_in_order_and_only_while_the_wal_bit_is_set()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using MemoryErasureKey key = MemoryErasureTestKeys.CreateKey(new InMemoryOsCredentialStore());

        Guid verified = Guid.Parse("0A000000-0000-4000-8000-000000000003");

        Guid first = Guid.Parse("1A000000-0000-4000-8000-000000000001");

        Guid second = Guid.Parse("2A000000-0000-4000-8000-000000000002");

        // Written out of order, so the order read back is the store's and not the insertion's.
        await InsertPendingReceiptAsync(key, second, MemoryReviewStore.Lexicon, key.KeyId.ToArray(), mask: 1 | 2);

        await InsertPendingReceiptAsync(key, verified, MemoryReviewStore.Saga, key.KeyId.ToArray(), mask: 1);

        await InsertPendingReceiptAsync(key, first, MemoryReviewStore.Saga, key.KeyId.ToArray(), mask: 1);

        Assert.Equal(1L, await MemoryErasureEvidence.ClearWalPendingAsync(Connection, null, verified, Token));

        Assert.Equal<Guid>([first, second], await MemoryErasureEvidence.ReadWalPendingAsync(Connection, null, Token));

        await ExecuteAsync(
            Connection,
            "UPDATE grimoire_feature_schemas SET SchemaVersion = 12 WHERE FamilyCode = 0 AND TransactionTierCode = 0;");

        Assert.Empty(await MemoryErasureEvidence.ReadWalPendingAsync(Connection, null, Token));

        await ExecuteAsync(
            Connection,
            "UPDATE grimoire_feature_schemas SET SchemaVersion = 13 WHERE FamilyCode = 0 AND TransactionTierCode = 0;");

        Assert.Equal<Guid>([first, second], await MemoryErasureEvidence.ReadWalPendingAsync(Connection, null, Token));

        Assert.Equal(1L, await MemoryErasureEvidence.ClearWalPendingAsync(Connection, null, null, Token));

        Assert.Empty(await MemoryErasureEvidence.ReadWalPendingAsync(Connection, null, Token));

        // The second receipt is still pending, for a reason the log never clears.
        Assert.Equal((1, 2), await ScrubAsync(second));
    }

    /// <summary>
    /// A receipt and its subjects are one record, so they are written only inside the caller's
    /// transaction: a failure between them would otherwise leave a receipt that cannot answer for
    /// every subject it names.
    /// </summary>
    /// <remarks>
    /// A raw <c>BEGIN IMMEDIATE</c> on the connection is a transaction too. It is how every Lexicon
    /// write runs, and the Lexicon erase passes no transaction object for that reason.
    /// </remarks>
    [SkippableFact]
    public async Task A_receipt_is_written_only_inside_a_transaction()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using MemoryErasureKey key = MemoryErasureTestKeys.CreateKey(new InMemoryOsCredentialStore());

        Guid mutationId = Guid.NewGuid();

        byte[] subject = key.Subject(MemoryReviewStore.Lexicon, mutationId.ToString("D"));

        MemoryErasureReceiptRow row = PendingReceipt(mutationId, MemoryReviewStore.Lexicon, key.KeyId.ToArray(), mask: 1);

        ArgumentNullException refused = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            MemoryErasureEvidence.InsertReceiptAsync(Connection, null, row, [subject], Token));

        Assert.Equal("transaction", refused.ParamName);

        Assert.Null(await MemoryErasureEvidence.ReadReceiptAsync(Connection, null, mutationId, Token));

        Assert.False(await MemoryErasureEvidence.SubjectErasedAsync(Connection, null, subject, Token));

        await ExecuteAsync(Connection, "BEGIN IMMEDIATE;");

        await MemoryErasureEvidence.InsertReceiptAsync(Connection, null, row, [subject], Token);

        await ExecuteAsync(Connection, "COMMIT;");

        Assert.NotNull(await MemoryErasureEvidence.ReadReceiptAsync(Connection, null, mutationId, Token));

        Assert.True(await MemoryErasureEvidence.SubjectErasedAsync(Connection, null, subject, Token));
    }

    /// <summary>
    /// A catalog that cannot hold evidence answers every read empty and every delete with nothing
    /// removed, and refuses both inserts, whatever rows its tables happen to hold.
    /// </summary>
    /// <remarks>
    /// The two head-catalog cases hold real evidence first, so a member that skipped the installed
    /// check would find it: a subject would answer erased, a receipt would read back, and a delete or
    /// a scrub would change rows. The rows are counted directly afterwards to prove none did.
    /// </remarks>
    [SkippableTheory]
    [InlineData("fixture-v12")]
    [InlineData("v13-recorded-as-12")]
    [InlineData("v13-without-fingerprint-table")]
    public async Task A_catalog_that_cannot_hold_evidence_answers_empty_from_every_member(string catalog)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using MemoryErasureKey key = MemoryErasureTestKeys.CreateKey(new InMemoryOsCredentialStore());

        byte[] fingerprint = key.Fingerprint(Identity);

        byte[] foreignKeyId = [.. Enumerable.Repeat((byte)0x5A, 16)];

        Guid mutationId = Guid.Parse("3F2504E0-4F89-41D3-9A0C-0305E82C3301");

        byte[] subject = key.Subject(MemoryReviewStore.Saga, mutationId.ToString("D"));

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

                connection = owned;
            }
            else
            {
                connection = Connection;

                await MemoryErasureTestKeys.SeedFingerprintAsync(connection, key, Identity, Token);

                Assert.Equal(
                    subject,
                    await InsertPendingReceiptAsync(key, mutationId, MemoryReviewStore.Saga, key.KeyId.ToArray(), mask: 1));

                if (catalog is "v13-recorded-as-12")
                {
                    await ExecuteAsync(
                        connection,
                        "UPDATE grimoire_feature_schemas SET SchemaVersion = 12 WHERE FamilyCode = 0 AND TransactionTierCode = 0;");
                }
                else
                {
                    Assert.Equal("v13-without-fingerprint-table", catalog);

                    await ExecuteAsync(connection, "DROP TABLE memory_erasure_fingerprints;");
                }
            }

            Assert.False(await MemoryErasureEvidence.IsInstalledAsync(connection, null, Token));

            foreach (MemoryReviewStore store in (MemoryReviewStore[])
                [MemoryReviewStore.Covenant, MemoryReviewStore.Saga, MemoryReviewStore.Lexicon])
            {
                Assert.False(await MemoryErasureEvidence.AnyAsync(connection, null, store, Token));

                Assert.False(await MemoryErasureEvidence.AnyForeignAsync(connection, null, store, foreignKeyId, Token));
            }

            Assert.False(await MemoryErasureEvidence.ContainsAsync(connection, null, fingerprint, Token));

            Assert.False(await MemoryErasureEvidence.SubjectErasedAsync(connection, null, subject, Token));

            Assert.Null(await MemoryErasureEvidence.ReadReceiptAsync(connection, null, mutationId, Token));

            foreach (byte[]? keyId in (byte[]?[])[null, foreignKeyId])
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

                Assert.Equal<long>([0, 0, 0], counts.UnverifiableStoreReceipts);

                Assert.Equal(0L, counts.PendingScrubReceipts);
            }

            Assert.Empty(await MemoryErasureEvidence.ReadWalPendingAsync(connection, null, Token));

            Assert.Equal(0L, await MemoryErasureEvidence.ClearWalPendingAsync(connection, null, mutationId, Token));

            Assert.Equal(0L, await MemoryErasureEvidence.ClearWalPendingAsync(connection, null, null, Token));

            Assert.Equal(0, await MemoryErasureEvidence.DeleteFingerprintAsync(connection, null, fingerprint, Token));

            Assert.Equal((0L, 0L), await MemoryErasureEvidence.DeleteUnverifiableAsync(connection, null, foreignKeyId, Token));

            await Assert.ThrowsAsync<InvalidOperationException>(() => MemoryErasureEvidence.InsertFingerprintAsync(
                connection,
                null,
                fingerprint,
                MemoryReviewStore.Saga,
                key.KeyId.ToArray(),
                Token));

            await using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => MemoryErasureEvidence.InsertReceiptAsync(
                    connection,
                    transaction,
                    PendingReceipt(Guid.NewGuid(), MemoryReviewStore.Saga, key.KeyId.ToArray(), mask: 1),
                    [key.Subject(MemoryReviewStore.Saga, Guid.NewGuid().ToString("D"))],
                    Token));
            }

            if (scratch is null)
            {
                // Nothing above touched the evidence the catalog already held.
                Assert.Equal(
                    "1|1|1",
                    await ScalarAsync(
                        connection,
                        "SELECT count(*) || '|' || min(ScrubStateCode) || '|' || min(ScrubPendingReasonMask) FROM memory_erasure_receipts;"));

                Assert.Equal(1L, await ScalarAsync(connection, "SELECT count(*) FROM memory_erasure_receipt_subjects;"));

                if (catalog is "v13-recorded-as-12")
                {
                    Assert.Equal(1L, await ScalarAsync(connection, "SELECT count(*) FROM memory_erasure_fingerprints;"));
                }
            }
        }
        finally
        {
            if (owned is not null)
            {
                await owned.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// A catalog with no fingerprint table holds no evidence, and answers so before it reads Core
    /// metadata, which a Covenant-only scratch catalog does not carry either.
    /// </summary>
    [SkippableFact]
    public async Task An_absent_table_answers_no_evidence_without_reading_core_metadata()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using CovenantSchemaScratchDatabase scratch = await CovenantSchemaScratchDatabase.CreateAsync(Token);

        Assert.False(await scratch.ObjectExistsAsync("grimoire_feature_schemas", "table", Token));

        Assert.False(await scratch.ObjectExistsAsync("memory_erasure_fingerprints", "table", Token));

        Assert.False(await MemoryErasureEvidence.IsInstalledAsync(scratch.Connection, null, Token));

        Assert.False(await MemoryErasureEvidence.AnyAsync(scratch.Connection, null, MemoryReviewStore.Covenant, Token));
    }

    /// <summary>
    /// A fingerprint table whose catalog cannot say what Core version it is fails closed: missing
    /// metadata, or a metadata table with no Core row, throws rather than reading as no evidence.
    /// </summary>
    [SkippableFact]
    public async Task A_present_table_without_core_metadata_fails_closed()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using CovenantSchemaScratchDatabase scratch = await CovenantSchemaScratchDatabase.CreateAsync(Token);

        await scratch.InstallCoreObjectsAsync(["memory_erasure_fingerprints"], Token);

        _ = await Assert.ThrowsAsync<SqliteException>(() =>
            MemoryErasureEvidence.IsInstalledAsync(scratch.Connection, null, Token));

        _ = await Assert.ThrowsAsync<SqliteException>(() =>
            MemoryErasureEvidence.AnyAsync(scratch.Connection, null, MemoryReviewStore.Covenant, Token));

        await scratch.InstallCoreObjectsAsync(["grimoire_feature_schemas"], Token);

        _ = await Assert.ThrowsAsync<InvalidDataException>(() =>
            MemoryErasureEvidence.IsInstalledAsync(scratch.Connection, null, Token));
    }

    private static MemoryErasureIdentity Saga(string content) =>
        MemoryErasureIdentity.ForSaga(SagaMemoryScopeKind.Global, null, content);

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync(Token);
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        return await command.ExecuteScalarAsync(Token);
    }

    /// <summary>Inserts a pending receipt with one subject, and returns that subject's digest.</summary>
    private async Task<byte[]> InsertPendingReceiptAsync(
        MemoryErasureKey key,
        Guid mutationId,
        MemoryReviewStore store,
        byte[] keyId,
        int mask)
    {
        byte[] subject = key.Subject(store, mutationId.ToString("D"));

        await using SqliteTransaction transaction = Connection.BeginTransaction(deferred: false);

        await MemoryErasureEvidence.InsertReceiptAsync(
            Connection,
            transaction,
            PendingReceipt(mutationId, store, keyId, mask),
            [subject],
            Token);

        await transaction.CommitAsync(Token);

        return subject;
    }

    private static MemoryErasureReceiptRow PendingReceipt(Guid mutationId, MemoryReviewStore store, byte[] keyId, int mask) =>
        new(
            mutationId,
            store,
            keyId,
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32),
            ErasedItemCount: 1,
            RemovedRowCount: 1,
            RemovedLabelCount: 0,
            RemovedRetirementSuppressionCount: 0,
            Authorship: MemoryExternalEvidence.NotApplicable,
            Context: MemoryExternalEvidence.NotApplicable,
            Embedding: MemoryExternalEvidence.NotApplicable,
            Backup: MemoryExternalEvidence.NotApplicable,
            OtherExternal: MemoryExternalEvidence.NotApplicable,
            RetainedCopiesMask: 0,
            ScrubStateCode: 1,
            ScrubPendingReasonMask: mask);

    private async Task<(int State, int Mask)> ScrubAsync(Guid mutationId)
    {
        MemoryErasureReceiptRow? receipt = await MemoryErasureEvidence.ReadReceiptAsync(Connection, null, mutationId, Token);

        Assert.NotNull(receipt);

        return (receipt.ScrubStateCode, receipt.ScrubPendingReasonMask);
    }

    /// <summary>Every row this connection has inserted, updated or deleted, including unchanged rewrites.</summary>
    private async Task<long> TotalChangesAsync() => (long)(await ScalarAsync("SELECT total_changes();"))!;

    private async Task<object?> ScalarAsync(string sql)
    {
        await using SqliteCommand command = Connection.CreateCommand();

        command.CommandText = sql;

        return await command.ExecuteScalarAsync(Token);
    }
}
