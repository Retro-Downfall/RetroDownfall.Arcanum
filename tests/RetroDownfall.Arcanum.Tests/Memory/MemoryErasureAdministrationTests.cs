using System.Buffers.Text;
using System.Security.Cryptography;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Memory;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Data;
using RetroDownfall.Arcanum.Tests.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Memory;

/// <summary>
/// The erasure administration service on its own: the write-ahead-log scrub against receipts actual Saga
/// erases left pending, and every member over a catalog that cannot hold evidence yet.
/// </summary>
/// <remarks>
/// <para>Memories go in through the Saga store's own insert and come out through the production erase
/// service, so every receipt here was written by the erase protocol. A reader held on a sibling
/// connection keeps a snapshot older than an erase's commit, which is what makes that erase's own
/// checkpoint answer busy and leave its receipt pending.</para>
///
/// <para>No test reaches the real keychain: the keyring is over an in-memory store.</para>
/// </remarks>
[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class MemoryErasureAdministrationTests
{
    private const string Account = ArcanumCredentialIdentity.MemoryErasureFingerprintKeyAccount;

    private const int WalCheckpointPendingBit = 1;

    private const int VectorIndexScrubUnverifiedBit = 4;

    private static CancellationToken Token => CancellationToken.None;

    [SkippableFact]
    public async Task Scrub_upgrades_only_receipts_whose_only_reason_was_the_wal()
    {
        await using ErasureHarness harness = await ErasureHarness.CreateAsync();

        string first = await harness.InsertAsync("The ferry leaves at the second bell.");

        string second = await harness.InsertAsync("The lighthouse lamp is trimmed at dusk.");

        MemoryErasureResultDto walOnly;

        MemoryErasureResultDto withVector;

        await using (await harness.HoldReaderAsync())
        {
            walOnly = await harness.EraseAsync(first);

            Assert.Equal<MemoryErasureScrubPendingReason>([MemoryErasureScrubPendingReason.WalCheckpointPending], walOnly.Local.PendingReasons);

            Assert.Equal(MemoryErasureWalCheckpointAttempt.Busy, walOnly.Local.WalCheckpointAttempt);

            await harness.Harness.CreateLegacyVectorMirrorAsync();

            withVector = await harness.EraseAsync(second);

            Assert.Equal<MemoryErasureScrubPendingReason>(
                [MemoryErasureScrubPendingReason.WalCheckpointPending, MemoryErasureScrubPendingReason.VectorIndexScrubUnverified],
                withVector.Local.PendingReasons);
        }

        MemoryErasureAdministration admin = harness.Administration();

        Assert.Equal(
            new MemoryErasureScrubResultDto(MemoryErasureWalCheckpointAttempt.Truncated, 1, 1),
            (await admin.ScrubAsync(Token)).Value);

        Assert.Equal((2, 0), await harness.ScrubStateAsync(walOnly.MutationId));

        Assert.Equal((1, VectorIndexScrubUnverifiedBit), await harness.ScrubStateAsync(withVector.MutationId));

        Assert.Equal(1, (await admin.GetStatusAsync(Token)).Value.PendingScrubReceipts);
    }

    [SkippableFact]
    public async Task A_busy_scrub_changes_nothing()
    {
        await using ErasureHarness harness = await ErasureHarness.CreateAsync();

        string memory = await harness.InsertAsync("The ferry leaves at the second bell.");

        await using IAsyncDisposable reader = await harness.HoldReaderAsync();

        MemoryErasureResultDto erased = await harness.EraseAsync(memory);

        Assert.Equal<MemoryErasureScrubPendingReason>([MemoryErasureScrubPendingReason.WalCheckpointPending], erased.Local.PendingReasons);

        Assert.Equal(
            new MemoryErasureScrubResultDto(MemoryErasureWalCheckpointAttempt.Busy, 0, 1),
            (await harness.Administration().ScrubAsync(Token)).Value);

        Assert.Equal((1, WalCheckpointPendingBit), await harness.ScrubStateAsync(erased.MutationId));
    }

    /// <summary>
    /// The scrub clears only the receipts it read as pending before it checkpointed. A receipt that
    /// committed while the checkpoint ran was not covered by it, so it keeps its WAL reason even though
    /// the checkpoint reports a truncation.
    /// </summary>
    [SkippableFact]
    public async Task Scrub_never_clears_a_receipt_committed_after_its_pending_snapshot()
    {
        await using ErasureHarness harness = await ErasureHarness.CreateAsync();

        string first = await harness.InsertAsync("The ferry leaves at the second bell.");

        string second = await harness.InsertAsync("The lighthouse lamp is trimmed at dusk.");

        MemoryErasureResultDto before;

        await using (await harness.HoldReaderAsync())
        {
            before = await harness.EraseAsync(first);
        }

        Assert.Equal<MemoryErasureScrubPendingReason>([MemoryErasureScrubPendingReason.WalCheckpointPending], before.Local.PendingReasons);

        MemoryErasureResultDto? during = null;

        MemoryErasureAdministration admin = harness.Administration(async cancellationToken =>
        {
            MemoryErasureResultDto erased;

            await using (await harness.HoldReaderAsync())
            {
                erased = await harness.EraseAsync(second);
            }

            Assert.Equal(MemoryErasureWalCheckpointAttempt.Busy, erased.Local.WalCheckpointAttempt);

            during = erased;

            return MemoryErasureWalCheckpointAttempt.Truncated;
        });

        Assert.Equal(
            new MemoryErasureScrubResultDto(MemoryErasureWalCheckpointAttempt.Truncated, 1, 1),
            (await admin.ScrubAsync(Token)).Value);

        Assert.Equal((2, 0), await harness.ScrubStateAsync(before.MutationId));

        Assert.NotNull(during);

        Assert.Equal((1, WalCheckpointPendingBit), await harness.ScrubStateAsync(during.MutationId));
    }

    /// <summary>
    /// Below Core 13 there is no evidence: the scrub and both halves of a key reset say the feature is
    /// not ready, and status reports an empty installation from the presence probe alone.
    /// </summary>
    [SkippableFact]
    public async Task Scrub_and_reset_key_refuse_below_core_13_and_status_reports_zeros()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using (SqliteConnection connection = await file.OpenAsync(Token))
        {
            GrimoireSchemaInstallResult installed = await GrimoireSchemaTestInstaller.InstallAsync(
                connection,
                CoreSchemaVersionTwelveFixture.ChainSet(),
                64,
                Token);

            Assert.Equal(12, installed.Core.SchemaVersion);
        }

        await using ArcanumDbContext db = SagaMemoryMidUpgradeWriteTests.CreateContext(file);

        SecretAccessRecordingCredentialStore credentials = new(new InMemoryOsCredentialStore());

        using MemoryErasureKeyring keyring = MemoryErasureTestKeys.Isolated(credentials);

        MemoryErasureAdministration admin = new(
            db,
            keyring,
            new MemoryReviewTokenCodec(TimeProvider.System),
            new MemoryErasureScrubber(new FixtureOrdinaryConnectionFactory(file.ConnectionString)),
            NullLogger<MemoryErasureAdministration>.Instance);

        Assert.Equal(ErrorCodes.MemoryErasure.Unavailable, (await admin.ScrubAsync(Token)).Error.Code);

        Assert.Equal(ErrorCodes.MemoryErasure.Unavailable, (await admin.PrepareKeyResetAsync(Token)).Error.Code);

        Assert.Equal(ErrorCodes.MemoryErasure.Unavailable, (await admin.ResetKeyAsync(new("any"), Token)).Error.Code);

        Result<MemoryErasureStatusDto> status = await admin.GetStatusAsync(Token);

        Assert.True(status.IsSuccess, status.IsFailure ? status.Error.Message : null);

        Assert.Equal(MemoryErasureKeyStatus.Absent, status.Value.KeyStatus);

        Assert.Equal<MemoryErasureStoreCountsDto>(
            [
                new(MemoryReviewStore.Covenant, 0, 0, 0),
                new(MemoryReviewStore.Saga, 0, 0, 0),
                new(MemoryReviewStore.Lexicon, 0, 0, 0),
            ],
            status.Value.Stores);

        Assert.Equal(0, status.Value.PendingScrubReceipts);

        Assert.Equal(0, credentials.TryGetCount(Account));

        Assert.Equal(0, credentials.SetCount(Account));

        Assert.Equal(1, credentials.ProbeCount(Account));
    }

    /// <summary>
    /// The host holds the key it created, but the key's OS item can be deleted while it runs. Status asks
    /// the store whether the item still exists, so it reports the key lost instead of present, with every
    /// row unverifiable, and reports it unavailable when the store cannot say.
    /// </summary>
    [SkippableFact]
    public async Task Status_reports_a_held_key_lost_once_its_item_is_deleted_and_unavailable_when_that_cannot_be_asked()
    {
        InMemoryOsCredentialStore inner = new();

        PresenceFaultStore credentials = new(inner);

        await using ErasureHarness harness = await ErasureHarness.CreateAsync(credentials);

        _ = await harness.EraseAsync(await harness.InsertAsync("The ferry leaves at the second bell."));

        MemoryErasureAdministration admin = harness.Administration();

        MemoryErasureStatusDto present = (await admin.GetStatusAsync(Token)).Value;

        Assert.Equal(MemoryErasureKeyStatus.Present, present.KeyStatus);

        Assert.Equal(new MemoryErasureStoreCountsDto(MemoryReviewStore.Saga, 1, 0, 1), present.Stores[1]);

        credentials.ProbeFails = true;

        MemoryErasureStatusDto unknown = (await admin.GetStatusAsync(Token)).Value;

        Assert.Equal(MemoryErasureKeyStatus.Unavailable, unknown.KeyStatus);

        Assert.All(unknown.Stores, static store => Assert.Equal(0, store.Unverifiable));

        credentials.ProbeFails = false;

        _ = inner.Delete(ArcanumCredentialIdentity.Service, Account);

        MemoryErasureStatusDto lost = (await admin.GetStatusAsync(Token)).Value;

        Assert.Equal(MemoryErasureKeyStatus.Lost, lost.KeyStatus);

        Assert.Equal(new MemoryErasureStoreCountsDto(MemoryReviewStore.Saga, 1, 1, 1), lost.Stores[1]);
    }

    /// <summary>
    /// An erase prepare over a held key whose OS item has been deleted is refused as a lost key, so no
    /// fingerprint is recorded under a key the operating system no longer holds.
    /// </summary>
    [SkippableFact]
    public async Task Prepare_refuses_a_held_key_whose_item_is_deleted_and_records_nothing()
    {
        InMemoryOsCredentialStore credentials = new();

        await using ErasureHarness harness = await ErasureHarness.CreateAsync(credentials);

        string first = await harness.InsertAsync("The ferry leaves at the second bell.");

        string second = await harness.InsertAsync("The lighthouse lamp is trimmed at dusk.");

        _ = await harness.EraseAsync(first);

        Assert.True((await harness.PrepareAsync(second)).IsSuccess);

        _ = credentials.Delete(ArcanumCredentialIdentity.Service, Account);

        Result<MemoryErasurePreflightDto> refused = await harness.PrepareAsync(second);

        Assert.True(refused.IsFailure, "A prepare was issued under a key the operating system no longer holds.");

        Assert.Equal(ErrorCodes.MemoryErasure.KeyLost, refused.Error.Code);

        Assert.Equal(1, (await harness.Administration().GetStatusAsync(Token)).Value.Stores[1].Fingerprints);

        Assert.Equal(OsCredentialStoreStatus.NotFound, credentials.ProbePresence(ArcanumCredentialIdentity.Service, Account));
    }

    /// <summary>
    /// A reset creates a key only when it writes one. Here another caller, such as a first erase on an
    /// installation with no evidence, writes the key between the reset's own read and its create path,
    /// so the reset finds that key, keeps it, and does not claim to have created it.
    /// </summary>
    [SkippableFact]
    public async Task A_reset_that_finds_a_key_another_caller_created_does_not_report_creating_it()
    {
        InMemoryOsCredentialStore inner = new();

        InterleavingCredentialStore store = new(inner);

        using MemoryErasureKeyring keyring = MemoryErasureTestKeys.Isolated(store);

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: true, keyring);

        MemoryErasureAdministration admin = new(
            harness.Context,
            keyring,
            new MemoryReviewTokenCodec(TimeProvider.System),
            new MemoryErasureScrubber(FixtureOrdinaryConnectionFactory.For(harness.Context)),
            NullLogger<MemoryErasureAdministration>.Instance);

        Result<MemoryErasureKeyResetPreflightDto> prepared = await admin.PrepareKeyResetAsync(Token);

        Assert.Equal(MemoryErasureKeyStatus.Absent, prepared.Value.KeyStatus);

        string concurrent = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

        // The apply's own read still finds nothing; the read after it, the create path's, finds the key.
        store.BeforeRead(store.Reads + 2, () => inner.Set(ArcanumCredentialIdentity.Service, Account, concurrent));

        Result<MemoryErasureKeyResetResultDto> reset = await admin.ResetKeyAsync(new(prepared.Value.PreflightToken), Token);

        Assert.True(reset.IsSuccess, reset.IsFailure ? reset.Error.Message : null);

        Assert.Equal(new MemoryErasureKeyResetResultDto(MemoryErasureKeyStatus.Present, 0, 0, KeyCreated: false), reset.Value);

        Assert.Equal(concurrent, inner.TryGet(ArcanumCredentialIdentity.Service, Account).Value);

        Assert.Equal(0, store.Writes);
    }

    /// <summary>An in-memory store whose metadata-only presence probe can be made to report a fault.</summary>
    private sealed class PresenceFaultStore(InMemoryOsCredentialStore inner) : IOsCredentialStore, IOsCredentialPresenceProbe
    {
        internal bool ProbeFails { get; set; }

        public bool IsAvailable => true;

        public OsCredentialStoreStatus ProbePresence(string service, string account) =>
            ProbeFails ? OsCredentialStoreStatus.Unavailable : inner.ProbePresence(service, account);

        public OsCredentialStoreResult TryGet(string service, string account) => inner.TryGet(service, account);

        public OsCredentialStoreResult Set(string service, string account, string secret) =>
            inner.Set(service, account, secret);

        public OsCredentialStoreResult Delete(string service, string account) => inner.Delete(service, account);
    }

    /// <summary>
    /// Reads the in-memory store, running a step queued for one numbered read just before it, so a test
    /// can place another caller's write between two reads the code under test makes.
    /// </summary>
    private sealed class InterleavingCredentialStore(InMemoryOsCredentialStore inner) : IOsCredentialStore
    {
        private readonly Dictionary<int, Action> _beforeRead = [];

        internal int Reads { get; private set; }

        internal int Writes { get; private set; }

        public bool IsAvailable => true;

        internal void BeforeRead(int read, Action step) => _beforeRead[read] = step;

        public OsCredentialStoreResult TryGet(string service, string account)
        {
            Reads++;

            if (_beforeRead.Remove(Reads, out Action? step))
            {
                step();
            }

            return inner.TryGet(service, account);
        }

        public OsCredentialStoreResult Set(string service, string account, string secret)
        {
            Writes++;

            return inner.Set(service, account, secret);
        }

        public OsCredentialStoreResult Delete(string service, string account) => inner.Delete(service, account);
    }

    /// <summary>
    /// A temporary Grimoire with the Annals on, a keyring over an in-memory store, and the production
    /// Saga erase service composed over the harness's own context.
    /// </summary>
    private sealed class ErasureHarness : IAsyncDisposable
    {
        private readonly FakeCovenantAuthorityProvider _authority = new();

        private ErasureHarness(SagaStoreHarness harness, MemoryErasureKeyring keyring)
        {
            Harness = harness;

            Keyring = keyring;

            Codec = new MemoryReviewTokenCodec(TimeProvider.System);

            Scrubber = new MemoryErasureScrubber(FixtureOrdinaryConnectionFactory.For(harness.Context));

            Erase = new SagaMemoryErasureService(
                harness.Context,
                keyring,
                keyring,
                Codec,
                Scrubber,
                new RecordingCovenantOperationGate(),
                CovenantErasureAuthorityFixture.Issuer(_authority),
                CovenantSqliteConnectionInitializer.Instance,
                new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings
                {
                    Features = new FeatureSettings { Annals = true },
                    Integrations = new IntegrationSettings
                    {
                        Embeddings = new EmbeddingIntegrationSettings { Dimensions = 64 },
                    },
                }));
        }

        internal SagaStoreHarness Harness { get; }

        internal MemoryErasureKeyring Keyring { get; }

        internal MemoryReviewTokenCodec Codec { get; }

        internal MemoryErasureScrubber Scrubber { get; }

        internal SagaMemoryErasureService Erase { get; }

        private SqliteConnection Connection => (SqliteConnection)Harness.Connection;

        internal static async Task<ErasureHarness> CreateAsync(IOsCredentialStore? credentials = null)
        {
            MemoryErasureKeyring keyring = MemoryErasureTestKeys.Isolated(credentials ?? new InMemoryOsCredentialStore());

            return new ErasureHarness(await SagaStoreHarness.CreateAsync(annalsEnabled: true, keyring), keyring);
        }

        internal MemoryErasureAdministration Administration() =>
            new(Harness.Context, Keyring, Codec, Scrubber, NullLogger<MemoryErasureAdministration>.Instance);

        internal MemoryErasureAdministration Administration(
            Func<CancellationToken, Task<MemoryErasureWalCheckpointAttempt>> checkpoint) =>
            new(Harness.Context, Keyring, Codec, Scrubber, NullLogger<MemoryErasureAdministration>.Instance, checkpoint);

        /// <summary>Writes one Global memory through the store's own insert, and requires that it landed.</summary>
        internal async Task<string> InsertAsync(string content)
        {
            string id = Guid.NewGuid().ToString();

            Assert.Equal(
                SagaMemoryWriteOutcome.Written,
                await Harness.Store.InsertAsync(id, content, DateTimeOffset.UtcNow, null, null, "extraction", Harness.Embedding(), Token));

            return id;
        }

        /// <summary>Prepares the erase of one memory through the production service, whatever it answers.</summary>
        internal async Task<Result<MemoryErasurePreflightDto>> PrepareAsync(string memoryId) =>
            await Erase.PrepareAsync(await PrepareRequestAsync(memoryId), Token);

        private async Task<SagaErasePrepareRequest> PrepareRequestAsync(string memoryId)
        {
            SagaMemoryCurationRow row = (await Harness.Store.ReadCurationRowAsync(memoryId, Token))!;

            AnnalClaimHead? claim = await Harness.Annals.GetClaimAsync(AnnalSubjectStore.Saga, memoryId, Token);

            return new SagaErasePrepareRequest(
                memoryId,
                Convert.ToHexString(AnnalContentDigest.ForSagaMemory(row.Memory.Content)),
                claim?.CurrentVersionId,
                Guid.NewGuid());
        }

        /// <summary>Prepares and applies the erase of one memory through the production service.</summary>
        internal async Task<MemoryErasureResultDto> EraseAsync(string memoryId)
        {
            SagaErasePrepareRequest prepare = await PrepareRequestAsync(memoryId);

            Result<MemoryErasurePreflightDto> prepared = await Erase.PrepareAsync(prepare, Token);

            Assert.True(prepared.IsSuccess, prepared.IsFailure ? prepared.Error.Message : null);

            Result<MemoryErasureResultDto> applied = await Erase.ApplyAsync(
                new SagaEraseRequest(
                    prepare.MemoryId,
                    prepare.ExpectedContentHash,
                    prepare.ExpectedClaimVersionId,
                    prepare.MutationId,
                    prepared.Value.PreflightToken),
                CovenantErasureAuthorityFixture.OperatorContext(_authority),
                Token);

            Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : null);

            return applied.Value;
        }

        /// <summary>One receipt's scrub state and pending-reason mask, read through the evidence store.</summary>
        internal async Task<(int State, int Mask)> ScrubStateAsync(Guid mutationId)
        {
            MemoryErasureReceiptRow? receipt = await MemoryErasureEvidence.ReadReceiptAsync(Connection, null, mutationId, Token);

            Assert.NotNull(receipt);

            return (receipt.ScrubStateCode, receipt.ScrubPendingReasonMask);
        }

        /// <summary>
        /// Holds a read snapshot on a sibling connection until disposed, so a checkpoint taken meanwhile
        /// cannot truncate the log.
        /// </summary>
        internal async Task<IAsyncDisposable> HoldReaderAsync()
        {
            ArcanumDbContext sibling = Harness.CreateSiblingContext();

            try
            {
                SqliteConnection connection = (SqliteConnection)sibling.Database.GetDbConnection();

                await sibling.Database.OpenConnectionAsync(Token);

                await using SqliteCommand snapshot = connection.CreateCommand();

                snapshot.CommandText = "BEGIN; SELECT count(*) FROM saga_memories;";

                _ = await snapshot.ExecuteScalarAsync(Token);

                return new HeldReader(sibling, connection);
            }
            catch
            {
                await sibling.DisposeAsync();

                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Harness.DisposeAsync();

            Keyring.Dispose();
        }

        /// <summary>Rolls the held snapshot back and releases its context.</summary>
        private sealed class HeldReader(ArcanumDbContext sibling, SqliteConnection connection) : IAsyncDisposable
        {
            public async ValueTask DisposeAsync()
            {
                try
                {
                    await using SqliteCommand rollback = connection.CreateCommand();

                    rollback.CommandText = "ROLLBACK;";

                    _ = await rollback.ExecuteNonQueryAsync(CancellationToken.None);
                }
                finally
                {
                    await sibling.DisposeAsync();
                }
            }
        }
    }
}
