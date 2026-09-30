using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Memory;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Memory;

/// <summary>
/// The protocol every store's erase shares: schema readiness, key access for prepare and apply,
/// receipt-first replay, the post-commit scrub and result, retained copies, and scope notes.
/// </summary>
/// <remarks>
/// Receipts are written through the evidence store's own insert and keys through the production
/// keyring, over the head catalog, so the receipt table's checks and update guard stay in force.
/// </remarks>
[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class MemoryErasureProtocolTests(GrimoireFixture fixture) : IAsyncLifetime
{
    private static readonly MemoryErasureIdentity Identity =
        MemoryErasureIdentity.ForSaga(SagaMemoryScopeKind.Global, null, "The operator prefers dark mode.");

    private static readonly MemoryRetainedLocalCopy[] ConstantCopies =
    [
        MemoryRetainedLocalCopy.SessionTranscripts,
        MemoryRetainedLocalCopy.SearchAndSummaryDerivatives,
        MemoryRetainedLocalCopy.Attachments,
        MemoryRetainedLocalCopy.ResponseCaches,
        MemoryRetainedLocalCopy.ApplicationLogs,
        MemoryRetainedLocalCopy.BackupArchives,
        MemoryRetainedLocalCopy.OtherLocalState,
    ];

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    private static CancellationToken Token => CancellationToken.None;

    private SqliteConnection Connection => (SqliteConnection)_db!.Database.GetDbConnection();

    public Task InitializeAsync()
    {
        _dbPath = fixture.CopyDatabase();

        _db = fixture.CreateContext(_dbPath);

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

    [Theory]
    [InlineData(MemoryReviewStore.Covenant)]
    [InlineData(MemoryReviewStore.Saga)]
    [InlineData(MemoryReviewStore.Lexicon)]
    public void Retained_copies_are_the_constant_list_and_audit_only_when_audit_files_exist(MemoryReviewStore store)
    {
        Assert.Equal(ConstantCopies, MemoryErasureRetainedCopies.For(store, auditFilesExist: false));

        Assert.Equal(
            [
                MemoryRetainedLocalCopy.SessionTranscripts,
                MemoryRetainedLocalCopy.SearchAndSummaryDerivatives,
                MemoryRetainedLocalCopy.Attachments,
                MemoryRetainedLocalCopy.ResponseCaches,
                MemoryRetainedLocalCopy.ApplicationLogs,
                MemoryRetainedLocalCopy.AuditLog,
                MemoryRetainedLocalCopy.BackupArchives,
                MemoryRetainedLocalCopy.OtherLocalState,
            ],
            MemoryErasureRetainedCopies.For(store, auditFilesExist: true));
    }

    [Fact]
    public void Audit_files_exist_only_when_a_dated_file_sits_beside_the_configured_path()
    {
        string directory = Directory.CreateTempSubdirectory("erasure-audit-").FullName;

        try
        {
            string configured = Path.Combine(directory, "audit.jsonl");

            Assert.False(MemoryErasureRetainedCopies.AuditFilesExist(Path.Combine(directory, "missing", "audit.jsonl")));

            Assert.False(MemoryErasureRetainedCopies.AuditFilesExist(configured));

            // The configured name itself, another stem, and another extension are not audit files.
            File.WriteAllText(configured, "{}");

            File.WriteAllText(Path.Combine(directory, "guardrail-20260101.jsonl"), "{}");

            File.WriteAllText(Path.Combine(directory, "audit-20260101.txt"), "{}");

            Assert.False(MemoryErasureRetainedCopies.AuditFilesExist(configured));

            File.WriteAllText(Path.Combine(directory, "audit-20260101.jsonl"), "{}");

            Assert.True(MemoryErasureRetainedCopies.AuditFilesExist(configured));

            // The settings overload reads the path the audit logger itself resolves.
            ArcanumSettings settings = new();

            Assert.Equal(
                MemoryErasureRetainedCopies.AuditFilesExist(settings.ResolveHostAuditLog().FilePath),
                MemoryErasureRetainedCopies.AuditFilesExist(settings));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Notes_are_scope_boundary_notes_in_code_order()
    {
        Assert.Equal(
            [MemoryErasureNote.UnresolvedScopeStopsMatchingOnResolution, MemoryErasureNote.OtherScopesUnaffected],
            MemoryErasureNotes.For(MemoryReviewStore.Saga, MemoryErasureScopeKind.LegacyUnresolved, false));

        Assert.Equal(
            [MemoryErasureNote.OtherScopesUnaffected],
            MemoryErasureNotes.For(MemoryReviewStore.Saga, MemoryErasureScopeKind.Global, false));

        Assert.Equal(
            [MemoryErasureNote.OtherScopesUnaffected],
            MemoryErasureNotes.For(MemoryReviewStore.Saga, MemoryErasureScopeKind.Campaign, false));

        Assert.Equal(
            [MemoryErasureNote.OtherScopesUnaffected],
            MemoryErasureNotes.For(MemoryReviewStore.Lexicon, MemoryErasureScopeKind.Campaign, false));

        Assert.Equal(
            [MemoryErasureNote.OtherScopesUnaffected],
            MemoryErasureNotes.For(MemoryReviewStore.Lexicon, MemoryErasureScopeKind.Global, false));

        foreach (bool reclaimsKey in (bool[])[false, true])
        {
            Assert.Equal(
                [
                    MemoryErasureNote.GlobalKeyStillProposableInCampaigns,
                    MemoryErasureNote.OtherScopesUnaffected,
                    MemoryErasureNote.CovenantDrainsInFlightTurns,
                ],
                MemoryErasureNotes.For(MemoryReviewStore.Covenant, MemoryErasureScopeKind.Global, reclaimsKey));

            Assert.Equal(
                [MemoryErasureNote.OtherScopesUnaffected, MemoryErasureNote.CovenantDrainsInFlightTurns],
                MemoryErasureNotes.For(MemoryReviewStore.Covenant, MemoryErasureScopeKind.Campaign, reclaimsKey));
        }

        _ = Assert.Throws<ArgumentException>(
            () => MemoryErasureNotes.For(MemoryReviewStore.Saga, MemoryErasureScopeKind.Global, true));

        _ = Assert.Throws<ArgumentException>(
            () => MemoryErasureNotes.For(MemoryReviewStore.Lexicon, MemoryErasureScopeKind.Global, true));

        _ = Assert.Throws<ArgumentException>(
            () => MemoryErasureNotes.For(MemoryReviewStore.Covenant, MemoryErasureScopeKind.LegacyUnresolved, false));
    }

    [SkippableFact]
    public async Task Probe_replays_a_matching_digest_and_conflicts_on_a_different_digest_or_store()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using MemoryErasureKey key = MemoryErasureTestKeys.CreateKey(new InMemoryOsCredentialStore());

        MemoryErasureReceiptRow row = Receipt(key, MemoryReviewStore.Saga, scrubState: 1, mask: 1);

        await InsertAsync(row, key);

        Result<MemoryErasureReceiptRow?> replay = await MemoryErasureProtocol.ProbeReceiptAsync(
            Connection, null, MemoryReviewStore.Saga, row.MutationId, [.. row.RequestDigest], Token);

        Assert.True(replay.IsSuccess, replay.IsFailure ? replay.Error.Message : string.Empty);

        Assert.Equal(row.MutationId, replay.Value!.MutationId);

        Assert.Equal(row.EffectDigest, replay.Value.EffectDigest);

        byte[] otherDigest = [.. row.RequestDigest];

        otherDigest[^1] ^= 0x01;

        Result<MemoryErasureReceiptRow?> otherRequest = await MemoryErasureProtocol.ProbeReceiptAsync(
            Connection, null, MemoryReviewStore.Saga, row.MutationId, otherDigest, Token);

        Assert.Equal(ErrorCodes.Security.IdempotencyConflict, otherRequest.Error.Code);

        Result<MemoryErasureReceiptRow?> otherStore = await MemoryErasureProtocol.ProbeReceiptAsync(
            Connection, null, MemoryReviewStore.Lexicon, row.MutationId, [.. row.RequestDigest], Token);

        Assert.Equal(ErrorCodes.Security.IdempotencyConflict, otherStore.Error.Code);

        Result<MemoryErasureReceiptRow?> absent = await MemoryErasureProtocol.ProbeReceiptAsync(
            Connection, null, MemoryReviewStore.Saga, Guid.NewGuid(), [.. row.RequestDigest], Token);

        Assert.True(absent.IsSuccess);

        Assert.Null(absent.Value);

        // Inside the erase's own transaction the probe reads the same row.
        await using SqliteTransaction transaction = Connection.BeginTransaction();

        Result<MemoryErasureReceiptRow?> inside = await MemoryErasureProtocol.ProbeReceiptAsync(
            Connection, transaction, MemoryReviewStore.Saga, row.MutationId, [.. row.RequestDigest], Token);

        Assert.Equal(row.MutationId, inside.Value!.MutationId);
    }

    [SkippableFact]
    public async Task Finish_upgrades_a_wal_only_pending_receipt_when_the_checkpoint_truncates()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using MemoryErasureKey key = MemoryErasureTestKeys.CreateKey(new InMemoryOsCredentialStore());

        MemoryErasureReceiptRow row = Receipt(key, MemoryReviewStore.Saga, scrubState: 1, mask: 1);

        await InsertAsync(row, key);

        FixtureOrdinaryConnectionFactory factory = FixtureOrdinaryConnectionFactory.For(_db!);

        MemoryErasureNote[] notes = [MemoryErasureNote.OtherScopesUnaffected];

        MemoryErasureResultDto result = await MemoryErasureProtocol.FinishAsync(
            Connection, new MemoryErasureScrubber(factory), row, replayed: false, notes, CancellationToken.None);

        Assert.Equal(MemoryLocalErasureOutcome.Verified, result.Local.Outcome);

        Assert.Empty(result.Local.PendingReasons);

        Assert.Equal(MemoryErasureWalCheckpointAttempt.Truncated, result.Local.WalCheckpointAttempt);

        Assert.Equal([GrimoireOrdinaryFreshConnectionKind.ReadWrite], factory.Kinds);

        Assert.Equal(2, (await MemoryErasureEvidence.ReadReceiptAsync(Connection, null, row.MutationId, Token))!.ScrubStateCode);

        // Everything else is the receipt's, read back rather than recomputed.
        Assert.Equal(MemoryReviewStore.Saga, result.Store);

        Assert.Equal(row.MutationId, result.MutationId);

        Assert.False(result.Replayed);

        Assert.Equal(Convert.ToHexStringLower(row.EffectDigest), result.EffectDigest);

        Assert.Equal((2, 9L, 1, 1, true), (
            result.Local.ErasedItemCount,
            result.Local.RemovedRowCount,
            result.Local.RemovedLabelCount,
            result.Local.RemovedRetirementSuppressionCount,
            result.Local.SuppressionFingerprintRecorded));

        Assert.Equal(MemoryExternalRevocation.NotPerformed, result.External.Revocation);

        Assert.Equal(
            [
                new MemoryExternalExposureChannelDto(MemoryExternalChannel.InferenceProviderAuthorship, MemoryExternalEvidence.Known),
                new MemoryExternalExposureChannelDto(MemoryExternalChannel.InferenceProviderContext, MemoryExternalEvidence.NotRecorded),
                new MemoryExternalExposureChannelDto(MemoryExternalChannel.EmbeddingProvider, MemoryExternalEvidence.Known),
                new MemoryExternalExposureChannelDto(MemoryExternalChannel.EncryptedBackup, MemoryExternalEvidence.ReceiptWindow),
                new MemoryExternalExposureChannelDto(MemoryExternalChannel.OtherExternal, MemoryExternalEvidence.NotRecorded),
            ],
            result.External.Channels);

        Assert.Equal(MemoryRetainedLocalCopies.FromMask(row.RetainedCopiesMask), result.RetainedLocalCopies);

        Assert.Equal(notes, result.Notes);
    }

    [SkippableFact]
    public async Task Finish_keeps_a_non_upgradable_reason_pending()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using MemoryErasureKey key = MemoryErasureTestKeys.CreateKey(new InMemoryOsCredentialStore());

        int mask = MemoryErasureScrubPendingReasons.ToMask(
            [MemoryErasureScrubPendingReason.WalCheckpointPending, MemoryErasureScrubPendingReason.FullTextSecureDeleteUnverified]);

        MemoryErasureReceiptRow row = Receipt(key, MemoryReviewStore.Lexicon, scrubState: 1, mask);

        await InsertAsync(row, key);

        MemoryErasureResultDto result = await MemoryErasureProtocol.FinishAsync(
            Connection,
            new MemoryErasureScrubber(FixtureOrdinaryConnectionFactory.For(_db!)),
            row,
            replayed: true,
            [],
            CancellationToken.None);

        Assert.Equal(MemoryLocalErasureOutcome.RowsRemovedScrubPending, result.Local.Outcome);

        Assert.Equal([MemoryErasureScrubPendingReason.FullTextSecureDeleteUnverified], result.Local.PendingReasons);

        Assert.Equal(MemoryErasureWalCheckpointAttempt.Truncated, result.Local.WalCheckpointAttempt);

        Assert.True(result.Replayed);

        MemoryErasureReceiptRow stored = (await MemoryErasureEvidence.ReadReceiptAsync(Connection, null, row.MutationId, Token))!;

        Assert.Equal((1, 2), (stored.ScrubStateCode, stored.ScrubPendingReasonMask));
    }

    [SkippableFact]
    public async Task Finish_does_not_checkpoint_a_verified_receipt()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using MemoryErasureKey key = MemoryErasureTestKeys.CreateKey(new InMemoryOsCredentialStore());

        MemoryErasureReceiptRow row = Receipt(key, MemoryReviewStore.Saga, scrubState: 2, mask: 0);

        await InsertAsync(row, key);

        FixtureOrdinaryConnectionFactory factory = FixtureOrdinaryConnectionFactory.For(_db!);

        MemoryErasureResultDto result = await MemoryErasureProtocol.FinishAsync(
            Connection, new MemoryErasureScrubber(factory), row, replayed: true, [], CancellationToken.None);

        Assert.Equal(MemoryErasureWalCheckpointAttempt.NotAttempted, result.Local.WalCheckpointAttempt);

        Assert.Equal(MemoryLocalErasureOutcome.Verified, result.Local.Outcome);

        Assert.Empty(factory.Kinds);
    }

    [SkippableFact]
    public async Task Finish_does_not_checkpoint_a_receipt_whose_only_reasons_never_upgrade()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using MemoryErasureKey key = MemoryErasureTestKeys.CreateKey(new InMemoryOsCredentialStore());

        MemoryErasureReceiptRow row = Receipt(key, MemoryReviewStore.Saga, scrubState: 1, mask: 4);

        await InsertAsync(row, key);

        FixtureOrdinaryConnectionFactory factory = FixtureOrdinaryConnectionFactory.For(_db!);

        MemoryErasureResultDto result = await MemoryErasureProtocol.FinishAsync(
            Connection, new MemoryErasureScrubber(factory), row, replayed: false, [], CancellationToken.None);

        Assert.Equal(MemoryErasureWalCheckpointAttempt.NotAttempted, result.Local.WalCheckpointAttempt);

        Assert.Equal([MemoryErasureScrubPendingReason.VectorIndexScrubUnverified], result.Local.PendingReasons);

        Assert.Empty(factory.Kinds);
    }

    [SkippableFact]
    public async Task Finish_refuses_to_run_inside_a_transaction()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using MemoryErasureKey key = MemoryErasureTestKeys.CreateKey(new InMemoryOsCredentialStore());

        MemoryErasureReceiptRow row = Receipt(key, MemoryReviewStore.Saga, scrubState: 1, mask: 1);

        FixtureOrdinaryConnectionFactory factory = FixtureOrdinaryConnectionFactory.For(_db!);

        await using SqliteTransaction transaction = Connection.BeginTransaction();

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => MemoryErasureProtocol.FinishAsync(
            Connection, new MemoryErasureScrubber(factory), row, replayed: false, [], CancellationToken.None));

        Assert.Empty(factory.Kinds);
    }

    /// <summary>
    /// Apply resolves the key the operator's erase needs and maps every state it can meet (R16).
    /// </summary>
    [SkippableTheory]
    [InlineData("present")]
    [InlineData("present-with-foreign-saga-row")]
    [InlineData("absent-with-rows")]
    [InlineData("absent-without-rows")]
    [InlineData("unavailable")]
    [InlineData("malformed")]
    public async Task Apply_key_access_maps_every_key_state(string state)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        CountingOsCredentialStore credentials = new(new InMemoryOsCredentialStore());

        switch (state)
        {
            case "present":
            case "present-with-foreign-saga-row":
            {
                using MemoryErasureKey created = MemoryErasureTestKeys.CreateKey(credentials);

                if (state == "present-with-foreign-saga-row")
                {
                    await ForeignFingerprintAsync(MemoryReviewStore.Saga);
                }

                using MemoryErasureKeyring keys = MemoryErasureTestKeys.Isolated(credentials);

                Result<MemoryErasureKey> saga = await MemoryErasureProtocol.OpenKeyForApplyAsync(Connection, keys, MemoryReviewStore.Saga, Token);

                Result<MemoryErasureKey> lexicon = await MemoryErasureProtocol.OpenKeyForApplyAsync(Connection, keys, MemoryReviewStore.Lexicon, Token);

                using MemoryErasureKey lexiconKey = lexicon.Value;

                Assert.True(lexiconKey.HasKeyId(created.KeyId));

                if (state == "present")
                {
                    using MemoryErasureKey sagaKey = saga.Value;

                    Assert.True(sagaKey.HasKeyId(created.KeyId));
                }
                else
                {
                    Assert.Equal(ErrorCodes.MemoryErasure.KeyLost, saga.Error.Code);
                }

                break;
            }

            case "absent-with-rows":
            {
                using (MemoryErasureKey created = MemoryErasureTestKeys.CreateKey(credentials))
                {
                    await MemoryErasureTestKeys.SeedFingerprintAsync(
                        Connection,
                        created,
                        MemoryErasureIdentity.ForLexicon(null, "Mill Warden"),
                        Token);
                }

                _ = credentials.Delete(ArcanumCredentialIdentity.Service, ArcanumCredentialIdentity.MemoryErasureFingerprintKeyAccount);

                using MemoryErasureKeyring keys = MemoryErasureTestKeys.Isolated(credentials);

                // A row in any store: the fingerprint is Lexicon's, the erase is Saga's.
                Assert.Equal(
                    ErrorCodes.MemoryErasure.KeyLost,
                    (await MemoryErasureProtocol.OpenKeyForApplyAsync(Connection, keys, MemoryReviewStore.Saga, Token)).Error.Code);

                break;
            }

            case "absent-without-rows":
            {
                using MemoryErasureKeyring keys = MemoryErasureTestKeys.Isolated(credentials);

                Assert.Equal(
                    ErrorCodes.MemoryErasure.InvalidPreflight,
                    (await MemoryErasureProtocol.OpenKeyForApplyAsync(Connection, keys, MemoryReviewStore.Saga, Token)).Error.Code);

                break;
            }

            default:
            {
                if (state == "unavailable")
                {
                    credentials.FailWith = OsCredentialStoreStatus.Unavailable;
                }
                else
                {
                    _ = credentials.Set(
                        ArcanumCredentialIdentity.Service,
                        ArcanumCredentialIdentity.MemoryErasureFingerprintKeyAccount,
                        "not-base64url");
                }

                using MemoryErasureKeyring keys = MemoryErasureTestKeys.Isolated(credentials);

                Assert.Equal(
                    ErrorCodes.MemoryErasure.KeyUnavailable,
                    (await MemoryErasureProtocol.OpenKeyForApplyAsync(Connection, keys, MemoryReviewStore.Saga, Token)).Error.Code);

                break;
            }
        }
    }

    /// <summary>
    /// Apply re-probes, because an erase is operator-initiated: a failure the latch remembered from an
    /// automatic probe is asked again rather than repeated.
    /// </summary>
    [SkippableFact]
    public async Task Apply_key_access_reprobes_a_latched_failure()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        CountingOsCredentialStore credentials = new(new InMemoryOsCredentialStore());

        using MemoryErasureKey created = MemoryErasureTestKeys.CreateKey(credentials);

        using MemoryErasureKeyring keys = MemoryErasureTestKeys.Isolated(credentials);

        credentials.FailWith = OsCredentialStoreStatus.Unavailable;

        Assert.Equal(MemoryErasureKeyState.Unavailable, keys.OpenExisting(MemoryErasureKeyProbe.UseLatched).State);

        credentials.FailWith = null;

        Result<MemoryErasureKey> opened = await MemoryErasureProtocol.OpenKeyForApplyAsync(Connection, keys, MemoryReviewStore.Saga, Token);

        using MemoryErasureKey key = opened.Value;

        Assert.True(key.HasKeyId(created.KeyId));
    }

    [SkippableFact]
    public async Task Prepare_key_access_creates_only_when_no_row_exists()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        WriteCountingCredentialStore credentials = new();

        using (MemoryErasureKeyring keys = MemoryErasureTestKeys.Isolated(credentials))
        {
            Result<MemoryErasureKey> created = await MemoryErasureProtocol.OpenKeyForPrepareAsync(Connection, keys, MemoryReviewStore.Saga, Token);

            using MemoryErasureKey key = created.Value;

            Assert.Equal(1, credentials.Writes);

            await MemoryErasureTestKeys.SeedFingerprintAsync(Connection, key, Identity, Token);

            // With a present key and the row it wrote, prepare opens it again without a write.
            Result<MemoryErasureKey> again = await MemoryErasureProtocol.OpenKeyForPrepareAsync(Connection, keys, MemoryReviewStore.Saga, Token);

            using MemoryErasureKey reopened = again.Value;

            Assert.True(reopened.HasKeyId(key.KeyId));

            Assert.Equal(1, credentials.Writes);
        }

        _ = credentials.Delete(ArcanumCredentialIdentity.Service, ArcanumCredentialIdentity.MemoryErasureFingerprintKeyAccount);

        using MemoryErasureKeyring fresh = MemoryErasureTestKeys.Isolated(credentials);

        // The row now in the Saga store makes the missing key lost for every store's prepare.
        Result<MemoryErasureKey> lost = await MemoryErasureProtocol.OpenKeyForPrepareAsync(Connection, fresh, MemoryReviewStore.Lexicon, Token);

        Assert.Equal(ErrorCodes.MemoryErasure.KeyLost, lost.Error.Code);

        Assert.Equal(1, credentials.Writes);

        Assert.Equal(
            OsCredentialStoreStatus.NotFound,
            credentials.TryGet(ArcanumCredentialIdentity.Service, ArcanumCredentialIdentity.MemoryErasureFingerprintKeyAccount).Status);
    }

    [SkippableFact]
    public async Task Prepare_key_access_refuses_a_store_holding_foreign_rows_and_an_unreadable_key()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        CountingOsCredentialStore credentials = new(new InMemoryOsCredentialStore());

        using (MemoryErasureKeyring keys = MemoryErasureTestKeys.Isolated(credentials))
        {
            using MemoryErasureKey created = (await MemoryErasureProtocol.OpenKeyForPrepareAsync(Connection, keys, MemoryReviewStore.Saga, Token)).Value;

            await ForeignFingerprintAsync(MemoryReviewStore.Covenant);

            Assert.Equal(
                ErrorCodes.MemoryErasure.KeyLost,
                (await MemoryErasureProtocol.OpenKeyForPrepareAsync(Connection, keys, MemoryReviewStore.Covenant, Token)).Error.Code);

            using MemoryErasureKey saga = (await MemoryErasureProtocol.OpenKeyForPrepareAsync(Connection, keys, MemoryReviewStore.Saga, Token)).Value;

            Assert.True(saga.HasKeyId(created.KeyId));
        }

        credentials.FailWith = OsCredentialStoreStatus.Unavailable;

        using MemoryErasureKeyring unavailable = MemoryErasureTestKeys.Isolated(credentials);

        Assert.Equal(
            ErrorCodes.MemoryErasure.KeyUnavailable,
            (await MemoryErasureProtocol.OpenKeyForPrepareAsync(Connection, unavailable, MemoryReviewStore.Saga, Token)).Error.Code);
    }

    /// <summary>
    /// Both key openings can read the OS credential store, which never happens inside a SQLite
    /// transaction (spec §5.1), whether the transaction is an object or a raw <c>BEGIN</c>.
    /// </summary>
    [SkippableTheory]
    [InlineData("prepare", "sqlite-transaction")]
    [InlineData("prepare", "raw-begin-immediate")]
    [InlineData("apply", "sqlite-transaction")]
    [InlineData("apply", "raw-begin-immediate")]
    public async Task Key_access_refuses_to_run_inside_a_transaction(string phase, string transactionKind)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        CountingOsCredentialStore credentials = new(new InMemoryOsCredentialStore());

        using MemoryErasureKeyring keys = MemoryErasureTestKeys.Isolated(credentials);

        SqliteTransaction? transaction = null;

        if (transactionKind == "sqlite-transaction")
        {
            transaction = Connection.BeginTransaction();
        }
        else
        {
            await ExecuteAsync("BEGIN IMMEDIATE;");
        }

        try
        {
            InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() => phase == "prepare"
                ? MemoryErasureProtocol.OpenKeyForPrepareAsync(Connection, keys, MemoryReviewStore.Saga, Token)
                : MemoryErasureProtocol.OpenKeyForApplyAsync(Connection, keys, MemoryReviewStore.Saga, Token));

            Assert.Contains("credential store", refused.Message, StringComparison.Ordinal);

            Assert.Equal(0, credentials.Calls);
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
            else
            {
                await ExecuteAsync("ROLLBACK;");
            }
        }
    }

    [SkippableFact]
    public async Task RequireInstalledAsync_refuses_a_version_twelve_catalog()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Assert.True((await MemoryErasureProtocol.RequireInstalledAsync(Connection, Token)).IsSuccess);

        using EvolutionScratchDatabase scratch = EvolutionScratchDatabase.Create();

        await using SqliteConnection twelve = await scratch.OpenAsync(Token);

        GrimoireSchemaInstallResult installed = await GrimoireSchemaTestInstaller.InstallAsync(
            twelve,
            CoreSchemaVersionTwelveFixture.ChainSet(),
            1536,
            Token);

        Assert.Equal(12, installed.Core.SchemaVersion);

        Result refused = await MemoryErasureProtocol.RequireInstalledAsync(twelve, Token);

        Assert.Equal(ErrorCodes.MemoryErasure.Unavailable, refused.Error.Code);
    }

    private static MemoryErasureReceiptRow Receipt(MemoryErasureKey key, MemoryReviewStore store, int scrubState, int mask) =>
        new(
            Guid.NewGuid(),
            store,
            key.KeyId.ToArray(),
            [.. Enumerable.Range(0, 32).Select(static value => (byte)value)],
            [.. Enumerable.Range(0, 32).Select(static value => (byte)(0xA0 ^ value))],
            ErasedItemCount: 2,
            RemovedRowCount: 9,
            RemovedLabelCount: 1,
            RemovedRetirementSuppressionCount: 1,
            Authorship: MemoryExternalEvidence.Known,
            Context: MemoryExternalEvidence.NotRecorded,
            Embedding: MemoryExternalEvidence.Known,
            Backup: MemoryExternalEvidence.ReceiptWindow,
            OtherExternal: MemoryExternalEvidence.NotRecorded,
            RetainedCopiesMask: MemoryRetainedLocalCopies.ToMask(
                [MemoryRetainedLocalCopy.SessionTranscripts, MemoryRetainedLocalCopy.AuditLog, MemoryRetainedLocalCopy.OtherLocalState]),
            ScrubStateCode: scrubState,
            ScrubPendingReasonMask: mask);

    private async Task InsertAsync(MemoryErasureReceiptRow row, MemoryErasureKey key)
    {
        await using SqliteTransaction transaction = Connection.BeginTransaction();

        await MemoryErasureEvidence.InsertReceiptAsync(
            Connection,
            transaction,
            row,
            [key.Subject(row.Store, Guid.NewGuid().ToString())],
            Token);

        await transaction.CommitAsync(Token);
    }

    /// <summary>A fingerprint recorded under a key identifier no key in this test holds.</summary>
    private async Task ForeignFingerprintAsync(MemoryReviewStore store) =>
        _ = await MemoryErasureEvidence.InsertFingerprintAsync(
            Connection,
            null,
            [.. Enumerable.Repeat((byte)0x7E, 32)],
            store,
            [.. Enumerable.Repeat((byte)0x5A, 16)],
            Token);

    private async Task ExecuteAsync(string sql)
    {
        await using SqliteCommand command = Connection.CreateCommand();

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync(Token);
    }

    /// <summary>An in-memory credential store that counts the writes made into it.</summary>
    private sealed class WriteCountingCredentialStore : IOsCredentialStore
    {
        private readonly InMemoryOsCredentialStore _inner = new();

        internal int Writes { get; private set; }

        public bool IsAvailable => _inner.IsAvailable;

        public OsCredentialStoreResult TryGet(string service, string account) => _inner.TryGet(service, account);

        public OsCredentialStoreResult Set(string service, string account, string secret)
        {
            Writes++;

            return _inner.Set(service, account, secret);
        }

        public OsCredentialStoreResult Delete(string service, string account) => _inner.Delete(service, account);
    }
}
