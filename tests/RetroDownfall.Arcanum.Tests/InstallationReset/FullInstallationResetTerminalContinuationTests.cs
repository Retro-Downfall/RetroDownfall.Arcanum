using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

/// <summary>
/// The last authorized step of an attested full installation reset.
/// </summary>
/// <remarks>
/// The negative arms are the substance. Removing the three restore credentials is the one action in
/// the product that destroys the only evidence capable of finishing an interrupted restore, so every
/// test here is really asking the same question: is there any shape short of "the managed files are
/// accounted for and the database is genuinely gone" that still gets them removed.
/// </remarks>
public sealed class FullInstallationResetTerminalContinuationTests : IDisposable
{

    private static CancellationToken Token => CancellationToken.None;

    private static readonly Guid InstallationId =
        Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");

    private static readonly Guid OperationId =
        Guid.Parse("11112222-3333-4444-8555-666677778888");

    // Owner-only, because the transition-slot proof opens the guarded root's parent and requires the
    // same strict posture the real installation root has. A world-readable scratch parent would make
    // the proof refuse for a reason the production layout never produces.
    private readonly string _root = CreateOwnerOnlyRoot();

    private readonly InMemoryOsCredentialStore _credentials = new();

    private static string CreateOwnerOnlyRoot()
    {

        string path = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-terminal-cont-{Guid.NewGuid():N}");

        SecureFilePermissions.CreateOwnerOnlyDirectoryAtPath(path);

        return path;

    }

    private string GuardedRoot => Path.Combine(_root, "arcanum");

    private string DatabaseFile => Path.Combine(GuardedRoot, "arcanum.db");

    [Fact]
    public void A_never_restored_installation_with_the_database_gone_verifies_absence_and_publishes_it()
    {

        Harness harness = Create();

        Result<FullInstallationResetTerminalOutcome> completed = harness.Complete();

        Assert.True(completed.IsSuccess, completed.Error.Message);

        Assert.Equal(
            InstallationResetRestoreCredentialCleanupPhase.TransitionCredentialsVerifiedAbsent,
            completed.Value.Phase);

        Assert.Equal(
            InstallationResetRestoreCredentialCleanupPhase.TransitionCredentialsVerifiedAbsent,
            harness.Store.Current.Payload.HostToolsMarkerPairReset!.RestoreCredentialCleanup);

    }

    [Fact]
    public void A_closed_anchor_removes_the_three_restore_credentials_in_order()
    {

        Harness harness = Create();

        harness.SeedClosedRestore();

        Result<FullInstallationResetTerminalOutcome> completed = harness.Complete();

        Assert.True(completed.IsSuccess, completed.Error.Message);

        Assert.Equal(
            [
                harness.Trio.AnchorAccount,
                harness.Trio.JournalKeyAccount,
                harness.Trio.InstallationAccount,
            ],
            harness.Deleted);

        Assert.All(
            new[]
            {
                harness.Trio.AnchorAccount,
                harness.Trio.JournalKeyAccount,
                harness.Trio.InstallationAccount,
            },
            account => Assert.Equal(
                OsCredentialStoreStatus.NotFound,
                _credentials.TryGet(ArcanumCredentialIdentity.Service, account).Status));

    }

    [Fact]
    public void A_database_that_still_exists_refuses_before_any_credential_is_read()
    {

        Harness harness = Create();

        harness.SeedClosedRestore();

        // Not "the cleanup told me it deleted it" — the file itself. This is the ordering the whole
        // slice rests on, and the only honest way to check it is to look.
        _ = Directory.CreateDirectory(GuardedRoot);

        File.WriteAllText(DatabaseFile, "still here");

        Result<FullInstallationResetTerminalOutcome> completed = harness.Complete();

        Assert.True(completed.IsFailure);

        Assert.Empty(harness.Deleted);

        Assert.Null(harness.Store.Current.Payload.HostToolsMarkerPairReset!.RestoreCredentialCleanup);

    }

    [Fact]
    public void A_managed_file_inventory_short_of_terminal_verification_refuses()
    {

        foreach (FullInstallationResetManagedFileReconciliationPhase phase in
                 Enum.GetValues<FullInstallationResetManagedFileReconciliationPhase>())
        {

            if (phase is FullInstallationResetManagedFileReconciliationPhase
                .TerminalInventoryVerified)
            {

                continue;

            }

            Harness harness = Create(managedFilePhase: phase);

            harness.SeedClosedRestore();

            Assert.True(harness.Complete().IsFailure);

            Assert.Empty(harness.Deleted);

        }

    }

    [Fact]
    public void A_record_with_no_managed_file_reconciliation_at_all_refuses()
    {

        Harness harness = Create(managedFilePhase: null);

        harness.SeedClosedRestore();

        Assert.True(harness.Complete().IsFailure);

        Assert.Empty(harness.Deleted);

    }

    [Fact]
    public void An_identity_a_full_reset_must_rotate_that_is_still_present_refuses()
    {

        Harness harness = Create();

        harness.SeedClosedRestore();

        // The Campaign root-identity key turns a physical directory into an opaque Campaign root
        // identity. Leaving it would hand the next installation the erased one's identities.
        _ = _credentials.Set(
            ArcanumCredentialIdentity.Service,
            ArcanumCredentialIdentity.CampaignRootIdentityKeyAccount,
            "survivor");

        Result<FullInstallationResetTerminalOutcome> completed = harness.Complete();

        Assert.True(completed.IsFailure);

        // Refused before the first irreversible step, not after the last: nothing was taken.
        Assert.Empty(harness.Deleted);

        Assert.Null(harness.Store.Current.Payload.HostToolsMarkerPairReset!.RestoreCredentialCleanup);

    }

    [Fact]
    public void A_stale_publication_refuses()
    {

        Harness harness = Create();

        InstallationResetActivePublication stale = harness.Store.Current;

        harness.Store.AdvanceOutOfBand();

        Assert.True(harness.Complete(stale).IsFailure);

        Assert.Empty(harness.Deleted);

    }

    [Fact]
    public void A_crash_mid_trio_resumes_from_the_persisted_projection_rather_than_wedging()
    {

        Harness harness = Create();

        harness.SeedClosedRestore();

        // Exactly the state a crash after the first removal leaves: the anchor gone, the other two
        // still there, and the record carrying the phase that was reached and the projection it was
        // proven against. Re-deriving the proof here would report "partially cleaned" and refuse
        // forever, which is the wedge this design exists to avoid.
        harness.RemoveAnchorOutOfBand();

        harness.SeedResumeAt(InstallationResetRestoreCredentialCleanupPhase.AnchorRemoved);

        Result<FullInstallationResetTerminalOutcome> completed = harness.Complete();

        Assert.True(completed.IsSuccess, completed.Error.Message);

        Assert.Equal(
            InstallationResetRestoreCredentialCleanupPhase.TransitionCredentialsVerifiedAbsent,
            completed.Value.Phase);

        // It resumed rather than restarted: the anchor was not deleted a second time, and the two
        // that were still there were compared against the persisted projection and taken in order.
        Assert.Equal(
            [harness.Trio.JournalKeyAccount, harness.Trio.InstallationAccount],
            harness.Deleted);

        // Exactly four publications: the two remaining trio phases, the trio's own verification, and
        // the whole cleanup's terminal phase. The transition slot was never opened here, so it adds
        // no projection and no removals. A resume that republished a phase it had already reached
        // would advance the authenticated envelope revision for no reason, and every proof bound to
        // the one it replaced would go stale with it.
        Assert.Equal(4, harness.Store.Advances);

    }

    [Fact]
    public void A_resume_still_compares_each_surviving_account_against_the_persisted_projection()
    {

        Harness harness = Create();

        harness.SeedClosedRestore();

        harness.RemoveAnchorOutOfBand();

        harness.SeedResumeAt(InstallationResetRestoreCredentialCleanupPhase.AnchorRemoved);

        // Something wrote to a surviving slot after the proof was made. The persisted projection is
        // what makes that detectable across a restart, and it must still refuse.
        harness.OverwriteJournalKey("written after the proof");

        Result<FullInstallationResetTerminalOutcome> completed = harness.Complete();

        Assert.True(completed.IsFailure);

        Assert.Empty(harness.Deleted);

    }

    [Fact]
    public void A_record_already_verified_absent_is_idempotent_and_removes_nothing_twice()
    {

        Harness harness = Create();

        harness.SeedClosedRestore();

        Assert.True(harness.Complete().IsSuccess);

        int deletesAfterFirst = harness.Deleted.Count;

        Result<FullInstallationResetTerminalOutcome> second = harness.Complete();

        Assert.True(second.IsSuccess, second.Error.Message);

        Assert.Equal(
            InstallationResetRestoreCredentialCleanupPhase.TransitionCredentialsVerifiedAbsent,
            second.Value.Phase);

        Assert.Equal(deletesAfterFirst, harness.Deleted.Count);

    }

    [Fact]
    public void A_closed_transition_slot_is_compare_removed_anchor_then_key_after_the_restore_trio()
    {

        // The last thing a full reset takes. Both accounts survive every other cleanup in the product
        // precisely because they are the only evidence that could finish an interrupted transition, so
        // the ordering matters twice: after the trio, and anchor before key within the pair.
        Harness harness = Create();

        harness.SeedClosedRestore();

        harness.SeedClosedTransitionSlot();

        harness.SeedNestedReceipt(InstallationResetNestedTransitionPhase.Completed);

        Result<FullInstallationResetTerminalOutcome> completed = harness.Complete();

        Assert.True(completed.IsSuccess, completed.IsFailure ? completed.Error.Message : null);

        Assert.Equal(
            InstallationResetRestoreCredentialCleanupPhase.TransitionCredentialsVerifiedAbsent,
            completed.Value.Phase);

        Assert.Equal(
            [
                harness.Trio.AnchorAccount,
                harness.Trio.JournalKeyAccount,
                harness.Trio.InstallationAccount,
                harness.TransitionAccounts.AnchorAccount,
                harness.TransitionAccounts.KeyAccount,
            ],
            harness.Deleted);

    }

    [Fact]
    public void A_nested_transition_that_never_reported_blocks_the_removal_it_could_still_need()
    {

        // A claim with no completion is a reset that started a database transition and never heard
        // how it ended. Taking the pair here would destroy the only credentials that could finish it,
        // so the reset stops with everything still recoverable.
        Harness harness = Create();

        harness.SeedClosedRestore();

        harness.SeedClosedTransitionSlot();

        harness.SeedNestedReceipt(InstallationResetNestedTransitionPhase.Claimed);

        Result<FullInstallationResetTerminalOutcome> completed = harness.Complete();

        Assert.True(completed.IsFailure);

        Assert.DoesNotContain(harness.TransitionAccounts.AnchorAccount, harness.Deleted);

        Assert.DoesNotContain(harness.TransitionAccounts.KeyAccount, harness.Deleted);

    }

    public void Dispose()
    {

        try
        {

            Directory.Delete(_root, recursive: true);

        }
        catch (IOException)
        {

            // A scratch directory under the OS temp root; a failure to remove it is not an outcome.
        }

    }

    private Harness Create(
        FullInstallationResetManagedFileReconciliationPhase? managedFilePhase =
            FullInstallationResetManagedFileReconciliationPhase.TerminalInventoryVerified) =>
        new(this, managedFilePhase);

    /// <summary>
    /// A recording credential store that keeps the deletion order, over the real in-memory store.
    /// </summary>
    private sealed class OrderRecordingCredentialStore(InMemoryOsCredentialStore inner)
        : IOsCredentialStore
    {

        internal List<string> Deleted { get; } = [];

        public bool IsAvailable => inner.IsAvailable;

        public OsCredentialStoreResult TryGet(string service, string account) =>
            inner.TryGet(service, account);

        public OsCredentialStoreResult Set(string service, string account, string secret) =>
            inner.Set(service, account, secret);

        public OsCredentialStoreResult Delete(string service, string account)
        {

            Deleted.Add(account);

            return inner.Delete(service, account);

        }

    }

    private sealed class Harness
    {

        private readonly FullInstallationResetTerminalContinuationTests _owner;

        private readonly OrderRecordingCredentialStore _recording;

        internal Harness(
            FullInstallationResetTerminalContinuationTests owner,
            FullInstallationResetManagedFileReconciliationPhase? managedFilePhase)
        {

            _owner = owner;

            _recording = new OrderRecordingCredentialStore(owner._credentials);

            _ = Directory.CreateDirectory(owner.GuardedRoot);

            Store = new TerminalActiveStore(
                owner.GuardedRoot,
                Publication(managedFilePhase));

            Result<BackupRestoreProfileNamespace> profile =
                BackupRestoreJournalAuthenticator.ResolveProfileNamespace(owner.GuardedRoot);

            Assert.True(profile.IsSuccess, profile.Error.Message);

            Trio = InstallationResetRestoreCredentialCleanup.Derive(profile.Value.Digest);

            Subject = new FullInstallationResetTerminalContinuation(
                Store,
                new BackupRestoreJournalAnchorStore(
                    _recording,
                    new BackupRestoreJournalKeyProvider(_recording),
                    new BackupRestoreJournalInstallationIdentityProvider(_recording)),
                new InstallationResetRestoreCredentialCleanup(_recording),
                _recording,
                new GrimoireOfflineTransitionJournalAnchorStore(_recording),
                owner.DatabaseFile);

        }

        internal TerminalActiveStore Store { get; }

        internal FullInstallationResetTerminalContinuation Subject { get; }

        internal InstallationResetRestoreCredentialTrio Trio { get; }

        internal IReadOnlyList<string> Deleted => _recording.Deleted;

        internal int Advances => Store.Advances;

        internal GrimoireOfflineTransitionJournalLocation TransitionLocation =>
            new GrimoireOfflineTransitionJournalFileStore()
                .ResolveLocation(_owner.GuardedRoot).Value;

        internal (string AnchorAccount, string KeyAccount) TransitionAccounts =>
            GrimoireOfflineTransitionJournalAnchorStore.TerminalAccounts(
                TransitionLocation.ProfileNamespace);

        /// <summary>Writes the credential pair a closed offline-transition slot leaves behind.</summary>
        internal void SeedClosedTransitionSlot()
        {

            GrimoireOfflineTransitionAnchorV1 anchor = new(
                Version: 1,
                TransitionLocation.ProfileNamespace.Digest,
                InstallationId,
                SlotEpoch: 3,
                GrimoireOfflineTransitionAnchorState.Closed,
                OperationId: Guid.Parse("aaaabbbb-cccc-4ddd-8eee-ffff00001111"),
                Kind: GrimoireOfflineTransitionKind.HealthyCatalogFactoryErasure,
                PayloadVersion: 1,
                Revision: 6,
                new CovenantDigest([.. Enumerable.Repeat((byte)0x66, 32)]),
                TransitionLocation.JournalLocationDigest);

            Result<string> encoded =
                GrimoireOfflineTransitionJournalAuthenticator.EncodeAnchor(anchor);

            Assert.True(encoded.IsSuccess, encoded.IsFailure ? encoded.Error.Message : null);

            _ = _owner._credentials.Set(
                ArcanumCredentialIdentity.Service,
                TransitionAccounts.AnchorAccount,
                encoded.Value);

            _ = _owner._credentials.Set(
                ArcanumCredentialIdentity.Service,
                TransitionAccounts.KeyAccount,
                Convert.ToBase64String([.. Enumerable.Repeat((byte)0x77, 32)]));

        }

        /// <summary>Records a nested transition the reset launched and how far it reported.</summary>
        internal void SeedNestedReceipt(InstallationResetNestedTransitionPhase phase) =>
            Store.SeedPayload(payload => payload with
            {
                NestedTransitionReceipt = new InstallationResetNestedTransitionReceiptV1(
                    Version: 1,
                    NestedOperationId: Guid.Parse("bbbbcccc-dddd-4eee-8fff-000011112222"),
                    phase,
                    phase is InstallationResetNestedTransitionPhase.Completed
                        ? new CovenantDigest([.. Enumerable.Repeat((byte)0x88, 32)])
                        : null,
                    phase is InstallationResetNestedTransitionPhase.Completed
                        ? new CovenantDigest([.. Enumerable.Repeat((byte)0x99, 32)])
                        : null),
            });

        /// <summary>
        /// Removes the anchor without recording anything, exactly as a crash mid-removal leaves it.
        /// </summary>
        internal void RemoveAnchorOutOfBand() =>
            _ = _owner._credentials.Delete(ArcanumCredentialIdentity.Service, Trio.AnchorAccount);

        internal void OverwriteJournalKey(string value) =>
            _ = _owner._credentials.Set(
                ArcanumCredentialIdentity.Service,
                Trio.JournalKeyAccount,
                value);

        /// <summary>
        /// Seeds the durable record a resumed removal would find: the phase reached, and the
        /// projection it was proven against while all three accounts were still there.
        /// </summary>
        internal void SeedResumeAt(InstallationResetRestoreCredentialCleanupPhase phase)
        {

            BackupRestoreFullResetTerminalProjectionV1 terminal = new(
                Version: 1,
                BackupRestoreFullResetTerminalArm.ClosedAnchor,
                Profile().Digest,
                InstallationId,
                ClosedOperationId: Guid.Parse("99998888-7777-4666-8555-444433332222"),
                ClosedRevision: 4,
                new CovenantDigest([.. Enumerable.Repeat((byte)0x21, 32)]),
                new CovenantDigest([.. Enumerable.Repeat((byte)0x22, 32)]),
                BackupRestoreJournalAnchorStore.AccountValueDigest(
                    Trio.InstallationAccount,
                    InstallationId.ToString("D")),
                BackupRestoreJournalAnchorStore.AccountValueDigest(
                    Trio.JournalKeyAccount,
                    Convert.ToBase64String([.. Enumerable.Repeat((byte)0x33, 32)])),
                new CovenantDigest([.. Enumerable.Repeat((byte)0x44, 32)]),
                new CovenantDigest([.. Enumerable.Repeat((byte)0x55, 32)]));

            Store.Seed(marker => marker with
            {
                RestoreTerminal = terminal,
                RestoreCredentialCleanup = phase,
            });

        }

        internal Result<FullInstallationResetTerminalOutcome> Complete(
            InstallationResetActivePublication? publication = null)
        {

            using ArcanumMaintenanceLock held = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(_owner.GuardedRoot));

            return Subject
                .CompleteAsync(held, publication ?? Store.Current, Token)
                .GetAwaiter()
                .GetResult();

        }

        /// <summary>
        /// Writes the credential set an installation whose last restore closed leaves behind.
        /// </summary>
        internal void SeedClosedRestore()
        {

            BackupRestoreJournalAnchorV1 anchor = new(
                Version: 1,
                Profile().Digest,
                InstallationId,
                Guid.Parse("99998888-7777-4666-8555-444433332222"),
                Revision: 4,
                new CovenantDigest([.. Enumerable.Repeat((byte)0x21, 32)]),
                new CovenantDigest([.. Enumerable.Repeat((byte)0x22, 32)]),
                BackupRestoreJournalAnchorState.Closed);

            Result<string> encoded = BackupRestoreJournalAuthenticator.EncodeAnchor(anchor);

            Assert.True(encoded.IsSuccess, encoded.Error.Message);

            _ = _owner._credentials.Set(
                ArcanumCredentialIdentity.Service,
                Trio.AnchorAccount,
                encoded.Value);

            _ = _owner._credentials.Set(
                ArcanumCredentialIdentity.Service,
                Trio.JournalKeyAccount,
                Convert.ToBase64String([.. Enumerable.Repeat((byte)0x33, 32)]));

            _ = _owner._credentials.Set(
                ArcanumCredentialIdentity.Service,
                Trio.InstallationAccount,
                InstallationId.ToString("D"));

        }

        private BackupRestoreProfileNamespace Profile() =>
            BackupRestoreJournalAuthenticator.ResolveProfileNamespace(_owner.GuardedRoot).Value;

        private static CovenantDigest Digest(byte value) =>
            new([.. Enumerable.Repeat(value, 32)]);

        private static InstallationResetActivePublication Publication(
            FullInstallationResetManagedFileReconciliationPhase? managedFilePhase)
        {

            DateTimeOffset acceptedAtUtc = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

            FullInstallationResetExternalRemediationAttestation attestation = new(
                Version: 1,
                OperationId,
                InstallationId,
                HostToolsTransitionId: Guid.Parse("11111111-2222-4333-8444-555555555555"),
                TaintMasterKeyVersion: 7,
                AuthorityFingerprint: Digest(0x5A),
                DatabaseMarkerDigest: Digest(0x5B),
                OsMarkerDigest: Digest(0x5C),
                RemediationActionDigest: Digest(0x5D),
                NonceBase64Url: "nonce",
                Issuer: "issuer",
                IssuedAtUtc: acceptedAtUtc,
                ExpiresAtUtc: acceptedAtUtc.AddHours(1),
                SignatureBase64Url: "signature");

            ImmutableArray<Guid> empty = [];

            FullInstallationResetManagedFileCheckpointV1? managedFile =
                managedFilePhase is not { } phase
                    ? null
                    : new FullInstallationResetManagedFileCheckpointV1(
                        Version: 1,
                        phase,
                        SourceCount: 0,
                        empty,
                        FullInstallationResetManagedFileDigests
                            .SourceWriteIntentVector(empty)
                            .Value,
                        LocalErasureWorkItemCount: null,
                        OrderedLocalErasureWorkItemIds: null,
                        LocalErasureWorkItemVectorDigest: null,
                        SafeTerminalWriteIntentCount: null,
                        ManualWriteOrphanCount: null,
                        CompletedWorkItemCount: null,
                        ManualWorkItemOrphanCount: null,
                        TerminalClassificationDigest: null);

            HostToolsMarkerPairResetCheckpointV1 checkpoint = new(
                Version: 1,
                HostToolsMarkerPairResetPhase.PairAbsenceVerified,
                new FullInstallationResetRestartProofV1(
                    Version: 1,
                    FullInstallationResetSignedAttestationProjectionV1.FromAttestation(attestation),
                    acceptedAtUtc,
                    Digest(0x60),
                    new HostProcessToolsDatabaseMarkerEvidence(
                        InstallationId.ToString(),
                        RetroDownfall.Arcanum.Core.Security.CovenantHostToolsState.HostToolsTainted,
                        attestation.HostToolsTransitionId,
                        attestation.TaintMasterKeyVersion,
                        attestation.AuthorityFingerprint),
                    new HostProcessToolsOsMarkerEvidence(
                        InstallationId.ToString(),
                        attestation.HostToolsTransitionId,
                        attestation.TaintMasterKeyVersion,
                        attestation.AuthorityFingerprint,
                        attestation.OsMarkerDigest,
                        Digest(0x5E)),
                    Digest(0x61)),
                CampaignInventory: [],
                Digest(0x62),
                Digest(0x63),
                MarkerIntentCount: 0,
                empty,
                FullInstallationResetMarkerPairResetDigests.FullResetIntentVector(empty).Value,
                DeletedCount: 0,
                OrphanCount: 0,
                managedFile);

            InstallationResetActiveRecord record = new(
                InstallationResetActiveStore.CurrentVersion,
                OperationId,
                "full-reset-plan",
                InstallationResetScope.All,
                Workspace: null,
                new InstallationResetAcceptedBinding("binding", [], [], [], [], []),
                InstallationResetPhase.OfflineCleanupComplete,
                PointOfNoReturn: true,
                RowsDeleted: 0,
                FilesDeleted: 1,
                EstimatedBytesDeleted: 0,
                CredentialResults: [],
                LastErrorCode: ErrorCodes.Data.RecoveryRequired,
                FullInstallationResetRemediationClaim: new FullInstallationResetRemediationClaimV1(
                    1,
                    OperationId,
                    InstallationId,
                    Digest(0x60),
                    Digest(0x45),
                    Digest(0x46),
                    acceptedAtUtc),
                HostToolsMarkerPairReset: checkpoint);

            InstallationResetActiveLocation location = new(
                "/active",
                Digest(0x10),
                Digest(0x11),
                "reset.active",
                Digest(0x12));

            CovenantDigest envelopeDigest = Digest(0x14);

            return new InstallationResetActivePublication(
                location,
                new InstallationResetActiveEnvelopeV2(
                    2,
                    location.ProfileNamespaceDigest,
                    InstallationId,
                    OperationId,
                    1,
                    Digest(0x13),
                    location.Digest,
                    InstallationResetScope.All,
                    record.PlanId,
                    "nonce",
                    "ciphertext",
                    "tag"),
                envelopeDigest,
                InstallationResetActivePayloadV3.FromRecord(record),
                new InstallationResetActiveAnchorV1(
                    1,
                    InstallationResetActiveAnchorState.Active,
                    location.ProfileNamespaceDigest,
                    InstallationId,
                    OperationId,
                    1,
                    envelopeDigest,
                    location.Digest));

        }

    }

    /// <summary>
    /// A durable active store that advances the envelope revision on every publication.
    /// </summary>
    internal sealed class TerminalActiveStore(
        string guardedRoot,
        InstallationResetActivePublication initial) : IInstallationResetActiveStore
    {

        public string GuardedRoot { get; } = guardedRoot;

        internal InstallationResetActivePublication Current { get; private set; } = initial;

        /// <summary>How many times this store advanced, which is how many phases were published.</summary>
        internal int Advances { get; private set; }

        internal void AdvanceOutOfBand() => Current = Bump(Current, Current.Payload);

        /// <summary>Rewrites the durable marker checkpoint without advancing the revision.</summary>
        internal void SeedPayload(
            Func<InstallationResetActiveRecord, InstallationResetActiveRecord> rewrite) =>
            Current = Current with
            {
                Payload = InstallationResetActivePayloadV3.FromRecord(
                    rewrite(Current.Payload.ToRecord())),
            };

        internal void Seed(
            Func<HostToolsMarkerPairResetCheckpointV1, HostToolsMarkerPairResetCheckpointV1> rewrite)
        {

            Current = Current with
            {
                Payload = InstallationResetActivePayloadV3.FromRecord(
                    Current.Payload.ToRecord() with
                    {
                        HostToolsMarkerPairReset =
                            rewrite(Current.Payload.HostToolsMarkerPairReset!),
                    }),
            };

        }

        public Task<Result<InstallationResetActiveRecoveryState>> RecoverAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            CancellationToken cancellationToken = default)
        {

            ArgumentNullException.ThrowIfNull(heldInstallationLock);

            heldInstallationLock.AssertHeldFor(GuardedRoot);

            return Task.FromResult(
                Result<InstallationResetActiveRecoveryState>.Success(
                    new InstallationResetActiveRecoveryState(
                        InstallationResetActiveRecoveryOutcome.AuthenticatedV2,
                        Current,
                        LegacyRecord: null)));

        }

        public Task<Result<InstallationResetActivePublication>> AdvanceAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            InstallationResetActivePublication current,
            InstallationResetActiveRecord next,
            CancellationToken cancellationToken = default)
        {

            ArgumentNullException.ThrowIfNull(heldInstallationLock);

            ArgumentNullException.ThrowIfNull(next);

            heldInstallationLock.AssertHeldFor(GuardedRoot);

            if (current.EnvelopeDigest != Current.EnvelopeDigest)
            {

                return Task.FromResult(
                    Result<InstallationResetActivePublication>.Failure(
                        new Error(
                            ErrorCodes.Data.RecoveryRequired,
                            "The publication is not the current one.")));

            }

            Advances++;

            Current = Bump(Current, InstallationResetActivePayloadV3.FromRecord(next));

            return Task.FromResult(
                Result<InstallationResetActivePublication>.Success(Current));

        }

        public Task<Result<InstallationResetActivePublication>> BeginAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            Guid installationId,
            InstallationResetActiveRecord record,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<InstallationResetActiveRecoveryState>> InspectAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<InstallationResetActivePublication>> MigrateLegacyV1Async(
            ArcanumMaintenanceLock heldInstallationLock,
            Guid installationId,
            InstallationResetActiveRecord expectedRecord,
            FileHandleIdentity expectedIdentity,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result> RetireAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            Guid operationId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result> CompleteStartupCleanupAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        private static InstallationResetActivePublication Bump(
            InstallationResetActivePublication current,
            InstallationResetActivePayloadV3 payload)
        {

            CovenantDigest envelopeDigest = new(
                [.. Enumerable.Repeat(
                    checked((byte)(0x20 + current.Envelope.Revision)),
                    32)]);

            return current with
            {
                Envelope = current.Envelope with
                {
                    Revision = current.Envelope.Revision + 1,
                    PreviousEnvelopeDigest = current.EnvelopeDigest,
                },
                EnvelopeDigest = envelopeDigest,
                Payload = payload,
                Anchor = current.Anchor with
                {
                    Revision = current.Anchor.Revision + 1,
                    EnvelopeDigest = envelopeDigest,
                },
            };

        }

    }

}
