using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.Backup;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Covenant;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Secrets.Security;

using RetroDownfall.Arcanum.Tests.Covenant;

namespace RetroDownfall.Arcanum.Tests.Backup;

/// <summary>
/// Where one restore died between the two renames that swap an installation.
/// </summary>
/// <remarks>
/// Every case here is a machine that lost power inside the displacement window. The journal is the
/// only evidence left, and a restart believes it <em>before</em> any database opens — so the two
/// phases have to answer different questions. Physical topology recovery converges the tree to exactly
/// one journal-selected live root and authorizes nothing; authority recovery resumes the exact
/// exclusive owner and is the only thing that may reopen admission (§10.19.8).
/// </remarks>
public sealed class BackupRestoreStartupRecoveryTests : IDisposable
{

    /// <summary>The four topology states a killed restore can leave inside the commit window.</summary>
    public enum RestoreCrashPoint
    {

        BeforeLiveRootDisplacement = 0,

        AfterLiveRootRenamedToRollback = 1,

        AfterStagedRootRenamedToLive = 2,

        AfterLiveParentFsync = 3,

    }

    private static readonly Guid OperationId = new("66666666-6666-4666-8666-666666666666");

    private static CancellationToken Token => CancellationToken.None;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "arcanum-restore-startup-" + Guid.NewGuid().ToString("N"));

    private readonly InMemoryOsCredentialStore _credentials = new();

    private readonly string _guarded;

    private readonly ArcanumMaintenanceLock _lock;

    private readonly Guid _installationId = Guid.NewGuid();

    public BackupRestoreStartupRecoveryTests()
    {

        Directory.CreateDirectory(_root);

        _guarded = Path.Combine(_root, "arcanum");

        _lock = ArcanumMaintenanceLock.TryAcquire(_guarded)
            ?? throw new InvalidOperationException("The test could not take its own maintenance lock.");

    }

    public void Dispose()
    {

        _lock.Dispose();

        if (Directory.Exists(_root))
        {

            Directory.Delete(_root, recursive: true);

        }

    }

    // ---------------------------------------------------------------- physical topology

    [Theory]
    [InlineData(RestoreCrashPoint.BeforeLiveRootDisplacement, "live")]
    [InlineData(RestoreCrashPoint.AfterLiveRootRenamedToRollback, "live")]
    [InlineData(RestoreCrashPoint.AfterStagedRootRenamedToLive, "staged")]
    [InlineData(RestoreCrashPoint.AfterLiveParentFsync, "staged")]
    public async Task Physical_recovery_converges_each_displacement_crash_point_to_one_live_root(
        RestoreCrashPoint crash,
        string expectedTree)
    {

        Interrupted interrupted = Interrupt(crash);

        Result<BackupRestorePhysicalRecoveryOutcome> recovered = await Recovery()
            .RecoverPhysicalTopologyBeforeDatabaseAsync(_lock, Token);

        Assert.True(recovered.IsSuccess);

        Assert.Equal(BackupRestorePhysicalRecoveryOutcome.TopologyReady, recovered.Value);

        // Exactly one live root, and it is the one the journal selects: the prior installation before
        // the staged tree was renamed into place, and the restored tree after it.
        Assert.Equal(expectedTree, File.ReadAllText(Path.Combine(_guarded, "marker.txt")));

        bool preSwap = expectedTree == "live";

        // The tree that is not live is exactly where the displacement left it. A rolled-back restore
        // holds its staged generation and no rollback artifact; a swapped one holds the rollback
        // artifact and no staged generation.
        Assert.Equal(preSwap, Directory.Exists(interrupted.StagedRoot));

        Assert.Equal(!preSwap, Directory.Exists(interrupted.DisplacedRoot));

        // Neither the journal nor its anchor is consumed here. Physical convergence is not a decision
        // about authority, and the second phase still has to authenticate the same evidence.
        Assert.True(File.Exists(interrupted.JournalPath));

        Assert.Equal(BackupRestoreJournalAnchorState.Active, ReadAnchor(interrupted).State);

    }

    [Fact]
    public async Task Physical_recovery_refuses_a_live_root_that_is_not_the_one_the_journal_names()
    {

        Interrupted interrupted = Interrupt(RestoreCrashPoint.AfterLiveRootRenamedToRollback);

        // A directory with the recorded name but a different durable identity. Renaming it back would
        // publish a tree this restore never displaced.
        Directory.CreateDirectory(_guarded);

        File.WriteAllText(Path.Combine(_guarded, "marker.txt"), "impostor");

        Result<BackupRestorePhysicalRecoveryOutcome> recovered = await Recovery()
            .RecoverPhysicalTopologyBeforeDatabaseAsync(_lock, Token);

        Assert.True(recovered.IsSuccess);

        Assert.Equal(BackupRestorePhysicalRecoveryOutcome.KeptClosed, recovered.Value);

        Assert.Equal("impostor", File.ReadAllText(Path.Combine(_guarded, "marker.txt")));

        Assert.True(Directory.Exists(interrupted.DisplacedRoot));

    }

    [Fact]
    public async Task Physical_recovery_refuses_a_staged_root_that_is_not_the_one_the_journal_names()
    {

        Interrupted interrupted = Interrupt(RestoreCrashPoint.BeforeLiveRootDisplacement);

        // Allocated while the journaled directory is still alive, so the substitute cannot inherit the
        // identity it is replacing.
        string substitute = Path.Combine(interrupted.StagingRoot, "substitute");

        Directory.CreateDirectory(substitute);

        Directory.Delete(interrupted.StagedRoot, recursive: true);

        Directory.Move(substitute, interrupted.StagedRoot);

        Result<BackupRestorePhysicalRecoveryOutcome> recovered = await Recovery()
            .RecoverPhysicalTopologyBeforeDatabaseAsync(_lock, Token);

        Assert.True(recovered.IsSuccess);

        Assert.Equal(BackupRestorePhysicalRecoveryOutcome.KeptClosed, recovered.Value);

    }

    [Fact]
    public async Task Physical_recovery_returns_no_active_journal_only_for_proven_absence()
    {

        Directory.CreateDirectory(_guarded);

        Result<BackupRestorePhysicalRecoveryOutcome> recovered = await Recovery()
            .RecoverPhysicalTopologyBeforeDatabaseAsync(_lock, Token);

        Assert.True(recovered.IsSuccess);

        Assert.Equal(BackupRestorePhysicalRecoveryOutcome.NoActiveJournal, recovered.Value);

    }

    [Fact]
    public async Task Physical_recovery_keeps_startup_closed_for_a_canonical_journal_with_no_anchor()
    {

        Directory.CreateDirectory(_guarded);

        string lookalike = Path.Combine(_root, BackupRestoreJournal.CreateStagingName());

        Directory.CreateDirectory(lookalike);

        // A canonical staging name is a bare random suffix anyone can create, so the file beneath it
        // carries no authority — but it is evidence, and evidence nothing commits to is never absence.
        File.WriteAllText(
            Path.Combine(lookalike, BackupRestoreJournalAnchorStore.JournalFileName),
            "{}");

        Result<BackupRestorePhysicalRecoveryOutcome> recovered = await Recovery()
            .RecoverPhysicalTopologyBeforeDatabaseAsync(_lock, Token);

        Assert.True(recovered.IsSuccess);

        Assert.Equal(BackupRestorePhysicalRecoveryOutcome.KeptClosed, recovered.Value);

    }

    /// <summary>
    /// A new-profile restore stages beside its destination, where the canonical sweep cannot reach it.
    /// </summary>
    [Fact]
    public async Task Physical_recovery_keeps_startup_closed_for_an_indexed_journal_it_cannot_authenticate()
    {

        Directory.CreateDirectory(_guarded);

        string elsewhere = Path.Combine(_root, "another-destination");

        string staging = Path.Combine(elsewhere, BackupRestoreJournal.CreateStagingName());

        Directory.CreateDirectory(staging);

        File.WriteAllText(
            Path.Combine(staging, BackupRestoreJournalAnchorStore.JournalFileName),
            "{}");

        BackupRestoreStagingIndex.Add(_guarded, staging);

        Result<BackupRestorePhysicalRecoveryOutcome> recovered = await Recovery()
            .RecoverPhysicalTopologyBeforeDatabaseAsync(_lock, Token);

        Assert.True(recovered.IsSuccess);

        Assert.Equal(BackupRestorePhysicalRecoveryOutcome.KeptClosed, recovered.Value);

    }

    [Fact]
    public async Task Physical_recovery_keeps_startup_closed_when_the_active_anchor_names_no_swept_root()
    {

        Interrupted interrupted = Interrupt(RestoreCrashPoint.BeforeLiveRootDisplacement);

        // The anchor still says an operation is in flight; the location it commits to is gone.
        Directory.Delete(interrupted.StagingRoot, recursive: true);

        Result<BackupRestorePhysicalRecoveryOutcome> recovered = await Recovery()
            .RecoverPhysicalTopologyBeforeDatabaseAsync(_lock, Token);

        Assert.True(recovered.IsSuccess);

        Assert.Equal(BackupRestorePhysicalRecoveryOutcome.KeptClosed, recovered.Value);

    }

    [Fact]
    public async Task Physical_recovery_alone_never_authorizes_admission()
    {

        Interrupted interrupted = Interrupt(RestoreCrashPoint.AfterStagedRootRenamedToLive);

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        Result<BackupRestorePhysicalRecoveryOutcome> recovered = await Recovery(gate)
            .RecoverPhysicalTopologyBeforeDatabaseAsync(_lock, Token);

        Assert.Equal(BackupRestorePhysicalRecoveryOutcome.TopologyReady, recovered.Value);

        // No closure was adopted, so nothing in this process may resume the restore's scope yet, and
        // the replacement has been proven healthy by nobody.
        Result<CovenantExclusiveLease> resumed = await gate.ResumeExclusiveAsync(
            interrupted.Owner,
            Token);

        Assert.True(resumed.IsFailure);

        Assert.Equal(BackupRestoreJournalAnchorState.Active, ReadAnchor(interrupted).State);

    }

    // ---------------------------------------------------------------- authority

    [Fact]
    public async Task Authority_recovery_returns_no_active_journal_when_nothing_is_in_flight()
    {

        Directory.CreateDirectory(_guarded);

        Result<BackupRestoreStartupRecoveryOutcome> recovered = await Recovery()
            .RecoverAuthorityBeforeReadinessAsync(_lock, Token);

        Assert.True(recovered.IsSuccess);

        Assert.Equal(BackupRestoreStartupRecoveryOutcome.NoActiveJournal, recovered.Value);

    }

    [Fact]
    public async Task Authority_recovery_rolls_back_a_proven_pre_swap_restore_under_the_resumed_owner()
    {

        Interrupted interrupted = Interrupt(RestoreCrashPoint.AfterLiveRootRenamedToRollback);

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        FakeCampaignPathMarkerLifecycle markers = new();

        BackupRestoreRecovery recovery = Recovery(gate, markers);

        Assert.Equal(
            BackupRestorePhysicalRecoveryOutcome.TopologyReady,
            Value(await recovery.RecoverPhysicalTopologyBeforeDatabaseAsync(
                _lock,
                Token)));

        Result<BackupRestoreStartupRecoveryOutcome> recovered = await recovery
            .RecoverAuthorityBeforeReadinessAsync(_lock, Token);

        Assert.True(recovered.IsSuccess);

        Assert.Equal(BackupRestoreStartupRecoveryOutcome.RecoveredReady, recovered.Value);

        // Nothing durable had happened to the installation, so the prior tree stands and no marker
        // child was ever prepared to reconcile.
        Assert.Equal("live", File.ReadAllText(Path.Combine(_guarded, "marker.txt")));

        Assert.Equal(0, markers.ReconcileCalls);

        Assert.False(Directory.Exists(interrupted.StagingRoot));

        Assert.Equal(BackupRestoreJournalAnchorState.Closed, ReadAnchor(interrupted).State);

        // The rollback reopened admission, so the closed scope is gone rather than merely unheld.
        Result<CovenantExclusiveLease> resumed = await gate.ResumeExclusiveAsync(
            interrupted.Owner,
            Token);

        Assert.True(resumed.IsFailure);

    }

    [Fact]
    public async Task Authority_recovery_commits_a_post_swap_restore_only_through_the_authenticated_children()
    {

        ImmutableArray<Guid> children =
        [
            new Guid("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
            new Guid("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"),
        ];

        Interrupted interrupted = Interrupt(RestoreCrashPoint.AfterStagedRootRenamedToLive, children);

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        FakeCampaignPathMarkerLifecycle markers = new();

        BackupRestoreRecovery recovery = Recovery(gate, markers);

        _ = Value(await recovery.RecoverPhysicalTopologyBeforeDatabaseAsync(
            _lock,
            Token));

        Result<BackupRestoreStartupRecoveryOutcome> recovered = await recovery
            .RecoverAuthorityBeforeReadinessAsync(_lock, Token);

        Assert.True(recovered.IsSuccess);

        Assert.Equal(BackupRestoreStartupRecoveryOutcome.RecoveredReady, recovered.Value);

        Assert.Equal("staged", File.ReadAllText(Path.Combine(_guarded, "marker.txt")));

        // The exact authenticated vector, owner and all: a reconciliation that could be handed a
        // shorter list would abandon a marker nothing else will ever adopt.
        Assert.NotNull(markers.LastRequest);

        Assert.Equal(interrupted.Owner, markers.LastRequest!.Owner);

        Assert.Equal<IEnumerable<Guid>>(children, markers.LastRequest.OrderedIntentIds);

        Assert.Equal(
            Value(CampaignPathRestoreCleanupIntentVector.Compute(children)),
            markers.LastRequest.IntentVectorDigest);

        // The finalizer is the lifecycle's, spent exactly once and only after the disposition it was
        // handed actually succeeded.
        Assert.Equal(1, markers.Finalizer.Invocations);

        Assert.Equal(
            CovenantExclusiveLeaseDisposition.CommitAndReopen,
            markers.Finalizer.ObservedDisposition);

        Assert.Equal(OperationId, markers.ReleasedOwnerOperationId);

        Assert.Equal(BackupRestoreJournalAnchorState.Closed, ReadAnchor(interrupted).State);

        Assert.False(Directory.Exists(interrupted.StagingRoot));

    }

    [Fact]
    public async Task Authority_recovery_keeps_admission_closed_for_a_mismatched_owner()
    {

        Interrupted interrupted = Interrupt(RestoreCrashPoint.AfterStagedRootRenamedToLive);

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        // The same operation identity with a different effect digest is a different operation, and it
        // must never adopt this one's closed scope.
        gate.AdoptDurableRecoveryOwner(
            new CovenantExclusiveRecoveryOwner(
                interrupted.Owner.OperationId,
                CovenantExclusiveOperation.BackupRestore,
                CovenantOperationGateFixture.Digest(99)),
            scope: null,
            cleanupOnlyHistoricalCampaign: false);

        FakeCampaignPathMarkerLifecycle markers = new();

        BackupRestoreRecovery recovery = Recovery(gate, markers);

        _ = Value(await recovery.RecoverPhysicalTopologyBeforeDatabaseAsync(
            _lock,
            Token));

        Result<BackupRestoreStartupRecoveryOutcome> recovered = await recovery
            .RecoverAuthorityBeforeReadinessAsync(_lock, Token);

        Assert.True(recovered.IsSuccess);

        Assert.Equal(BackupRestoreStartupRecoveryOutcome.KeptClosed, recovered.Value);

        Assert.Equal(0, markers.ReconcileCalls);

        Assert.True(File.Exists(interrupted.JournalPath));

        Assert.Equal(BackupRestoreJournalAnchorState.Active, ReadAnchor(interrupted).State);

    }

    [Fact]
    public async Task Authority_recovery_keeps_admission_closed_when_the_children_cannot_be_proven()
    {

        Interrupted interrupted = Interrupt(RestoreCrashPoint.AfterStagedRootRenamedToLive);

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        FakeCampaignPathMarkerLifecycle markers = new()
        {
            Failure = new Error(
                ErrorCodes.Covenant.ManualRecoveryRequired,
                "A marker child could not be proven after a restart."),
        };

        BackupRestoreRecovery recovery = Recovery(gate, markers);

        _ = Value(await recovery.RecoverPhysicalTopologyBeforeDatabaseAsync(
            _lock,
            Token));

        Result<BackupRestoreStartupRecoveryOutcome> recovered = await recovery
            .RecoverAuthorityBeforeReadinessAsync(_lock, Token);

        Assert.True(recovered.IsSuccess);

        Assert.Equal(BackupRestoreStartupRecoveryOutcome.KeptClosed, recovered.Value);

        // Nothing else in this process will adopt the roots the restart proof reopened for children it
        // could not finish, so the one release that covers both arms runs on this path too.
        Assert.Equal(OperationId, markers.ReleasedOwnerOperationId);

        // Post-swap uncertainty leaves the journal active and the staged evidence in place: the next
        // start has to be able to try again.
        Assert.True(File.Exists(interrupted.JournalPath));

        Assert.Equal(BackupRestoreJournalAnchorState.Active, ReadAnchor(interrupted).State);

        Assert.Equal("staged", File.ReadAllText(Path.Combine(_guarded, "marker.txt")));

    }

    [Fact]
    public async Task Authority_recovery_returns_recovered_ready_only_after_a_successful_commit_and_reopen()
    {

        Interrupted interrupted = Interrupt(RestoreCrashPoint.AfterStagedRootRenamedToLive);

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        // A reconciliation that reached no commit is not a restore that finished.
        FakeCampaignPathMarkerLifecycle markers = new()
        {
            Disposition = CovenantExclusiveLeaseDisposition.KeepClosed,

            Outcome = CampaignPathMarkerAggregateOutcome.Orphaned,
        };

        BackupRestoreRecovery recovery = Recovery(gate, markers);

        _ = Value(await recovery.RecoverPhysicalTopologyBeforeDatabaseAsync(
            _lock,
            Token));

        Result<BackupRestoreStartupRecoveryOutcome> recovered = await recovery
            .RecoverAuthorityBeforeReadinessAsync(_lock, Token);

        Assert.True(recovered.IsSuccess);

        Assert.Equal(BackupRestoreStartupRecoveryOutcome.KeptClosed, recovered.Value);

        Assert.Equal(BackupRestoreJournalAnchorState.Active, ReadAnchor(interrupted).State);

    }

    // ---------------------------------------------------------------- fixture

    private BackupRestoreRecovery Recovery(
        CovenantOperationGate? gate = null,
        ICampaignPathMarkerLifecycle? markers = null) =>
        new(
            _guarded,
            new BackupRestoreJournalAnchorStore(
                _credentials,
                new BackupRestoreJournalKeyProvider(_credentials),
                new BackupRestoreJournalInstallationIdentityProvider(_credentials)),
            gate,
            markers);

    private Interrupted Interrupt(RestoreCrashPoint crash) =>
        Interrupt(crash, ImmutableArray<Guid>.Empty);

    private Interrupted Interrupt(RestoreCrashPoint crash, ImmutableArray<Guid> children)
    {

        BackupRestoreProfileNamespace profile = Value(
            BackupRestoreJournalAuthenticator.ResolveProfileNamespace(_guarded));

        BackupRestoreJournalInstallationIdentityProvider identities = new(_credentials);

        _ = Value(identities.SeedFromDatabase(_lock, _guarded, profile, _installationId));

        Value(new BackupRestoreJournalKeyProvider(_credentials)
            .CreateOrOpen(_lock, _guarded, profile))
            .Dispose();

        Write(_guarded, "live");

        string stagingRoot = Path.Combine(_root, BackupRestoreJournal.CreateStagingName());

        Directory.CreateDirectory(stagingRoot);

        string stagedRoot = Path.Combine(stagingRoot, BackupRestoreJournal.StagedDirectoryName);

        string displacedRoot = Path.Combine(stagingRoot, BackupRestoreJournal.DisplacedDirectoryName);

        Write(stagedRoot, "staged");

        BackupRestoreJournalPayloadV2 payload = new(
            OperationId,
            CovenantExclusiveOperation.BackupRestore,
            EffectDigest,
            BackupRestoreConflictMode.ReplaceInstallation,
            BackupRestorePhase.Commit,
            Digest(19),
            Node(_root, "arcanum", present: true),
            Node(stagingRoot, BackupRestoreJournal.StagedDirectoryName, present: true),
            Node(stagingRoot, BackupRestoreJournal.DisplacedDirectoryName, present: false),
            new BackupRestoreDurableNodeIdentityV1(
                _root,
                Identity(_root),
                "source.arcbackup",
                BackupRestoreNodeKind.RegularFile,
                BackupRestoreNodePresence.Present,
                Digest(16),
                Digest(17)),
            null,
            new BackupRestoreMarkerCleanupCheckpointV1(
                1,
                OperationId,
                CovenantExclusiveOperation.BackupRestore,
                EffectDigest,
                children,
                checked((ulong)children.Length),
                Value(CampaignPathRestoreCleanupIntentVector.Compute(children))));

        BackupRestoreJournalAnchorStore anchors = new(
            _credentials,
            new BackupRestoreJournalKeyProvider(_credentials),
            identities);

        BackupRestoreJournalLocation location = Value(
            anchors.ResolveLocation(profile, _installationId, OperationId, stagingRoot));

        _ = Value(anchors.Begin(_lock, _guarded, profile, location, payload));

        BackupRestoreStagingIndex.Add(_guarded, stagingRoot);

        if (crash is not RestoreCrashPoint.BeforeLiveRootDisplacement)
        {

            Directory.Move(_guarded, displacedRoot);

        }

        if (crash is RestoreCrashPoint.AfterStagedRootRenamedToLive
            or RestoreCrashPoint.AfterLiveParentFsync)
        {

            Directory.Move(stagedRoot, _guarded);

        }

        return new Interrupted(
            stagingRoot,
            stagedRoot,
            displacedRoot,
            Path.Combine(stagingRoot, BackupRestoreJournalAnchorStore.JournalFileName),
            profile,
            new CovenantExclusiveRecoveryOwner(
                OperationId,
                CovenantExclusiveOperation.BackupRestore,
                EffectDigest));

    }

    private BackupRestoreJournalAnchorV1 ReadAnchor(Interrupted interrupted)
    {

        BackupRestoreJournalAnchorStore anchors = new(
            _credentials,
            new BackupRestoreJournalKeyProvider(_credentials),
            new BackupRestoreJournalInstallationIdentityProvider(_credentials));

        BackupRestoreJournalAnchorV1? anchor = Value(anchors.TryReadAnchor(interrupted.Profile));

        Assert.NotNull(anchor);

        return anchor;

    }

    private static BackupRestoreDurableNodeIdentityV1 Node(string parent, string leaf, bool present) =>
        new(
            parent,
            Identity(parent),
            leaf,
            BackupRestoreNodeKind.Directory,
            present ? BackupRestoreNodePresence.Present : BackupRestoreNodePresence.Absent,
            present ? Identity(Path.Combine(parent, leaf)) : null,
            null);

    private static CovenantDigest Identity(string path)
    {

        Assert.True(
            FileHandleIdentityInterop.TryGetPathMetadataNoFollow(path, out FileHandleMetadata metadata),
            "The test fixture could not read the durable identity of " + path);

        return BackupRestoreJournalAuthenticator.PhysicalIdentity(
            metadata.Identity.VolumeId,
            metadata.Identity.FileId);

    }

    private static void Write(string directory, string marker)
    {

        Directory.CreateDirectory(directory);

        File.WriteAllText(Path.Combine(directory, "marker.txt"), marker);

    }

    private static CovenantDigest EffectDigest => Digest(18);

    private static CovenantDigest Digest(byte seed) =>
        new([.. Enumerable.Repeat(seed, 32)]);

    private static T Value<T>(Result<T> result)
    {

        Assert.True(
            result.IsSuccess,
            result.IsFailure ? result.Error.Code + ": " + result.Error.Message : string.Empty);

        return result.Value;

    }

    private sealed record Interrupted(
        string StagingRoot,
        string StagedRoot,
        string DisplacedRoot,
        string JournalPath,
        BackupRestoreProfileNamespace Profile,
        CovenantExclusiveRecoveryOwner Owner);

}

/// <summary>
/// A marker lifecycle that records what a restarted restore asked it to reconcile.
/// </summary>
/// <remarks>
/// The real lifecycle needs a live SQLCipher connection and retained no-follow roots; what this suite
/// asserts is the seam above it — that recovery hands over the exact authenticated child vector and
/// spends the returned disposition once, rather than deciding for itself what the marker protocol did.
/// </remarks>
internal sealed class FakeCampaignPathMarkerLifecycle : ICampaignPathMarkerLifecycle
{

    internal int ReconcileCalls { get; private set; }

    internal CampaignPathMarkerGateReconcileRequest? LastRequest { get; private set; }

    internal Guid? ReleasedOwnerOperationId { get; private set; }

    internal RecordingPostDispositionFinalizer Finalizer { get; } = new();

    internal CovenantExclusiveLeaseDisposition Disposition { get; init; } =
        CovenantExclusiveLeaseDisposition.CommitAndReopen;

    internal CampaignPathMarkerAggregateOutcome Outcome { get; init; } =
        CampaignPathMarkerAggregateOutcome.Committed;

    internal Error? Failure { get; init; }

    public Task<Result<CampaignPathRestoreCleanupPreparationReceipt>> PrepareRestoreCleanupInStagedDatabaseAsync(
        CampaignPathRestoreCleanupPreparation preparation,
        Microsoft.Data.Sqlite.SqliteConnection stagedConnection,
        Microsoft.Data.Sqlite.SqliteTransaction stagedTransaction,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Startup recovery never prepares a restore cleanup.");

    public Task<Result<CampaignPathMarkerGateCompletion>> ReconcileGateOwnedAsync(
        CampaignPathMarkerGateReconcileRequest request,
        ICovenantExclusiveOperationLease exclusiveLease,
        CancellationToken cancellationToken)
    {

        ReconcileCalls++;

        LastRequest = request;

        return Task.FromResult(
            Failure is { } failure
                ? Result<CampaignPathMarkerGateCompletion>.Failure(failure)
                : Result<CampaignPathMarkerGateCompletion>.Success(
                    new CampaignPathMarkerGateCompletion(Outcome, Disposition, Finalizer)));

    }

    public ValueTask ReleaseRetainedRootsAsync(Guid ownerOperationId)
    {

        ReleasedOwnerOperationId = ownerOperationId;

        return ValueTask.CompletedTask;

    }

}
