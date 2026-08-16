using System.Collections.Immutable;
using System.Security.Cryptography;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.TheForge;
using RetroDownfall.Arcanum.Tests.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// The restore arm of the Campaign marker protocol, against real directories and a real SQLCipher
/// staged database.
/// </summary>
/// <remarks>
/// Real storage on both sides on purpose. The properties under test are what the operating system
/// does with a no-follow open and what the guard triggers do to an unauthorized write, and a fake of
/// either would only prove the fake agrees with itself (§10.12).
/// </remarks>
public sealed class CampaignPathMarkerLifecycleTests : IAsyncLifetime, IDisposable
{

    private static readonly byte[] RootIdentityKey = Convert.FromHexString(
        "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");

    private static readonly byte[] MarkerKey = Convert.FromHexString(
        "202122232425262728292A2B2C2D2E2F303132333435363738393A3B3C3D3E3F");

    private readonly string _parent =
        Directory.CreateTempSubdirectory("arcanum-marker-lifecycle-").FullName;

    private readonly PhysicalCampaignRootOpener _opener = new(new StubKeySource(RootIdentityKey));

    private readonly CampaignPathMarkerCodec _codec = new(new StubKeySource(MarkerKey));

    private CovenantSchemaScratchDatabase _database = null!;

    private CampaignPathMarkerLifecycle _lifecycle = null!;

    public async Task InitializeAsync()
    {

        _database = await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        await _database.InstallCoreObjectsAsync(
            [
                "campaign_path_marker_intents",
                "campaign_path_marker_intents_guard_insert",
                "campaign_path_marker_intents_guard_update",
                "campaign_path_marker_intents_guard_delete",
            ],
            CancellationToken.None);

        _lifecycle = new CampaignPathMarkerLifecycle(
            _codec,
            new FixedCovenantConnectionSource(_database.Connection),
            CovenantSqliteConnectionInitializer.Instance,
            TimeProvider.System);

    }

    [Fact]
    public async Task A_zero_seed_preparation_returns_the_frozen_receipt_without_touching_storage()
    {

        CovenantExclusiveRecoveryOwner owner = Owner();

        // No connection and no transaction: a zero inventory must not need either, because the whole
        // point of the arm is that it opens nothing.
        Result<CampaignPathRestoreCleanupPreparationReceipt> prepared =
            await _lifecycle.PrepareRestoreCleanupInStagedDatabaseAsync(
                new CampaignPathRestoreCleanupPreparation(owner, ImmutableArray<CampaignPathRestoreCleanupSeed>.Empty),
                stagedConnection: null!,
                stagedTransaction: null!,
                CancellationToken.None);

        Assert.True(prepared.IsSuccess, Describe(prepared));

        Assert.Equal(owner, prepared.Value.Owner);
        Assert.Empty(prepared.Value.OrderedIntentIds);
        Assert.Equal(0UL, prepared.Value.IntentCount);
        Assert.Equal(CampaignPathRestoreCleanupIntentVector.EmptyDigest, prepared.Value.IntentVectorDigest);

        Assert.Equal(0, await CountIntentsAsync());

    }

    [Fact]
    public async Task An_uninitialized_seed_vector_is_not_a_proven_empty_inventory()
    {

        Result<CampaignPathRestoreCleanupPreparationReceipt> prepared =
            await _lifecycle.PrepareRestoreCleanupInStagedDatabaseAsync(
                new CampaignPathRestoreCleanupPreparation(Owner(), default),
                _database.Connection,
                await BeginAsync(),
                CancellationToken.None);

        Assert.True(prepared.IsFailure);
        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, prepared.Error.Code);

    }

    [Fact]
    public async Task A_non_restore_owner_can_never_journal_a_restore_cleanup_child()
    {

        CovenantExclusiveRecoveryOwner wrongOperation = new(
            Guid.NewGuid(),
            CovenantExclusiveOperation.CovenantReset,
            Digest(9));

        Result<CampaignPathRestoreCleanupPreparationReceipt> prepared =
            await _lifecycle.PrepareRestoreCleanupInStagedDatabaseAsync(
                new CampaignPathRestoreCleanupPreparation(
                    wrongOperation,
                    ImmutableArray<CampaignPathRestoreCleanupSeed>.Empty),
                _database.Connection,
                await BeginAsync(),
                CancellationToken.None);

        Assert.True(prepared.IsFailure);
        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, prepared.Error.Code);

    }

    [Fact]
    public async Task Preparation_commits_one_child_per_marked_campaign_inside_the_caller_transaction()
    {

        CovenantExclusiveRecoveryOwner owner = Owner();

        CampaignRoot first = await CreateMarkedRootAsync("alpha", 3);

        CampaignRoot second = await CreateMarkedRootAsync("beta", 5);

        await using SqliteTransaction transaction = await BeginAsync();

        Result<CampaignPathRestoreCleanupPreparationReceipt> prepared =
            await _lifecycle.PrepareRestoreCleanupInStagedDatabaseAsync(
                new CampaignPathRestoreCleanupPreparation(owner, [first.Seed, second.Seed]),
                _database.Connection,
                transaction,
                CancellationToken.None);

        Assert.True(prepared.IsSuccess, Describe(prepared));

        Assert.Equal(2, prepared.Value.OrderedIntentIds.Length);
        Assert.Equal(2UL, prepared.Value.IntentCount);

        Assert.Equal(
            CampaignPathRestoreCleanupIntentVector.Compute(prepared.Value.OrderedIntentIds).Value,
            prepared.Value.IntentVectorDigest);

        // Still uncommitted: the lifecycle borrows the transaction and never commits it, because only
        // the caller knows what else has to become durable at the same instant.
        await transaction.RollbackAsync(CancellationToken.None);

        Assert.Equal(0, await CountIntentsAsync());

    }

    [Fact]
    public async Task A_campaign_whose_marker_is_already_absent_contributes_no_child()
    {

        CampaignRoot marked = await CreateMarkedRootAsync("present", 2);

        CampaignRoot bare = await CreateRootAsync("absent", 4);

        await using SqliteTransaction transaction = await BeginAsync();

        Result<CampaignPathRestoreCleanupPreparationReceipt> prepared =
            await _lifecycle.PrepareRestoreCleanupInStagedDatabaseAsync(
                new CampaignPathRestoreCleanupPreparation(Owner(), [marked.Seed, bare.Seed]),
                _database.Connection,
                transaction,
                CancellationToken.None);

        Assert.True(prepared.IsSuccess, Describe(prepared));

        // One child, not two. A durable row whose only possible outcome is "there was nothing to do"
        // would need a sentinel digest standing in for "no bytes", and a sentinel is one substitution
        // away from matching a real marker.
        Assert.Single(prepared.Value.OrderedIntentIds);

        await transaction.CommitAsync(CancellationToken.None);

        Assert.Equal(1, await CountIntentsAsync());

    }

    [Fact]
    public async Task A_seed_whose_root_is_blocked_refuses_the_whole_preparation()
    {

        CampaignRoot marked = await CreateMarkedRootAsync("open", 2);

        CampaignPathRestoreCleanupSeed blocked = marked.Seed with
        {
            CampaignId = Guid.NewGuid(),

            Observation = new CampaignPathCleanupRootObservation.Unavailable(
                new CampaignPathCleanupRootBlockerEvidence(
                    CampaignPathCleanupRootBlocker.RootUnavailable,
                    Digest(4),
                    Digest(5))),
        };

        Result<CampaignPathRestoreCleanupPreparationReceipt> prepared =
            await _lifecycle.PrepareRestoreCleanupInStagedDatabaseAsync(
                new CampaignPathRestoreCleanupPreparation(Owner(), [marked.Seed, blocked]),
                _database.Connection,
                await BeginAsync(),
                CancellationToken.None);

        Assert.True(prepared.IsFailure);
        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, prepared.Error.Code);

    }

    [Fact]
    public async Task A_marker_naming_different_ownership_blocks_rather_than_being_removed()
    {

        CampaignRoot root = await CreateRootAsync("stranger", 6);

        // A well-formed marker for a Campaign this seed does not name. Removing it would destroy a
        // claim this restore holds no authority over.
        await WriteMarkerAsync(root, Guid.NewGuid(), 6);

        Result<CampaignPathRestoreCleanupPreparationReceipt> prepared =
            await _lifecycle.PrepareRestoreCleanupInStagedDatabaseAsync(
                new CampaignPathRestoreCleanupPreparation(Owner(), [root.Seed]),
                _database.Connection,
                await BeginAsync(),
                CancellationToken.None);

        Assert.True(prepared.IsFailure);
        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, prepared.Error.Code);

        Assert.True(File.Exists(MarkerPath(root)));

    }

    [Fact]
    public async Task Replay_of_the_same_owner_and_campaign_returns_the_same_child_identity()
    {

        CovenantExclusiveRecoveryOwner owner = Owner();

        CampaignRoot root = await CreateMarkedRootAsync("replayed", 7);

        await using (SqliteTransaction first = await BeginAsync())
        {

            Result<CampaignPathRestoreCleanupPreparationReceipt> once =
                await _lifecycle.PrepareRestoreCleanupInStagedDatabaseAsync(
                    new CampaignPathRestoreCleanupPreparation(owner, [root.Seed]),
                    _database.Connection,
                    first,
                    CancellationToken.None);

            Assert.True(once.IsSuccess, Describe(once));

            await first.CommitAsync(CancellationToken.None);

            await using SqliteTransaction second = await BeginAsync();

            Result<CampaignPathRestoreCleanupPreparationReceipt> again =
                await _lifecycle.PrepareRestoreCleanupInStagedDatabaseAsync(
                    new CampaignPathRestoreCleanupPreparation(owner, [root.Seed]),
                    _database.Connection,
                    second,
                    CancellationToken.None);

            Assert.True(again.IsSuccess, Describe(again));

            Assert.Equal([.. once.Value.OrderedIntentIds], again.Value.OrderedIntentIds.ToArray());
            Assert.Equal(once.Value.IntentVectorDigest, again.Value.IntentVectorDigest);

            await second.CommitAsync(CancellationToken.None);

        }

        Assert.Equal(1, await CountIntentsAsync());

    }

    [Fact]
    public async Task The_verified_zero_request_commits_without_opening_marker_storage()
    {

        CovenantExclusiveRecoveryOwner owner = Owner();

        Result<CampaignPathMarkerGateCompletion> completed =
            await _lifecycle.ReconcileGateOwnedAsync(
                new CampaignPathMarkerGateReconcileRequest(
                    owner,
                    ImmutableArray<Guid>.Empty,
                    CampaignPathRestoreCleanupIntentVector.EmptyDigest),
                new StubExclusiveLease(owner),
                CancellationToken.None);

        Assert.True(completed.IsSuccess, Describe(completed));

        Assert.Equal(CampaignPathMarkerAggregateOutcome.Committed, completed.Value.Outcome);
        Assert.Equal(CovenantExclusiveLeaseDisposition.CommitAndReopen, completed.Value.Disposition);

        // The frozen singleton itself, not a substitute that merely behaves like one.
        Assert.Same(CovenantNoOpPostDispositionFinalizer.Instance, completed.Value.Finalizer);

    }

    [Fact]
    public async Task A_zero_request_carrying_any_other_digest_keeps_admission_closed()
    {

        CovenantExclusiveRecoveryOwner owner = Owner();

        Result<CampaignPathMarkerGateCompletion> completed =
            await _lifecycle.ReconcileGateOwnedAsync(
                new CampaignPathMarkerGateReconcileRequest(
                    owner,
                    ImmutableArray<Guid>.Empty,
                    Digest(3)),
                new StubExclusiveLease(owner),
                CancellationToken.None);

        Assert.True(completed.IsFailure);
        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, completed.Error.Code);

    }

    [Fact]
    public async Task A_lease_that_does_not_carry_the_same_owner_reconciles_nothing()
    {

        CovenantExclusiveRecoveryOwner owner = Owner();

        CovenantExclusiveRecoveryOwner other = new(
            Guid.NewGuid(),
            CovenantExclusiveOperation.BackupRestore,
            Digest(8));

        Result<CampaignPathMarkerGateCompletion> completed =
            await _lifecycle.ReconcileGateOwnedAsync(
                new CampaignPathMarkerGateReconcileRequest(
                    owner,
                    ImmutableArray<Guid>.Empty,
                    CampaignPathRestoreCleanupIntentVector.EmptyDigest),
                new StubExclusiveLease(other),
                CancellationToken.None);

        Assert.True(completed.IsFailure);
        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, completed.Error.Code);

    }

    [Fact]
    public async Task Reconciliation_deletes_each_exact_marker_and_completes_only_after_disposition()
    {

        CovenantExclusiveRecoveryOwner owner = Owner();

        CampaignRoot first = await CreateMarkedRootAsync("one", 3);

        CampaignRoot second = await CreateMarkedRootAsync("two", 4);

        CampaignPathRestoreCleanupPreparationReceipt receipt =
            await PrepareAndCommitAsync(owner, [first.Seed, second.Seed]);

        Result<CampaignPathMarkerGateCompletion> completed =
            await _lifecycle.ReconcileGateOwnedAsync(
                new CampaignPathMarkerGateReconcileRequest(
                    owner,
                    receipt.OrderedIntentIds,
                    receipt.IntentVectorDigest),
                new StubExclusiveLease(owner),
                CancellationToken.None);

        Assert.True(completed.IsSuccess, Describe(completed));

        Assert.False(File.Exists(MarkerPath(first)));
        Assert.False(File.Exists(MarkerPath(second)));

        // Pending, not complete. The caller owns the lease and has not spent its one reopening
        // decision yet, so a terminal row here would record something nobody proved.
        Assert.Equal(2, await CountPhaseAsync(11));
        Assert.Equal(0, await CountPhaseAsync(12));

        Assert.True((await completed.Value.Finalizer.FinalizeAfterSuccessfulDispositionAsync(
            CovenantExclusiveLeaseDisposition.CommitAndReopen,
            CancellationToken.None)).IsSuccess);

        Assert.Equal(2, await CountPhaseAsync(12));

    }

    [Fact]
    public async Task The_marker_journal_finalizer_runs_exactly_once_and_only_for_a_commit()
    {

        CovenantExclusiveRecoveryOwner owner = Owner();

        CampaignRoot root = await CreateMarkedRootAsync("once", 2);

        CampaignPathRestoreCleanupPreparationReceipt receipt =
            await PrepareAndCommitAsync(owner, [root.Seed]);

        CampaignPathMarkerGateCompletion completion =
            (await _lifecycle.ReconcileGateOwnedAsync(
                new CampaignPathMarkerGateReconcileRequest(
                    owner,
                    receipt.OrderedIntentIds,
                    receipt.IntentVectorDigest),
                new StubExclusiveLease(owner),
                CancellationToken.None)).Value;

        Assert.True((await completion.Finalizer.FinalizeAfterSuccessfulDispositionAsync(
            CovenantExclusiveLeaseDisposition.KeepClosed,
            CancellationToken.None)).IsFailure);

        Assert.Equal(0, await CountPhaseAsync(12));

    }

    [Fact]
    public async Task A_marker_that_changed_after_preparation_is_left_exactly_where_it_is()
    {

        CovenantExclusiveRecoveryOwner owner = Owner();

        CampaignRoot root = await CreateMarkedRootAsync("tampered", 3);

        CampaignPathRestoreCleanupPreparationReceipt receipt =
            await PrepareAndCommitAsync(owner, [root.Seed]);

        // Same Campaign and revision, different per-registration secret: a well-formed marker that is
        // not the one this restore committed evidence for.
        File.Delete(MarkerPath(root));

        await WriteMarkerAsync(root, root.Seed.CampaignId, root.Seed.PriorPathRevision, secretSeed: 0x99);

        Result<CampaignPathMarkerGateCompletion> completed =
            await _lifecycle.ReconcileGateOwnedAsync(
                new CampaignPathMarkerGateReconcileRequest(
                    owner,
                    receipt.OrderedIntentIds,
                    receipt.IntentVectorDigest),
                new StubExclusiveLease(owner),
                CancellationToken.None);

        Assert.True(completed.IsFailure);
        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, completed.Error.Code);

        Assert.True(File.Exists(MarkerPath(root)));

    }

    [Fact]
    public async Task Reconciliation_that_names_a_different_vector_than_it_carries_is_refused()
    {

        CovenantExclusiveRecoveryOwner owner = Owner();

        CampaignRoot root = await CreateMarkedRootAsync("mismatch", 3);

        CampaignPathRestoreCleanupPreparationReceipt receipt =
            await PrepareAndCommitAsync(owner, [root.Seed]);

        Result<CampaignPathMarkerGateCompletion> completed =
            await _lifecycle.ReconcileGateOwnedAsync(
                new CampaignPathMarkerGateReconcileRequest(
                    owner,
                    receipt.OrderedIntentIds,
                    Digest(6)),
                new StubExclusiveLease(owner),
                CancellationToken.None);

        Assert.True(completed.IsFailure);
        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, completed.Error.Code);

        Assert.True(File.Exists(MarkerPath(root)));

    }

    public async Task DisposeAsync()
    {

        if (_database is not null)
        {

            await _database.DisposeAsync();

        }

    }

    public void Dispose()
    {

        try
        {
            Directory.Delete(_parent, recursive: true);
        }
        catch (IOException)
        {
            // A leftover scratch directory is not worth failing a suite over.
        }

    }

    private static CovenantExclusiveRecoveryOwner Owner() =>
        new(Guid.NewGuid(), CovenantExclusiveOperation.BackupRestore, Digest(7));

    private static CovenantDigest Digest(byte seed) =>
        new([.. Enumerable.Repeat(seed, 32)]);

    private static string Describe<T>(Result<T> result) =>
        result.IsFailure ? $"{result.Error.Code}: {result.Error.Message}" : string.Empty;

    private static string MarkerPath(CampaignRoot root) =>
        Path.Combine(root.Directory, ".arcanum", "campaign-root.marker");

    private async Task<CampaignPathRestoreCleanupPreparationReceipt> PrepareAndCommitAsync(
        CovenantExclusiveRecoveryOwner owner,
        ImmutableArray<CampaignPathRestoreCleanupSeed> seeds)
    {

        await using SqliteTransaction transaction = await BeginAsync();

        Result<CampaignPathRestoreCleanupPreparationReceipt> prepared =
            await _lifecycle.PrepareRestoreCleanupInStagedDatabaseAsync(
                new CampaignPathRestoreCleanupPreparation(owner, seeds),
                _database.Connection,
                transaction,
                CancellationToken.None);

        Assert.True(prepared.IsSuccess, Describe(prepared));

        await transaction.CommitAsync(CancellationToken.None);

        return prepared.Value;

    }

    private async Task<SqliteTransaction> BeginAsync() =>
        (SqliteTransaction)await _database.Connection.BeginTransactionAsync(
            CancellationToken.None);

    private async Task<long> CountIntentsAsync() =>
        await _database.ScalarLongAsync(
            "SELECT COUNT(*) FROM campaign_path_marker_intents;",
            CancellationToken.None);

    private async Task<long> CountPhaseAsync(int phaseCode) =>
        await _database.ScalarLongAsync(
            $"SELECT COUNT(*) FROM campaign_path_marker_intents WHERE PhaseCode = {phaseCode};",
            CancellationToken.None);

    private async Task<CampaignRoot> CreateRootAsync(string name, long revision)
    {

        string directory = Directory.CreateDirectory(Path.Combine(_parent, name)).FullName;

        Guid campaignId = Guid.NewGuid();

        CovenantDigest identity = _opener.IdentifyExact(directory)!.Value;

        Result<CampaignPathMarkerRootAuthority> opened =
            await CampaignPathMarkerRootAuthority.Instance.OpenAsync(
                _opener,
                campaignId,
                revision,
                identity,
                directory,
                CancellationToken.None);

        Assert.True(opened.IsSuccess, Describe(opened));

        return new CampaignRoot(
            directory,
            new CampaignPathRestoreCleanupSeed(
                campaignId,
                revision,
                identity,
                directory,
                new CampaignPathCleanupRootObservation.Opened(opened.Value)));

    }

    private async Task<CampaignRoot> CreateMarkedRootAsync(string name, long revision)
    {

        CampaignRoot root = await CreateRootAsync(name, revision);

        await WriteMarkerAsync(root, root.Seed.CampaignId, revision);

        return root;

    }

    /// <summary>
    /// Writes one authentic marker through the same codec and retained handles production uses.
    /// </summary>
    private async Task WriteMarkerAsync(
        CampaignRoot root,
        Guid campaignId,
        long revision,
        byte secretSeed = 0x33)
    {

        CampaignPathMarkerRootAuthority authority =
            ((CampaignPathCleanupRootObservation.Opened)root.Seed.Observation).RootAuthority;

        (ulong volumeId, ulong fileId) = ReadRootTuple(root.Directory);

        Result<byte[]> encoded = _codec.Encode(
            new CampaignPathMarkerContent(
                CampaignPathMarkerPolicy.Version,
                campaignId,
                revision,
                volumeId,
                fileId,
                [.. Enumerable.Repeat(secretSeed, CampaignPathMarkerPolicy.MarkerSecretByteCount)]));

        Assert.True(encoded.IsSuccess, Describe(encoded));

        string leaf = $"marker-{Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}.tmp";

        PhysicalCampaignRootOpener.MarkerTemporaryHandleCapability temporary =
            (await authority.CreateTemporaryExclusiveNoFollowAsync(
                leaf,
                CancellationToken.None)).Value;

        Assert.True((await temporary.WriteAllAsync(
            encoded.Value,
            CancellationToken.None)).IsSuccess);

        Assert.True((await temporary.FlushToDiskAsync(CancellationToken.None)).IsSuccess);

        Assert.True((await authority.RenameTemporaryToMarkerNoReplaceAsync(
            temporary,
            temporary.PhysicalIdentityDigest,
            encoded.Value,
            CancellationToken.None)).IsSuccess);

    }

    /// <summary>
    /// The root's own volume and file identifiers, which the marker binds itself to.
    /// </summary>
    private static (ulong VolumeId, ulong FileId) ReadRootTuple(string directory)
    {

        Assert.True(FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
            directory,
            out FileHandleMetadata metadata));

        return (metadata.Identity.VolumeId, metadata.Identity.FileId);

    }

    private sealed record CampaignRoot(string Directory, CampaignPathRestoreCleanupSeed Seed);

    private sealed class StubKeySource(byte[] key) : ICampaignRootIdentityKeyProvider
    {

        public bool TryCopyRootIdentityKey(Span<byte> destination)
        {

            if (destination.Length < key.Length)
            {
                return false;
            }

            key.CopyTo(destination);

            return true;

        }

    }

    /// <summary>
    /// A lease the caller already holds. Only its snapshot matters here: the lifecycle is required to
    /// prove the lease closes this exact owner's scope before it touches a marker.
    /// </summary>
    private sealed class StubExclusiveLease(CovenantExclusiveRecoveryOwner owner)
        : ICovenantExclusiveOperationLease
    {

        public CovenantOperationLeaseSnapshot Snapshot { get; } = new(
            Guid.NewGuid(),
            CovenantLeaseKind.Exclusive,
            CovenantLeaseCoverage.Installation,
            null,
            Guid.NewGuid(),
            1,
            1,
            1,
            null,
            null,
            null,
            null,
            owner,
            false);

        public CancellationToken Revocation => CancellationToken.None;

        public ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success());

        public ValueTask<Result> CompleteAsync(
            CovenantExclusiveLeaseDisposition disposition,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    }

}
